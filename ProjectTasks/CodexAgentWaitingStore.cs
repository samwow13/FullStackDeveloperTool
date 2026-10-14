using System.IO;
using System.Text.Json.Serialization;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexAgentWaitingItem
{
    // Version 3 also freezes Fast and Ultrafast for later explicit dispatch.
    public int Version { get; init; } = 3;
    public string Id { get; init; } = "";
    public CodexAgentChatRequest Request { get; init; } = new("", "", "", "", "", "", "");
    public DateTimeOffset CreatedAt { get; init; }
    public string? AttemptId { get; init; }
    public string Summary { get; init; } = "Waiting for Go.";
    [JsonIgnore]
    public bool IsWaiting => AttemptId is null;
}

/// <summary>Stores independent manual prompts without preparing or starting a Codex chat.</summary>
public sealed partial class CodexAgentWaitingStore
{
    private const int MaximumItemBytes = 64_000_000;
    private const int MaximumItems = 100;
    private readonly string _directory = CodexAgentStorage.ResolvePath("codex-agent-waiting");
    private readonly CodexAgentChatStore _chats = new();
    private readonly CodexAgentChatClient _client = new();

    public CodexAgentWaitingItem Save(CodexAgentChatRequest request)
    {
        var frozen = CodexAgentChatStore.FreezeRequest(request);
        CodexAgentChatStore.ValidateRequest(frozen);
        frozen = frozen with { Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frozen.Folder)) };
        if (!Directory.Exists(frozen.Folder))
            throw new ArgumentException("The project folder no longer exists. Open its settings and choose an existing folder.");
        Directory.CreateDirectory(_directory);
        using var gate = CodexAgentStorage.AcquireLock(Path.Combine(_directory, "waiting.lock"));
        // List removes only bookkeeping whose immutable receipt confirms acceptance
        // or a terminal result. Uncertain requests continue to count toward the limit.
        _ = List();
        if (ItemFiles().Take(MaximumItems).Count() >= MaximumItems)
            throw new InvalidOperationException("At most 100 waiting or unresolved prompts can be saved. Start or review saved prompts before adding more.");
        var item = new CodexAgentWaitingItem
        {
            Id = Guid.NewGuid().ToString("N"), Request = frozen, CreatedAt = DateTimeOffset.UtcNow
        };
        var path = ItemPath(item.Id);
        var bytes = Serialize(item);
        CodexAgentStorage.WriteAtomic(path, bytes);
        return item;
    }

    public IReadOnlyList<CodexAgentWaitingItem> List()
    {
        if (!Directory.Exists(_directory)) return [];
        var paths = ItemFiles().Take(MaximumItems + 1).ToArray();
        if (paths.Length > MaximumItems)
            throw new InvalidOperationException("Saved waiting prompts exceed their supported count. Existing files were preserved.");
        var items = new List<CodexAgentWaitingItem>();
        foreach (var path in paths)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var item = Read(id, required: false);
            if (item is null) continue;
            if (item.AttemptId is { } attemptId)
            {
                try
                {
                    var receipt = _client.ReadReceipt(attemptId);
                    ValidateReceipt(item, receipt);
                    if (receipt.IsAccepted || receipt.TerminalConfirmed)
                    {
                        if (RemoveSettled(item)) continue;
                    }
                    item = item with { Summary = receipt.Summary };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    item = item with
                    {
                        Summary = LimitSummary(item.Summary + " Submission status needs review. " + SensitiveDataProtection.Redact(ex.Message))
                    };
                }
            }
            items.Add(item);
        }
        return items.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public void Remove(string id)
    {
        using var queueGate = AcquireQueueGate();
        RequireNotQueued(id);
        using var gate = CodexAgentStorage.AcquireLock(ItemPath(id) + ".lock");
        var item = Read(id, required: true)!;
        if (!item.IsWaiting)
            throw new InvalidOperationException("This prompt was already released. Review its saved submission before preparing another chat.");
        DeleteBookkeeping(item.Id);
    }

    /// <summary>Called only after explicit human confirmation; hides a released entry without changing its receipt or resending it.</summary>
    public void Dismiss(string id)
    {
        using var queueGate = AcquireQueueGate();
        RequireNotQueued(id);
        using var gate = CodexAgentStorage.AcquireLock(ItemPath(id) + ".lock");
        var item = Read(id, required: true)!;
        if (item.IsWaiting)
            throw new InvalidOperationException("This prompt is still waiting. Remove it using the waiting-prompt action instead.");
        // A crash between reservation and preparation may leave no chat receipt.
        // Preserve the full frozen request and original attempt identity separately
        // before removing visible bookkeeping. Existing receipts remain untouched.
        CodexAgentStorage.WriteAtomic(ItemPath(id) + ".dismissed", Serialize(item));
        DeleteBookkeeping(item.Id);
    }

    /// <summary>Explicitly releases one waiting prompt. A repeated call never launches another owner.</summary>
    public Task<CodexAgentChatReceipt> ReleaseAsync(string id, CancellationToken cancellationToken = default) =>
        ReleaseCoreAsync(id, null, cancellationToken);

    internal Task<CodexAgentChatReceipt> ReleaseQueuedAsync(CodexAgentWaitingQueue queue) =>
        ReleaseCoreAsync(queue.Current.WaitingId, queue, CancellationToken.None);

    private async Task<CodexAgentChatReceipt> ReleaseCoreAsync(string id, CodexAgentWaitingQueue? queue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = await Task.Run(() => Reserve(id, queue), cancellationToken).ConfigureAwait(false);
        var item = reservation.Item;
        if (!reservation.NewlyReserved)
        {
            var existing = _client.ReadReceipt(item.AttemptId!);
            ValidateReceipt(item, existing);
            return existing;
        }
        try
        {
            // The item already contains its durable attempt identity. Complete local
            // preparation even if cancellation arrives, so recovery can inspect it.
            await Task.Run(() => _chats.Prepare(item.Request, item.AttemptId!)).ConfigureAwait(false);
            var receipt = await _client.StartPreparedAsync(item.AttemptId!, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(item, receipt);
            try { RememberResult(item, receipt); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Receipt acceptance remains valid even when waiting-list cleanup
                // fails. Never report a failed launch after Codex accepted it.
                receipt = receipt with
                {
                    Summary = LimitSummary(receipt.Summary + " Waiting-list cleanup could not finish. Refresh to inspect the saved receipt. " +
                        SensitiveDataProtection.Redact(ex.Message))
                };
            }
            return receipt;
        }
        catch (Exception ex)
        {
            RememberFailure(item, ex);
            throw;
        }
    }

    private (CodexAgentWaitingItem Item, bool NewlyReserved) Reserve(string id, CodexAgentWaitingQueue? queue)
    {
        using var queueGate = AcquireQueueGate();
        if (queue is null) RequireNotQueued(id);
        else
        {
            RequireCurrentQueue(queue);
            if (!queue.DispatchStarted || queue.Current.WaitingId != id)
                throw new InvalidOperationException("The queue has not reserved this prompt for dispatch.");
        }
        using var gate = CodexAgentStorage.AcquireLock(ItemPath(id) + ".lock");
        var item = Read(id, required: true)!;
        if (queue is not null) ValidateQueueItem(queue.Current, item);
        if (!item.IsWaiting) return (item, false);
        item = item with { AttemptId = queue?.Current.AttemptId ?? Guid.NewGuid().ToString("N"),
            Summary = "Go requested. Checking the saved prompt before starting." };
        // Save intent before receipt creation or process launch. A crash never
        // returns this prompt to waiting or creates a replacement attempt.
        CodexAgentStorage.WriteAtomic(ItemPath(id), Serialize(item));
        return (item, true);
    }

    private void RememberResult(CodexAgentWaitingItem item, CodexAgentChatReceipt receipt)
    {
        using var queueGate = AcquireQueueGate();
        using var gate = CodexAgentStorage.AcquireLock(ItemPath(item.Id) + ".lock");
        var current = Read(item.Id, required: false);
        if (current is null) return;
        if (current.AttemptId != item.AttemptId)
            throw new InvalidOperationException("The waiting prompt's submission identity changed. Its saved request was preserved.");
        if ((receipt.IsAccepted || receipt.TerminalConfirmed) && !QueueOwns(item.Id)) DeleteBookkeeping(item.Id);
        else CodexAgentStorage.WriteAtomic(ItemPath(item.Id), Serialize(current with { Summary = receipt.Summary }));
    }

    private void RememberFailure(CodexAgentWaitingItem item, Exception failure)
    {
        try
        {
            using var gate = CodexAgentStorage.AcquireLock(ItemPath(item.Id) + ".lock");
            var current = Read(item.Id, required: false);
            if (current is null || current.AttemptId != item.AttemptId) return;
            CodexAgentStorage.WriteAtomic(ItemPath(item.Id), Serialize(current with
            {
                Summary = LimitSummary("Agent could not start. Saved request remains released for review. " +
                    SensitiveDataProtection.Redact(failure.Message))
            }));
        }
        catch (Exception) { /* The durable attempt identity remains authoritative. */ }
    }

    private bool RemoveSettled(CodexAgentWaitingItem item)
    {
        try
        {
            using var queueGate = AcquireQueueGate();
            if (QueueOwns(item.Id)) return false;
            using var gate = CodexAgentStorage.AcquireLock(ItemPath(item.Id) + ".lock");
            var current = Read(item.Id, required: false);
            if (current is null) return true;
            if (current.AttemptId != item.AttemptId) return false;
            var receipt = _client.ReadReceipt(item.AttemptId!);
            ValidateReceipt(item, receipt);
            if (receipt.IsAccepted || receipt.TerminalConfirmed)
            {
                DeleteBookkeeping(item.Id);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // A busy release or failed cleanup does not change receipt evidence.
            // The next explicit list read can remove this settled bookkeeping.
        }
        return false;
    }

    private void ValidateReceipt(CodexAgentWaitingItem item, CodexAgentChatReceipt receipt)
    {
        if (receipt.AttemptId != item.AttemptId ||
            !CodexAgentStorage.Serialize(receipt.Request).AsSpan().SequenceEqual(CodexAgentStorage.Serialize(item.Request)))
            throw new InvalidOperationException("The saved submission does not match this waiting prompt. Both records were preserved.");
    }

    private CodexAgentWaitingItem? Read(string id, bool required)
    {
        var bytes = CodexAgentStorage.Read(ItemPath(id), MaximumItemBytes);
        if (bytes is null)
        {
            if (required) throw new InvalidOperationException("The saved waiting prompt is unavailable. Refresh waiting prompts before continuing.");
            return null;
        }
        var item = CodexAgentStorage.Deserialize<CodexAgentWaitingItem>(bytes);
        Validate(item);
        if (item.Id != id)
            throw new InvalidOperationException("The saved waiting prompt identity is invalid. The existing file was preserved.");
        return item with { Request = CodexAgentChatStore.FreezeRequest(item.Request) };
    }

    private static byte[] Serialize(CodexAgentWaitingItem item)
    {
        Validate(item);
        var bytes = CodexAgentStorage.Serialize(item);
        if (bytes.Length > MaximumItemBytes)
            throw new InvalidOperationException("The saved waiting prompt exceeds its supported size. Remove an image or shorten the message.");
        return bytes;
    }

    private static void Validate(CodexAgentWaitingItem item)
    {
        if (item.Version is not (1 or 2 or 3) || !Guid.TryParseExact(item.Id, "N", out _) || item.CreatedAt == default ||
            item.AttemptId is { } attemptId && !Guid.TryParseExact(attemptId, "N", out _) ||
            item.Summary is null || item.Summary.Length > 4_000)
            throw new InvalidOperationException("The saved waiting prompt is invalid or unsupported. The existing file was preserved.");
        CodexAgentChatStore.ValidateRequest(item.Request);
        if (item.Version < 2 && item.Request.Images.Any(image => image.CaptureContext is not null))
            throw new InvalidOperationException("The saved waiting prompt version cannot preserve its desktop capture context. The existing file was preserved.");
        if (item.Version < 3 && (item.Request.UltraFastEnabled || item.Request.FastEnabled))
            throw new InvalidOperationException("The saved waiting prompt version cannot preserve its speed settings. The existing file was preserved.");
    }

    private IEnumerable<string> ItemFiles() => Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
        .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _));

    private string ItemPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("The waiting prompt identifier is invalid.");
        return Path.Combine(_directory, id + ".json");
    }

    private void DeleteBookkeeping(string id)
    {
        var path = ItemPath(id);
        File.Delete(path + ".bak");
        File.Delete(path);
    }

    private static string LimitSummary(string summary) => summary.Length <= 4_000 ? summary : summary[..4_000];
}
