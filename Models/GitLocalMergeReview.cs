namespace FullStackLauncher.Models;

/// <summary>The exact local snapshot and committed-history preview approved in one modal.</summary>
public sealed record GitLocalMergeReview(GitRepositorySnapshot Snapshot, GitMergeCheckResult Comparison);

/// <summary>Preserves a confirmed merge result independently of subsequent status-read failures.</summary>
public sealed record GitLocalMergeOutcome(GitSyncResult? Result, bool Succeeded,
    IReadOnlyList<string> ConflictedPaths, string Details, GitPendingSync? PendingSync,
    GitCommandExitUnconfirmedException? UnconfirmedCommand);
