using System.Text.Json.Serialization;

namespace FullStackLauncher.Models;

public sealed record GitRemoteBranchChoice(string Name, string CommitId);

public enum GitSyncStage
{
    Preparing, Fetched, BranchReady, Checkpointed, MergeStarted, Conflicts,
    AwaitingConfirmation, Finalizing, ReadyToPush, Pushing, PushUncertain, NeedsReview
}

/// <summary>Recovery identities only. No credentials, source contents or command output.</summary>
public sealed record GitPendingSync
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public string AttemptId { get; init; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string RepositoryRoot { get; init; } = "";
    [JsonRequired] public string Remote { get; init; } = "";
    [JsonRequired] public string RemoteFingerprint { get; init; } = "";
    [JsonRequired] public string DestinationBranch { get; init; } = "";
    [JsonRequired] public string SourceBranch { get; init; } = "";
    [JsonRequired] public string SourceCommit { get; init; } = "";
    [JsonRequired] public string OriginalHead { get; init; } = "";
    [JsonRequired] public string PreparedHead { get; init; } = "";
    public string? CheckpointCommit { get; init; }
    public string? FinalCommit { get; init; }
    public string? FinalBranch { get; init; }
    [JsonRequired] public GitSyncStage Stage { get; init; }
    [JsonRequired] public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GitPowerDetails
{
    public string? LastCreatedBranch { get; init; }
    public DateTimeOffset? LastCreatedAt { get; init; }
    public string LastCreatedEvidence { get; init; } = "No retained branch-creation record is available.";
    public string? ReleaseBranch { get; init; }
    public string? SuggestedReleaseBranch { get; init; }
    public string? CachedRemoteDefaultBranch { get; init; }
    public string? ReleaseBranchDetectionDetail { get; init; }
    public GitPendingSync? PendingSync { get; init; }
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public string? RecoveryMessage { get; init; }
    public bool ReadyToFinish { get; init; }
    public bool CanDismiss { get; init; }
}

public sealed record GitSyncResult
{
    public string Message { get; init; } = "";
    public GitPendingSync? PendingSync { get; init; }
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public bool ReadyToFinish { get; init; }
}

/// <summary>A committed-history comparison, never a prepared or completed merge.</summary>
public sealed record GitMergeCheckResult
{
    public string CurrentBranch { get; init; } = "";
    public string CurrentCommit { get; init; } = "";
    public string Remote { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string SourceCommit { get; init; } = "";
    public long IncomingCommits { get; init; }
    public bool HasConflicts { get; init; }
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public bool HasUncommittedChanges { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string GitDiagnostics { get; init; } = "";
    public string Message { get; init; } = "";
}
