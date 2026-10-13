using FullStackLauncher.Services;

namespace FullStackLauncher.Models;

/// <summary>Captured configured folders. Runtime evidence is never saved in launcher settings.</summary>
public sealed record ProjectGitRegistration(string ProjectId, IReadOnlyList<GitRepositoryDiscoveryFolder> Folders);

public sealed record ProjectGitState
{
    public required string ProjectId { get; init; }
    public IReadOnlyList<GitRepositoryDiscoveryFolder> Folders { get; init; } = [];
    public GitRepositoryDiscoveryResult Discovery { get; init; } = new([], [], null);
    public IReadOnlyDictionary<string, GitRepositoryState> Repositories { get; init; } =
        new Dictionary<string, GitRepositoryState>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset ReadAt { get; init; }
    public bool IsRefreshing { get; init; }
    public string? Error { get; init; }
}

/// <summary>One checkout's shared, immutable view. Cached evidence never bypasses command validation.</summary>
public sealed record GitRepositoryState
{
    public required string RepositoryRoot { get; init; }
    public GitRepositorySnapshot? Snapshot { get; init; }
    public AgentGitConnectionSelection? Selection { get; init; }
    public GitRemoteInfo? ActiveRemote { get; init; }
    public GitActiveConnectionInfo ActiveConnection { get; init; } = new();
    public string? SelectionSource { get; init; }
    public string? SelectionError { get; init; }
    public string RemoteName { get; init; } = "";
    public GitRemoteComparison? Comparison { get; init; }
    public string? ComparisonError { get; init; }
    public GitPowerDetails? PowerDetails { get; init; }
    public string? PowerError { get; init; }
    public AgentGitSummaryBatch? PendingSummaries { get; init; }
    public IReadOnlyList<AgentGitSummaryBatch> SummaryBatches { get; init; } = [];
    public string? LedgerError { get; init; }
    public string? SummaryError => LedgerError;
    public DateTimeOffset ReadAt { get; init; }
    public DateTimeOffset? LastFetchAt { get; init; }
    public string? FetchError { get; init; }
    public bool IsRefreshing { get; init; }
    public string? Error { get; init; }
    public Exception? Failure { get; init; }
}

public sealed class ProjectGitChangedEventArgs(string? projectId, string? repositoryRoot) : EventArgs
{
    public string? ProjectId { get; } = projectId;
    public string? RepositoryRoot { get; } = repositoryRoot;
}

/// <summary>A single passive ledger read shared by connection selection and commit previews.</summary>
public sealed record AgentGitLedgerSnapshot(AgentGitConnectionSelection? Selection,
    IReadOnlyList<AgentGitSummaryBatch> Summaries);
