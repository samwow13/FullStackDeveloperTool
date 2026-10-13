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
    MissingToken
}

/// <summary>Credential-free presentation of the selected remote's Git authentication route.</summary>
public sealed record GitActiveConnectionInfo
{
    public string Label { get; init; } = "Connection unavailable";
    public string HeaderLabel { get; init; } = "Git - Connection unavailable";
    public GitActiveConnectionKind Kind { get; init; }
    public string Foreground => Kind switch
    {
        GitActiveConnectionKind.GitHubCredentials => "#D8B4FE",
        GitActiveConnectionKind.DevOpsCredentials => "#A8CCFF",
        GitActiveConnectionKind.DevOpsToken => "#80CAFF",
        GitActiveConnectionKind.Ssh => "#A5F3D5",
        GitActiveConnectionKind.Mixed => "#FFD28C",
        GitActiveConnectionKind.MissingToken => "#FFBC8B",
        _ => "#B8C6D9"
    };
    public string Detail { get; init; } = "The active Git connection has not been read yet.";
}
