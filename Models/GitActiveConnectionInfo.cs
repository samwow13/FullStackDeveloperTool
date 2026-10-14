namespace FullStackLauncher.Models;

public enum GitActiveConnectionKind
{
    Unavailable,
    NoRemote,
    GitHubCredentials,
    DevOpsCredentials,
    DevOpsToken,
    Ssh,
    OtherCredentials,
    Mixed,
    MissingToken,
    NoRepository,
    SelectionRequired
}

/// <summary>Credential-free presentation of local repository state and the selected Git authentication route.</summary>
public sealed record GitActiveConnectionInfo
{
    public string Label { get; init; } = "Connection unavailable";
    public string HeaderLabel { get; init; } = "Git · Connection unavailable";
    public GitActiveConnectionKind Kind { get; init; }
    public string Foreground => Kind switch
    {
        GitActiveConnectionKind.GitHubCredentials => "#D8B4FE",
        GitActiveConnectionKind.DevOpsCredentials => "#A8CCFF",
        GitActiveConnectionKind.DevOpsToken => "#80CAFF",
        GitActiveConnectionKind.Ssh => "#A5F3D5",
        GitActiveConnectionKind.Mixed => "#FFD28C",
        GitActiveConnectionKind.MissingToken => "#FFBC8B",
        GitActiveConnectionKind.SelectionRequired => "#FFD28C",
        _ => "#B8C6D9"
    };
    public string Detail { get; init; } = "The active Git connection has not been read yet.";
    public string RepositoryStatus { get; init; } = "Repository status unavailable";
    public string RepositoryDetail { get; init; } = "Repository status has not been read yet.";
    public string BranchLabel { get; init; } = "Branch unavailable";
}
