using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

internal enum CodexAgentWaitingQueueState { Active, NeedsReview, Stopped, Completed }

internal sealed record CodexAgentWaitingQueueEntry(string WaitingId, string AttemptId,
    string ProjectId, string ProjectName, string Folder, string RequestHash);

internal sealed record CodexAgentWaitingQueue
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public long Revision { get; init; }
    [JsonRequired] public CodexAgentWaitingQueueState State { get; init; }
    [JsonRequired] public IReadOnlyList<CodexAgentWaitingQueueEntry> Entries { get; init; } = [];
    [JsonRequired] public int Index { get; init; }
    [JsonRequired] public bool DispatchStarted { get; init; }
    public string Summary { get; init; } = "";
    [JsonRequired] public DateTimeOffset CreatedAt { get; init; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; init; }
    [JsonIgnore] public bool OwnsPrompts => State is CodexAgentWaitingQueueState.Active or CodexAgentWaitingQueueState.NeedsReview;
    [JsonIgnore] public CodexAgentWaitingQueueEntry Current => Entries[Index];
}

public sealed partial class CodexAgentWaitingStore
{
    private string QueuePath => Path.Combine(_directory, "queue.json");

    private FileStream AcquireQueueGate()
    {
        Directory.CreateDirectory(_directory);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(_directory, "queue.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException("The saved queue is busy. Wait briefly and try again.");
                Thread.Sleep(25);
            }
        }
    }

    internal FileStream ClaimQueueOwner()
    {
        Directory.CreateDirectory(_directory);
        return CodexAgentStorage.AcquireLock(Path.Combine(_directory, "queue.owner.lock"));
    }

    internal CodexAgentWaitingQueue? ReadQueue()
    {
        var bytes = CodexAgentStorage.Read(QueuePath, 1_000_000);
        if (bytes is null)
        {
            // Never resurrect a previous active revision after Stop or completion.
            if (File.Exists(QueuePath + ".bak"))
                throw new InvalidOperationException("The saved queue is missing its current record. Its backup was preserved for review; no agents were started.");
            return null;
        }
        var queue = CodexAgentStorage.Deserialize<CodexAgentWaitingQueue>(bytes);
        ValidateQueue(queue);
        return queue;
    }

    internal CodexAgentWaitingQueue StartQueue(IReadOnlyList<CodexAgentWaitingItem> items)
    {
        using var gate = AcquireQueueGate();
        if (ReadQueue()?.OwnsPrompts == true)
            throw new InvalidOperationException("A saved queue already owns these prompts. Stop or review it before starting another batch.");
        if (items.Count is < 1 or > MaximumItems)
            throw new InvalidOperationException("Select between 1 and 100 waiting prompts for this queue.");
        var entries = items.Select(item => new CodexAgentWaitingQueueEntry(item.Id, Guid.NewGuid().ToString("N"),
            item.Request.ProjectId, item.Request.ProjectName, item.Request.Folder, RequestHash(item.Request))).ToArray();
        foreach (var entry in entries)
        {
            var saved = Read(entry.WaitingId, required: true)!;
            ValidateQueueItem(entry, saved);
            if (!saved.IsWaiting) throw new InvalidOperationException("A selected prompt was already released. Refresh waiting prompts before continuing.");
        }
        var queue = new CodexAgentWaitingQueue
        {
            Id = Guid.NewGuid().ToString("N"), Revision = 1, Entries = entries,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            State = CodexAgentWaitingQueueState.Active
        };
        SaveQueue(queue);
        return queue;
    }

    internal CodexAgentWaitingItem ReadQueueItem(CodexAgentWaitingQueue queue)
    {
        var item = Read(queue.Current.WaitingId, required: true)!;
        ValidateQueueItem(queue.Current, item);
        return item;
    }

    internal CodexAgentWaitingQueue BeginQueueDispatch(CodexAgentWaitingQueue expected)
    {
        using var gate = AcquireQueueGate();
        var queue = RequireCurrentQueue(expected);
        if (queue.DispatchStarted)
            throw new InvalidOperationException("This queue attempt was already released. Only its existing receipt may be inspected.");
        if (!ReadQueueItem(queue).IsWaiting)
            throw new InvalidOperationException("The next queue prompt was already released outside this batch. Review its submission before continuing.");
        // Persist the exact attempt BEFORE reservation, receipt creation, or process
        // launch. Recovery only observes this identity and never dispatches it again.
        return SaveNextQueue(queue with { DispatchStarted = true });
    }

    internal bool IsQueueAgentFinished(CodexAgentWaitingQueue queue)
    {
        if (!queue.DispatchStarted) throw new InvalidOperationException("The queue has no active submission to inspect.");
        var receipt = _client.ReadReceipt(queue.Current.AttemptId);
        if (receipt.AttemptId != queue.Current.AttemptId || RequestHash(receipt.Request) != queue.Current.RequestHash)
            throw new InvalidOperationException("The active queue receipt does not match its frozen prompt. Existing records were preserved.");
        return _client.HasFinishedSuccessfully(receipt);
    }

    internal CodexAgentWaitingQueue AdvanceQueue(CodexAgentWaitingQueue expected)
    {
        using var gate = AcquireQueueGate();
        var queue = RequireCurrentQueue(expected);
        if (!IsQueueAgentFinished(queue))
            throw new InvalidOperationException("The current queue agent has not finished. The next prompt remains waiting.");
        var index = queue.Index + 1;
        return SaveNextQueue(queue with
        {
            Index = index, DispatchStarted = false,
            State = index == queue.Entries.Count ? CodexAgentWaitingQueueState.Completed : CodexAgentWaitingQueueState.Active,
            Summary = index == queue.Entries.Count ? $"Queue finished. {index} separate Codex chat(s) completed." : ""
        });
    }

    internal CodexAgentWaitingQueue StopQueue(string queueId) => ChangeQueueState(queueId,
        CodexAgentWaitingQueueState.Stopped, "Queue stopped. The current agent continues; unstarted prompts remain in Waiting.");

    internal CodexAgentWaitingQueue PauseQueueForReview(string queueId, string reason) => ChangeQueueState(queueId,
        CodexAgentWaitingQueueState.NeedsReview, "Queue needs review. " + SensitiveDataProtection.Redact(reason));

    private CodexAgentWaitingQueue ChangeQueueState(string queueId, CodexAgentWaitingQueueState state, string summary)
    {
        using var gate = AcquireQueueGate();
        var queue = ReadQueue() ?? throw new InvalidOperationException("The saved queue is unavailable. Existing prompts were preserved.");
        if (queue.Id != queueId) throw new InvalidOperationException("The saved queue changed. Refresh before continuing.");
        // An error arriving after an explicit Stop must never reactivate the queue.
        if (!queue.OwnsPrompts) return queue;
        return SaveNextQueue(queue with { State = state, Summary = LimitSummary(summary) });
    }

    private CodexAgentWaitingQueue RequireCurrentQueue(CodexAgentWaitingQueue expected)
    {
        var queue = ReadQueue();
        if (queue is null || queue.Id != expected.Id || queue.Revision != expected.Revision ||
            queue.State != CodexAgentWaitingQueueState.Active)
            throw new InvalidOperationException("The saved queue stopped or changed. No further prompts were dispatched.");
        return queue;
    }

    private bool QueueOwns(string waitingId) => ReadQueue() is { OwnsPrompts: true } queue &&
        queue.Entries.Skip(queue.Index).Any(entry => entry.WaitingId == waitingId);

    private void RequireNotQueued(string waitingId)
    {
        if (QueueOwns(waitingId))
            throw new InvalidOperationException("This prompt belongs to the saved queue. Stop the queue before removing or releasing it separately.");
    }

    private CodexAgentWaitingQueue SaveNextQueue(CodexAgentWaitingQueue queue)
    {
        queue = queue with { Revision = checked(queue.Revision + 1), UpdatedAt = DateTimeOffset.UtcNow };
        SaveQueue(queue);
        return queue;
    }

    private void SaveQueue(CodexAgentWaitingQueue queue)
    {
        ValidateQueue(queue);
        var bytes = CodexAgentStorage.Serialize(queue);
        if (bytes.Length > 1_000_000) throw new InvalidOperationException("The saved queue exceeds its supported size.");
        CodexAgentStorage.WriteAtomic(QueuePath, bytes);
    }

    private static void ValidateQueueItem(CodexAgentWaitingQueueEntry entry, CodexAgentWaitingItem item)
    {
        if (item.Id != entry.WaitingId || item.Request.ProjectId != entry.ProjectId ||
            item.Request.ProjectName != entry.ProjectName || item.Request.Folder != entry.Folder ||
            RequestHash(item.Request) != entry.RequestHash || item.AttemptId is { } attempt && attempt != entry.AttemptId)
            throw new InvalidOperationException("The saved queue prompt or its submission identity changed. Existing records were preserved.");
    }

    private static string RequestHash(CodexAgentChatRequest request) =>
        Convert.ToHexString(SHA256.HashData(CodexAgentStorage.Serialize(request)));

    private static void ValidateQueue(CodexAgentWaitingQueue queue)
    {
        if (queue.Version != 1 || !Guid.TryParseExact(queue.Id, "N", out _) || queue.Revision < 1 ||
            !Enum.IsDefined(queue.State) || queue.Entries is null || queue.Entries.Count is < 1 or > MaximumItems ||
            queue.Index < 0 || queue.Index > queue.Entries.Count || queue.CreatedAt == default || queue.UpdatedAt == default ||
            queue.Summary is null || queue.Summary.Length > 4_000 ||
            (queue.State == CodexAgentWaitingQueueState.Completed) != (queue.Index == queue.Entries.Count) ||
            queue.DispatchStarted && queue.Index == queue.Entries.Count)
            throw new InvalidOperationException("The saved queue is invalid or unsupported. Existing records were preserved.");
        foreach (var entry in queue.Entries)
        {
            if (entry is null || !Guid.TryParseExact(entry.WaitingId, "N", out _) ||
                !Guid.TryParseExact(entry.AttemptId, "N", out _) ||
                string.IsNullOrWhiteSpace(entry.ProjectId) || entry.ProjectId.Length > 200 ||
                string.IsNullOrWhiteSpace(entry.ProjectName) || entry.ProjectName.Length > 300 ||
                string.IsNullOrWhiteSpace(entry.Folder) || entry.Folder.Length > 32_000 || !Path.IsPathFullyQualified(entry.Folder) ||
                entry.RequestHash is null || entry.RequestHash.Length != 64 || !entry.RequestHash.All(Uri.IsHexDigit))
                throw new InvalidOperationException("A saved queue entry is invalid or unsupported. Existing records were preserved.");
        }
        if (queue.Entries.Select(entry => entry.WaitingId).Distinct(StringComparer.Ordinal).Count() != queue.Entries.Count ||
            queue.Entries.Select(entry => entry.AttemptId).Distinct(StringComparer.Ordinal).Count() != queue.Entries.Count)
            throw new InvalidOperationException("The saved queue contains repeated identities. Existing records were preserved.");
    }
}
