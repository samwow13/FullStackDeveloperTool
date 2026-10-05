namespace FullStackLauncher.Models;

/// <summary>The selected dashboard checkout, branch and credential-free connection identity.</summary>
public sealed record DashboardGitComparisonScope(string RepositoryRoot, string Branch, string RemoteName, string ConnectionId);

/// <summary>Local comparison evidence. Nothing here is persisted in launcher settings.</summary>
public sealed record DashboardGitComparison
{
    public required DashboardGitComparisonScope Scope { get; init; }
    public GitRemoteComparison? Comparison { get; init; }
    public int UncommittedFiles { get; init; }
    public string? ReleaseBranch { get; init; }
    public string? SuggestedReleaseBranch { get; init; }
    public string? ReleaseBranchDetectionDetail { get; init; }
    public GitRepositorySnapshot? Snapshot { get; init; }
    public string? UnavailableReason { get; init; }
    public string? ConflictCheckUnavailableReason { get; init; }
    public DateTimeOffset ReadAt { get; init; }
    public bool Available => Comparison?.Available == true && UnavailableReason is null;
    public bool CanCheckConflicts => Snapshot is not null && ReleaseBranch is { Length: > 0 }
        && ConflictCheckUnavailableReason is null;
}

/// <summary>A remote Git-only simulation result, or an inline explanation of unavailable evidence.</summary>
public sealed record DashboardGitConflictCheck
{
    public required DashboardGitComparisonScope Scope { get; init; }
    public GitMergeCheckResult? Result { get; init; }
    public string? UnavailableReason { get; init; }
    public bool Available => Result is not null && UnavailableReason is null;
}
