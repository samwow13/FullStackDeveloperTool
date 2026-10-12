using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexAgentKeepAwakeStatus(bool IsEnabled, bool CanEnable, string Status);
public sealed record CodexAgentKeepAwakeEntry(string ThreadId, CodexAgentKeepAwakeStatus Status);

/// <summary>Exact-chat opt-in and append-only continuation receipts, separate from queues and settings.</summary>
public sealed class CodexAgentKeepAwakeStore
{
    private const int MaximumRecordBytes = 32_000;
    private readonly string _directory = CodexAgentStorage.ResolvePath("codex-agent-keep-awake");

    public CodexAgentKeepAwakeStatus GetStatus(string projectId, string threadId)
    {
        var state = Read(projectId, threadId);
        if (state is null) return new(false, false, "Keep awake requires a connected Launcher-started agent.");
        var available = OwnerAvailable(state);
        return new(state.IsEnabled, available && state.Phase is not (OwnerPhase.Suspended or OwnerPhase.Closed),
            available || state.Phase is OwnerPhase.Suspended or OwnerPhase.Closed ? state.Status
                : "Keep awake owner is unavailable. Existing work may still be running.");
    }

    public IReadOnlyList<CodexAgentKeepAwakeEntry> ReadEnabled(string projectId)
    {
        CodexAgentStorage.RequireText(projectId, 200, "Select the owning project before reading Keep awake.");
        if (!Directory.Exists(_directory)) return [];
        var result = new List<CodexAgentKeepAwakeEntry>();
        var examined = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(10_001))
        {
            if (++examined > 10_000)
                throw new InvalidOperationException("Too many saved Keep awake agents to read safely.");
            var bytes = CodexAgentStorage.Read(path, MaximumRecordBytes);
            if (bytes is null) continue;
            var state = CodexAgentStorage.Deserialize<OwnerState>(bytes);
            if (state.ProjectId != projectId || !state.IsEnabled) continue;
            result.Add(new(state.ThreadId, GetStatus(projectId, state.ThreadId)));
        }
        return result;
    }

    public CodexAgentKeepAwakeStatus SetEnabled(string projectId, string threadId, bool enabled)
    {
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireState(projectId, threadId);
        if (enabled && (!OwnerAvailable(state) || state.Phase is OwnerPhase.Suspended or OwnerPhase.Closed))
            throw new InvalidOperationException(state.Phase == OwnerPhase.Suspended ? state.Status
                : "Keep awake requires a connected Launcher-started agent. Start a new agent with Keep awake enabled.");
        Save(state with
        {
            IsEnabled = enabled,
            Status = state.Phase is OwnerPhase.Suspended or OwnerPhase.Closed ? state.Status
                : enabled ? "Keep awake enabled. The next completed turn will continue after 10 seconds."
                : "Keep awake off. The current turn continues; no further turns will be sent.",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return GetStatus(projectId, threadId);
    }

    internal void Initialize(string ownerAttemptId, string projectId, string threadId, bool initiallyEnabled)
    {
        if (!Guid.TryParseExact(ownerAttemptId, "N", out _))
            throw new ArgumentException("Keep awake requires a valid Launcher agent owner.");
        using var command = AcquireCommand(projectId, threadId);
        if (Read(projectId, threadId) is not null)
            throw new InvalidOperationException("This chat already has a Keep awake owner. Existing continuation history was preserved.");
        Save(new OwnerState
        {
            OwnerAttemptId = ownerAttemptId, ProjectId = projectId, ThreadId = threadId,
            IsEnabled = initiallyEnabled, Phase = OwnerPhase.Starting,
            Status = initiallyEnabled ? "Keep awake enabled. Waiting for the initial turn."
                : "Keep awake off. Enable it while this Launcher-started agent is connected.",
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    internal FileStream AcquireCommand(string projectId, string threadId)
    {
        ValidateIdentity(projectId, threadId);
        Directory.CreateDirectory(_directory);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(StatePath(threadId) + ".command.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException("Keep awake is sending or saving a command. Wait briefly and try again.");
                Thread.Sleep(25);
            }
        }
    }

    internal bool ContinueOrCloseIfDisabled(string ownerAttemptId, string projectId, string threadId)
    {
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        if (state.Phase is OwnerPhase.Suspended or OwnerPhase.Closed) return false;
        if (state.IsEnabled) return true;
        // Seal terminal-off atomically with the switch's command gate. Late enable cannot succeed as the owner exits.
        Save(state with { IsEnabled = false, Phase = OwnerPhase.Closed,
            Status = "Keep awake off. This Launcher agent owner has finished.", UpdatedAt = DateTimeOffset.UtcNow });
        return false;
    }

    internal void SetTurn(string ownerAttemptId, string projectId, string threadId, string turnId, bool running)
    {
        CodexAgentStorage.RequireText(turnId, 200, "The exact agent turn is unavailable.");
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        Save(state with { CurrentTurnId = turnId, Phase = running ? OwnerPhase.Running : OwnerPhase.Starting,
            Status = state.IsEnabled ? "Keep awake enabled. Waiting for the current turn to finish."
                : "Keep awake off. The current turn continues.", UpdatedAt = DateTimeOffset.UtcNow });
    }

    internal void SetIdle(string ownerAttemptId, string projectId, string threadId, string turnId)
    {
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        if (state.CurrentTurnId != turnId)
            throw new InvalidOperationException("Keep awake could not confirm its exact completed turn.");
        Save(state with { Phase = OwnerPhase.Idle, Status = state.IsEnabled
            ? "Keep awake enabled. Continuing this chat after 10 seconds."
            : "Keep awake off. The submitted turn completed.", UpdatedAt = DateTimeOffset.UtcNow });
    }

    internal ContinuationIntent? PrepareContinuationUnderCommand(string ownerAttemptId, string projectId,
        string threadId, string predecessorTurnId)
    {
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        if (!state.IsEnabled)
        {
            Save(state with { IsEnabled = false, Phase = OwnerPhase.Closed,
                Status = "Keep awake off. This Launcher agent owner has finished.", UpdatedAt = DateTimeOffset.UtcNow });
            return null;
        }
        if (state.Phase != OwnerPhase.Idle || state.CurrentTurnId != predecessorTurnId)
            throw new InvalidOperationException("Keep awake stopped because its latest owned turn could not be confirmed idle.");
        var intent = new ContinuationIntent(1, Guid.NewGuid().ToString("N"), ownerAttemptId, projectId,
            threadId, predecessorTurnId, DateTimeOffset.UtcNow);
        WriteImmutable(intent, "intent", intent);
        Save(state with { Phase = OwnerPhase.Starting, Status = "Sending Keep Going to this exact chat.",
            UpdatedAt = DateTimeOffset.UtcNow });
        return intent;
    }

    internal void BeforeSubmissionUnderCommand(ContinuationIntent intent)
    {
        var state = RequireOwner(intent.OwnerAttemptId, intent.ProjectId, intent.ThreadId);
        if (!state.IsEnabled || state.Phase != OwnerPhase.Starting || state.CurrentTurnId != intent.PredecessorTurnId)
            throw new InvalidOperationException("Keep awake was disabled or its completed turn changed before submission.");
        WriteImmutable(intent, "submitted", new ContinuationEvent(intent.ContinuationId, null,
            "submissionAttempted", "Keep Going submission was attempted.", false, DateTimeOffset.UtcNow));
    }

    internal void RecordAcceptedUnderCommand(ContinuationIntent intent, string turnId)
    {
        CodexAgentStorage.RequireText(turnId, 200, "Codex did not confirm the exact continuation turn.");
        WriteImmutable(intent, "accepted", new ContinuationEvent(intent.ContinuationId, turnId,
            "accepted", "Codex acknowledged the continuation turn.", false, DateTimeOffset.UtcNow));
        var state = RequireOwner(intent.OwnerAttemptId, intent.ProjectId, intent.ThreadId);
        Save(state with { CurrentTurnId = turnId, Phase = OwnerPhase.Starting,
            Status = "Keep Going accepted. Waiting for the exact continuation turn.", UpdatedAt = DateTimeOffset.UtcNow });
    }

    internal void RecordOutcome(ContinuationIntent intent, string? turnId, string status, string summary, bool terminalConfirmed)
    {
        WriteImmutable(intent, terminalConfirmed ? "terminal" : "unconfirmed",
            new ContinuationEvent(intent.ContinuationId, turnId, status, summary, terminalConfirmed, DateTimeOffset.UtcNow));
    }

    internal void Suspend(string ownerAttemptId, string projectId, string threadId, string status)
    {
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        Save(state with { Phase = OwnerPhase.Suspended, Status = status, UpdatedAt = DateTimeOffset.UtcNow });
    }

    internal void Close(string ownerAttemptId, string projectId, string threadId)
    {
        using var command = AcquireCommand(projectId, threadId);
        var state = RequireOwner(ownerAttemptId, projectId, threadId);
        if (state.Phase is OwnerPhase.Suspended or OwnerPhase.Closed) return;
        Save(state with { Phase = OwnerPhase.Closed, Status = state.IsEnabled
            ? "Keep awake owner closed. Existing work may still be running."
            : "Keep awake off. This Launcher agent owner has finished.", UpdatedAt = DateTimeOffset.UtcNow });
    }

    private OwnerState RequireOwner(string ownerAttemptId, string projectId, string threadId)
    {
        var state = RequireState(projectId, threadId);
        if (state.OwnerAttemptId != ownerAttemptId)
            throw new InvalidOperationException("Keep awake owner changed. Existing continuation history was preserved.");
        return state;
    }

    private OwnerState RequireState(string projectId, string threadId) => Read(projectId, threadId)
        ?? throw new InvalidOperationException("Keep awake requires a connected Launcher-started agent.");

    private OwnerState? Read(string projectId, string threadId)
    {
        ValidateIdentity(projectId, threadId);
        var bytes = CodexAgentStorage.Read(StatePath(threadId), MaximumRecordBytes);
        if (bytes is null) return null;
        var state = CodexAgentStorage.Deserialize<OwnerState>(bytes);
        if (state.Version != 1 || state.Capability != 1 || state.ProjectId != projectId || state.ThreadId != threadId
            || !Guid.TryParseExact(state.OwnerAttemptId, "N", out _) || !Enum.IsDefined(state.Phase)
            || state.Status is null || state.Status.Length > 4_000 || state.CurrentTurnId?.Length > 200
            || state.UpdatedAt == default)
            throw new InvalidOperationException("Saved Keep awake state is invalid or unsupported. Existing history was preserved.");
        return state;
    }

    private static bool OwnerAvailable(OwnerState state)
    {
        // A claim held by the exact new-capability owner is required. A saved flag alone is never evidence of a connection.
        var path = Path.Combine(CodexAgentStorage.ResolvePath("codex-agent-chats"), state.OwnerAttemptId + ".json.lock");
        try
        {
            using var claim = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void Save(OwnerState state) => CodexAgentStorage.WriteAtomic(StatePath(state.ThreadId), CodexAgentStorage.Serialize(state));

    private void WriteImmutable<T>(ContinuationIntent intent, string phase, T record)
    {
        var directory = Path.Combine(_directory, Digest(intent.ThreadId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Digest(intent.PredecessorTurnId) + "." + phase + ".json");
        var bytes = CodexAgentStorage.Serialize(record);
        if (bytes.Length > MaximumRecordBytes)
            throw new InvalidOperationException("The continuation receipt exceeds its supported size.");
        // CreateNew reserves this predecessor permanently, including uncertain submissions. No automatic resend or overwrite.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private string StatePath(string threadId) => Path.Combine(_directory, Digest(threadId) + ".json");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void ValidateIdentity(string projectId, string threadId)
    {
        CodexAgentStorage.RequireText(projectId, 200, "Select the owning project before changing Keep awake.");
        CodexAgentStorage.RequireText(threadId, 200, "Select one exact agent chat before changing Keep awake.");
    }

    internal sealed record ContinuationIntent(int Version, string ContinuationId, string OwnerAttemptId,
        string ProjectId, string ThreadId, string PredecessorTurnId, DateTimeOffset CreatedAt);
    private sealed record ContinuationEvent(string ContinuationId, string? TurnId, string Status,
        string Summary, bool TerminalConfirmed, DateTimeOffset CreatedAt);
    private enum OwnerPhase { Starting, Running, Idle, Suspended, Closed }
    private sealed record OwnerState
    {
        public int Version { get; init; } = 1;
        public int Capability { get; init; } = 1;
        public string OwnerAttemptId { get; init; } = "";
        public string ProjectId { get; init; } = "";
        public string ThreadId { get; init; } = "";
        public bool IsEnabled { get; init; }
        public string? CurrentTurnId { get; init; }
        public OwnerPhase Phase { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
