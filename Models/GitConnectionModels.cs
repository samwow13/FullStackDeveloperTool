namespace FullStackLauncher.Models;

public enum GitHostingProvider { GitHub, AzureDevOps }

public sealed record GitConnectionTarget
{
    public GitHostingProvider Provider { get; init; }
    public string CloneUrl { get; init; } = "";
    public string Host { get; init; } = "";
    public string Organization { get; init; } = "";
    public bool IsSsh { get; init; }
    public string DisplayName => Provider == GitHostingProvider.GitHub ? "GitHub" : "Azure DevOps";
}

public sealed record GitConnectionCheckResult
{
    public bool Authenticated { get; init; }
    public bool RepositoryAccessible { get; init; }
    public string Message { get; init; } = "";
    public bool Ready => Authenticated && RepositoryAccessible;
}

public sealed class GitRepositoryTrustException(string repositoryRoot)
    : InvalidOperationException("Git blocked this repository because its Windows owner differs from the current user. Review and trust this exact folder in setup only if you recognize it.")
{
    public string RepositoryRoot { get; } = repositoryRoot;
}
