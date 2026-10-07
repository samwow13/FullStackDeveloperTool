using System.IO;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexAgentChatRequest(string ProjectId, string ProjectName, string Folder,
    string Prompt, string Context, string ModelId, string ReasoningEffort)
{
    public IReadOnlyList<CodexAgentChatImage> Images { get; init; } = [];
    public CodexAgentAccessMode AccessMode { get; init; } = CodexAgentAccessMode.Workspace;
}

/// <summary>A clipboard image frozen with one explicit agent chat submission.</summary>
public sealed record CodexAgentChatImage
{
    public const int MaximumCount = 12;
    public const int MaximumBytes = 8_000_000;
    public const int MaximumTotalBytes = 24_000_000;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Caption { get; init; } = "";
    public string MimeType { get; init; } = "image/png";
    public string DataBase64 { get; init; } = "";
}

public enum CodexAgentChatState { Prepared, Starting, Running, Completed, Failed, Interrupted, Unconfirmed }

public sealed record CodexAgentChatReceipt
{
    public int Version { get; init; } = 2;
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
    // 24 MB of images expands to 32 MB of Base64, plus bounded escaped text.
    private const int MaximumReceiptBytes = 36_000_000;
    internal string DirectoryPath { get; } = CodexAgentStorage.ResolvePath("codex-agent-chats");

    internal CodexAgentChatReceipt Prepare(CodexAgentChatRequest request)
    {
        var frozen = FreezeRequest(request);
        ValidateRequest(frozen);
        frozen = frozen with { Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frozen.Folder)) };
        if (!Directory.Exists(frozen.Folder)) throw new ArgumentException("The project folder no longer exists. Open its settings and choose an existing folder.");
        var receipt = new CodexAgentChatReceipt
        {
            AttemptId = Guid.NewGuid().ToString("N"), Request = frozen,
            State = CodexAgentChatState.Prepared, Summary = "Preparing the new Codex chat.",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        Directory.CreateDirectory(DirectoryPath);
        var path = ReceiptPath(receipt.AttemptId);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(SerializeReceipt(receipt));
            stream.Flush(flushToDisk: true);
        }
        return receipt;
    }

    internal CodexAgentChatReceipt Read(string attemptId)
    {
        var bytes = CodexAgentStorage.Read(ReceiptPath(attemptId), MaximumReceiptBytes)
            ?? throw new InvalidOperationException("The saved agent chat receipt is unavailable. Review recent tasks in Codex before sending again.");
        var receipt = CodexAgentStorage.Deserialize<CodexAgentChatReceipt>(bytes);
        if (receipt.Version is not (1 or 2) || receipt.AttemptId != attemptId || !Enum.IsDefined(receipt.State)
            || receipt.Summary is null || receipt.Summary.Length > 4_000
            || receipt.ThreadId?.Length > 200 || receipt.TurnId?.Length > 200)
            throw new InvalidOperationException("The saved agent chat receipt is invalid or unsupported. Review recent tasks in Codex before sending again.");
        ValidateRequest(receipt.Request);
        if (receipt.Version == 1 && receipt.Request.Images.Count != 0)
            throw new InvalidOperationException("The saved agent chat receipt has unsupported image data. The existing receipt was preserved.");
        return receipt with { Request = FreezeRequest(receipt.Request) };
    }

    internal void Save(CodexAgentChatReceipt receipt) =>
        CodexAgentStorage.WriteAtomic(ReceiptPath(receipt.AttemptId), SerializeReceipt(receipt));

    private static byte[] SerializeReceipt(CodexAgentChatReceipt receipt)
    {
        var bytes = CodexAgentStorage.Serialize(receipt);
        if (bytes.Length > MaximumReceiptBytes)
            throw new InvalidOperationException("The agent chat receipt exceeds its supported size. Remove an image or shorten the message.");
        return bytes;
    }

    private static CodexAgentChatRequest FreezeRequest(CodexAgentChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images is null || request.Images.Count > CodexAgentChatImage.MaximumCount)
            throw new ArgumentException("An agent message has too many images or an invalid image list.");
        var images = request.Images.ToArray();
        if (images.Any(image => image is null))
            throw new ArgumentException("An attached image cannot be empty.");
        return request with { Images = Array.AsReadOnly(images.Select(image => image with { }).ToArray()) };
    }

    internal FileStream Claim(string attemptId) => CodexAgentStorage.AcquireLock(ReceiptPath(attemptId) + ".lock");

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
        CodexAgentImageStaging.ValidateImages(request.Images);
    }
}
