namespace FullStackLauncher.Models;

/// <summary>Captured committed history for a selected branch or a comparison of two branch tips.</summary>
public sealed record GitBranchComparison
{
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public string RepositoryRoot { get; init; } = "";
    public string CurrentBranch { get; init; } = "";
    public string CurrentBranchRef { get; init; } = "";
    public string LocalCommitId { get; init; } = "";
    public string SelectedBranchName { get; init; } = "";
    public string SelectedCommitId { get; init; } = "";
    public bool SelectedIsRemote { get; init; }
    public bool SelectedBranchOnly { get; init; }
    public long LocalOnlyCommits { get; init; }
    public long SelectedOnlyCommits { get; init; }
    public long TotalCommits { get; init; }
}

public enum GitBranchCommitMembership
{
    BothBranches,
    SelectedBranchOnly,
    LocalOnly,
    SelectedBranch
}

public sealed record GitBranchHistoryCommit
{
    public string CommitId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset CommittedAt { get; init; }
    public DateTimeOffset LocalCommittedAt => CommittedAt.ToLocalTime();
    public GitBranchCommitMembership Membership { get; init; }
    public bool SelectedIsRemote { get; init; }
    public string ShortCommitId => CommitId[..Math.Min(12, CommitId.Length)];
    public string MembershipLabel => Membership switch
    {
        GitBranchCommitMembership.BothBranches => "Both branches",
        GitBranchCommitMembership.LocalOnly => "Local only",
        GitBranchCommitMembership.SelectedBranch => SelectedIsRemote ? "Remote branch" : "Selected branch",
        _ => SelectedIsRemote ? "Remote only" : "Selected branch only"
    };
}

public sealed record GitBranchHistoryPage
{
    public int PageIndex { get; init; }
    public int PageSize { get; init; } = 30;
    public long TotalCommits { get; init; }
    public IReadOnlyList<GitBranchHistoryCommit> Commits { get; init; } = [];
    public bool HasPreviousPage => PageIndex > 0;
    public bool HasNextPage => ((long)PageIndex + 1) * PageSize < TotalCommits;
}
