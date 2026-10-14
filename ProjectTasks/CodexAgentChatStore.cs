using System.IO;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexAgentChatRequest(string ProjectId, string ProjectName, string Folder,
    string Prompt, string Context, string ModelId, string ReasoningEffort)
{
    public IReadOnlyList<CodexAgentChatImage> Images { get; init; } = [];
    public CodexAgentAccessMode AccessMode { get; init; } = CodexAgentAccessMode.Workspace;
    public bool KeepAwake { get; init; }
    public bool UltraFastEnabled { get; init; }
    public bool FastEnabled { get; init; }
}

/// <summary>An image and optional capture evidence frozen with one explicit agent chat submission.</summary>
public sealed record CodexAgentChatImage
{
    public const int MaximumCount = 12;
    public const int MaximumBytes = 8_000_000;
    public const int MaximumTotalBytes = 24_000_000;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Caption { get; init; } = "";
    public string MimeType { get; init; } = "image/png";
    public string DataBase64 { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodexAgentBrowserContext? BrowserContext { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodexAgentCaptureContext? CaptureContext { get; init; }
}

public enum CodexAgentChatState { Prepared, Starting, Running, Completed, Failed, Interrupted, Unconfirmed }

public sealed record CodexAgentChatReceipt
{
    // Version 5 also freezes Fast and Ultrafast. Older owners must reject it rather than
    // silently dispatch a different service tier or drop reviewed capture context.
    public int Version { get; init; } = 5;
    public string AttemptId { get; init; } = "";
    public CodexAgentChatRequest Request { get; init; } = new("", "", "", "", "", "", "");
    public CodexAgentChatState State { get; init; }
    public string? ThreadId { get; init; }
    public string? TurnId { get; init; }
    public string Summary { get; init; } = "";
    public bool SubmissionAttempted { get; init; }
    public bool ThreadCreationAttempted { get; init; }
    public bool TerminalConfirmed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAccepted => !string.IsNullOrWhiteSpace(ThreadId) && !string.IsNullOrWhiteSpace(TurnId)
        && (State == CodexAgentChatState.Running || TerminalConfirmed);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RequiresReview => State == CodexAgentChatState.Unconfirmed;
}

internal sealed class CodexAgentChatStore
{
    // 24 MB of images expands to 32 MB of Base64. Twelve browser captures may
    // add 4.8 million source characters; allow their worst-case JSON escaping.
    private const int MaximumReceiptBytes = 64_000_000;
    internal string DirectoryPath { get; } = CodexAgentStorage.ResolvePath("codex-agent-chats");

    internal CodexAgentChatReceipt Prepare(CodexAgentChatRequest request) =>
        Prepare(request, Guid.NewGuid().ToString("N"));

    internal CodexAgentChatReceipt Prepare(CodexAgentChatRequest request, string attemptId)
    {
        var frozen = FreezeRequest(request);
        ValidateRequest(frozen);
        frozen = frozen with { Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frozen.Folder)) };
        if (!Directory.Exists(frozen.Folder)) throw new ArgumentException("The project folder no longer exists. Open its settings and choose an existing folder.");
        Directory.CreateDirectory(DirectoryPath);
        var path = ReceiptPath(attemptId);
        using var preparation = CodexAgentStorage.AcquireLock(path + ".prepare.lock");
        if (File.Exists(path))
        {
            var existing = Read(attemptId);
            if (!CodexAgentStorage.Serialize(existing.Request).AsSpan().SequenceEqual(CodexAgentStorage.Serialize(frozen)))
                throw new InvalidOperationException("The saved agent attempt belongs to a different request. Its receipt was preserved.");
            return existing;
        }
        var receipt = new CodexAgentChatReceipt
        {
            AttemptId = attemptId, Request = frozen,
            State = CodexAgentChatState.Prepared, Summary = "Preparing the new Codex chat.",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        CodexAgentStorage.WriteAtomic(path, SerializeReceipt(receipt));
        return receipt;
    }

    internal CodexAgentChatReceipt Read(string attemptId)
    {
        var bytes = CodexAgentStorage.Read(ReceiptPath(attemptId), MaximumReceiptBytes)
            ?? throw new InvalidOperationException("The saved agent chat receipt is unavailable. Review recent tasks in Codex before sending again.");
        var receipt = CodexAgentStorage.Deserialize<CodexAgentChatReceipt>(bytes);
        if (receipt.Version is not (1 or 2 or 3 or 4 or 5) || receipt.AttemptId != attemptId || !Enum.IsDefined(receipt.State)
            || receipt.Summary is null || receipt.Summary.Length > 4_000
            || receipt.ThreadId?.Length > 200 || receipt.TurnId?.Length > 200)
            throw new InvalidOperationException("The saved agent chat receipt is invalid or unsupported. Review recent tasks in Codex before sending again.");
        ValidateRequest(receipt.Request);
        if (receipt.Version == 1 && receipt.Request.Images.Count != 0)
            throw new InvalidOperationException("The saved agent chat receipt has unsupported image data. The existing receipt was preserved.");
        if (receipt.Version < 3 && receipt.Request.Images.Any(image => image.BrowserContext is not null))
            throw new InvalidOperationException("The saved agent chat receipt has unsupported browser context. The existing receipt was preserved.");
        if (receipt.Version < 4 && receipt.Request.Images.Any(image => image.CaptureContext is not null))
            throw new InvalidOperationException("The saved agent chat receipt has unsupported desktop capture context. The existing receipt was preserved.");
        if (receipt.Version < 5 && (receipt.Request.UltraFastEnabled || receipt.Request.FastEnabled))
            throw new InvalidOperationException("The saved agent chat receipt has unsupported speed settings. The existing receipt was preserved.");
        return receipt with { Request = FreezeRequest(receipt.Request) };
    }

    internal void Save(CodexAgentChatReceipt receipt) =>
        CodexAgentStorage.WriteAtomic(ReceiptPath(receipt.AttemptId), SerializeReceipt(receipt));

    private static byte[] SerializeReceipt(CodexAgentChatReceipt receipt)
    {
        if (receipt.Version is not (1 or 2 or 3 or 4 or 5))
            throw new InvalidOperationException("The agent chat receipt version is unsupported. The existing receipt was preserved.");
        ValidateRequest(receipt.Request);
        if (receipt.Version == 1 && receipt.Request.Images.Count != 0 ||
            receipt.Version < 3 && receipt.Request.Images.Any(image => image.BrowserContext is not null) ||
            receipt.Version < 4 && receipt.Request.Images.Any(image => image.CaptureContext is not null))
            throw new InvalidOperationException("The agent chat receipt version cannot preserve its attachments. The existing receipt was preserved.");
        if (receipt.Version < 5 && (receipt.Request.UltraFastEnabled || receipt.Request.FastEnabled))
            throw new InvalidOperationException("The agent chat receipt version cannot preserve its speed settings. The existing receipt was preserved.");
        var bytes = CodexAgentStorage.Serialize(receipt);
        if (bytes.Length > MaximumReceiptBytes)
            throw new InvalidOperationException("The agent chat receipt exceeds its supported size. Remove an image or shorten the message.");
        return bytes;
    }

    internal static CodexAgentChatRequest FreezeRequest(CodexAgentChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images is null || request.Images.Count > CodexAgentChatImage.MaximumCount)
            throw new ArgumentException("An agent message has too many images or an invalid image list.");
        var images = request.Images.ToArray();
        if (images.Any(image => image is null))
            throw new ArgumentException("An attached image cannot be empty.");
        return request with { Images = Array.AsReadOnly(images.Select(image => image with
        {
            BrowserContext = image.BrowserContext is { } context ? context with { } : null,
            CaptureContext = image.CaptureContext is { } capture ? capture with { } : null
        }).ToArray()) };
    }

    internal FileStream Claim(string attemptId) => CodexAgentStorage.AcquireLock(ReceiptPath(attemptId) + ".lock");

    internal bool RecordDispatchIntent(string attemptId)
    {
        var path = ReceiptPath(attemptId) + ".dispatch";
        using var gate = CodexAgentStorage.AcquireLock(path + ".lock");
        var bytes = CodexAgentStorage.Read(path, 4096);
        if (bytes is not null)
        {
            var marker = CodexAgentStorage.Deserialize<DispatchMarker>(bytes);
            if (marker.AttemptId != attemptId || marker.RequestedAt == default)
                throw new InvalidOperationException("The agent dispatch record is invalid. The saved request was preserved.");
            return false;
        }
        var receipt = Read(attemptId);
        if (receipt.State != CodexAgentChatState.Prepared || receipt.ThreadCreationAttempted || receipt.SubmissionAttempted ||
            receipt.ThreadId is not null || receipt.TurnId is not null || receipt.TerminalConfirmed || IsReviewed(attemptId))
            return false;
        // An interrupted launch remains tied to this attempt. A second caller
        // can inspect its receipt, but can never create another owner implicitly.
        CodexAgentStorage.WriteAtomic(path, CodexAgentStorage.Serialize(new DispatchMarker(attemptId, DateTimeOffset.UtcNow)));
        return true;
    }

    private sealed record DispatchMarker(string AttemptId, DateTimeOffset RequestedAt);

    internal bool IsReviewed(string attemptId)
    {
        var bytes = CodexAgentStorage.Read(ReceiptPath(attemptId) + ".reviewed", 4096);
        if (bytes is null) return false;
        var marker = CodexAgentStorage.Deserialize<ReviewMarker>(bytes);
        if (marker.AttemptId != attemptId || marker.ReviewedAt == default)
            throw new InvalidOperationException("The agent chat review record is invalid. The original receipt was preserved.");
        return true;
    }

    internal void RecordReview(string attemptId)
    {
        // Separate from the owner's receipt so late outcome updates cannot erase
        // the user's review, and review never rewrites an uncertain outcome.
        CodexAgentStorage.WriteAtomic(ReceiptPath(attemptId) + ".reviewed",
            CodexAgentStorage.Serialize(new ReviewMarker(attemptId, DateTimeOffset.UtcNow)));
    }

    private sealed record ReviewMarker(string AttemptId, DateTimeOffset ReviewedAt);

    private string ReceiptPath(string attemptId)
    {
        if (!Guid.TryParseExact(attemptId, "N", out _)) throw new ArgumentException("The agent chat receipt identifier is invalid.");
        return Path.Combine(DirectoryPath, attemptId + ".json");
    }

    internal static void ValidateRequest(CodexAgentChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CodexAgentStorage.RequireText(request.ProjectId, 200, "Select a project before starting an agent.");
        CodexAgentStorage.RequireText(request.ProjectName, 300, "The project name is unavailable.");
        CodexAgentStorage.RequireText(request.Folder, 32_000, "The project folder is unavailable.");
        if (!Path.IsPathFullyQualified(request.Folder)) throw new ArgumentException("The project folder must be an absolute folder.");
        if (request.Prompt is null || request.Prompt.Length > CodexAgentChatClient.MaximumPromptCharacters
            || string.IsNullOrWhiteSpace(request.Prompt) && (request.Images is null || request.Images.Count == 0))
            throw new ArgumentException("Write a message or attach an image before starting an agent. Messages must be at most 100,000 characters.");
        if (request.Context is null || request.Context.Length > CodexAgentPromptStore.MaximumContextCharacters)
            throw new ArgumentException("Project context must be at most 8,000 characters.");
        CodexAgentStorage.RequireText(request.ModelId, 200, "Select an available Codex model.");
        CodexAgentStorage.RequireText(request.ReasoningEffort, 100, "Select an available thinking level.");
        if (!Enum.IsDefined(request.AccessMode)) throw new ArgumentException("Choose a supported agent access mode.");
        if (request.FastEnabled && request.UltraFastEnabled)
            throw new ArgumentException("Choose either Fast or Ultrafast, not both.");
        CodexAgentImageStaging.ValidateImages(request.Images);
    }
}
