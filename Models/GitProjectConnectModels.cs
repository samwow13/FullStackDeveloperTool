namespace FullStackLauncher.Models;

public enum GitProjectConnectMode
{
    EmptyRemoteInPlace,
    CopyIntoRepository
}

/// <summary>Explicit local source, hosted destination, and integration choices. Contains no credentials.</summary>
public sealed record GitProjectConnectRequest
{
    public string SourceFolder { get; init; } = "";
    public GitHostingProvider Provider { get; init; }
    public string CloneUrl { get; init; } = "";
    public GitProjectConnectMode Mode { get; init; }
    public string? DestinationFolder { get; init; }
    public string? RepositorySubfolder { get; init; }
    public string Branch { get; init; } = "main";
    public string? StartingBranch { get; init; }
    public string? FeatureBranch { get; init; }
}

/// <summary>A read-only remote check and content inventory, frozen for explicit review before local changes.</summary>
public sealed class GitProjectConnectReview
{
    internal GitProjectConnectReview(GitProjectConnectRequest request, string repositoryFolder,
        string projectFolder, string sourceIdentity, string parentFolder, string parentIdentity,
        string cloneFolder, string? startingCommit, IReadOnlyList<GitProjectConnectFile> files,
        IReadOnlyList<string> excludedEntries, IReadOnlyList<GitProjectConnectDirectory> directories)
    {
        Request = request;
        RepositoryFolder = repositoryFolder;
        ProjectFolder = projectFolder;
        SourceIdentity = sourceIdentity;
        ParentFolder = parentFolder;
        ParentIdentity = parentIdentity;
        CloneFolder = cloneFolder;
        StartingCommit = startingCommit;
        Files = Array.AsReadOnly(files.ToArray());
        ExcludedEntries = Array.AsReadOnly(excludedEntries.ToArray());
        Directories = Array.AsReadOnly(directories.ToArray());
    }

    public GitProjectConnectRequest Request { get; }
    public string SourceFolder => Request.SourceFolder;
    public string RepositoryFolder { get; }
    public string ProjectFolder { get; }
    public bool RemoteIsEmpty => StartingCommit is null;
    public string? StartingBranch => Request.StartingBranch;
    public string? StartingCommit { get; }
    public int IncludedFileCount => Files.Count;
    public long IncludedBytes => Files.Sum(file => file.Length);
    public IReadOnlyList<string> ExcludedEntries { get; }
    public string Summary => Request.Mode == GitProjectConnectMode.EmptyRemoteInPlace
        ? $"Connect this folder to the verified empty remote on local branch '{Request.Branch}'. Existing files remain in place and uncommitted."
        : $"Clone '{StartingBranch}', then copy {IncludedFileCount:N0} source files into '{Request.RepositorySubfolder}'. The original folder stays unchanged. Excluded files, including local settings and credentials, remain only in the original folder and may require setup before services can run from the copy.";

    internal string SourceIdentity { get; }
    internal string ParentFolder { get; }
    internal string ParentIdentity { get; }
    internal string CloneFolder { get; }
    internal IReadOnlyList<GitProjectConnectFile> Files { get; }
    internal IReadOnlyList<GitProjectConnectDirectory> Directories { get; }
}

public sealed record GitProjectConnectResult(string RepositoryRoot, string ProjectFolder,
    string SourceFolder, string Branch, string Message);

internal sealed record GitProjectConnectFile(string RelativePath, long Length, long LastWriteTicks, string Hash);
internal sealed record GitProjectConnectDirectory(string RelativePath, string Identity);
