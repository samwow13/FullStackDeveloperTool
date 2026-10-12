namespace FullStackLauncher.Models;

/// <summary>A validated, empty configured folder and one credential-free clone destination.</summary>
public sealed class GitCloneReview
{
    internal GitCloneReview(string folder, GitHostingProvider provider, string cloneUrl, string directoryIdentity,
        string? branch = null, string? featureBranch = null, bool useAzureDevOpsToken = false)
    {
        Folder = folder;
        Provider = provider;
        CloneUrl = cloneUrl;
        DirectoryIdentity = directoryIdentity;
        Branch = branch;
        FeatureBranch = featureBranch;
        UseAzureDevOpsToken = useAzureDevOpsToken;
    }

    public string Folder { get; }
    public GitHostingProvider Provider { get; }
    public string CloneUrl { get; }
    public string? Branch { get; }
    public string? FeatureBranch { get; }
    public bool UseAzureDevOpsToken { get; }
    internal string DirectoryIdentity { get; }
}

/// <summary>A new or existing workspace destination, reviewed without creating folders.</summary>
public sealed class GitWorkspaceCloneReview
{
    internal GitWorkspaceCloneReview(string parentFolder, string workspaceName, string folder,
        string parentDirectoryIdentity, IReadOnlyList<GitWorkspaceCloneRepositoryReview> repositories,
        bool usesExistingWorkspace = false)
    {
        ParentFolder = parentFolder;
        WorkspaceName = workspaceName;
        Folder = folder;
        ParentDirectoryIdentity = parentDirectoryIdentity;
        Repositories = Array.AsReadOnly(repositories.ToArray());
        UsesExistingWorkspace = usesExistingWorkspace;
    }

    public string ParentFolder { get; }
    public string WorkspaceName { get; }
    public string Folder { get; }
    public bool UsesExistingWorkspace { get; }
    public IReadOnlyList<GitWorkspaceCloneRepositoryReview> Repositories { get; }
    internal string ParentDirectoryIdentity { get; }
}

public sealed class GitWorkspaceCloneRepositoryReview
{
    internal GitWorkspaceCloneRepositoryReview(AzureDevOpsWorkspaceRepository selection, string folder)
    {
        Selection = selection;
        Folder = folder;
    }

    public AzureDevOpsWorkspaceRepository Selection { get; }
    public AzureDevOpsImportRepository Repository => Selection.Repository;
    public string ProjectName => Selection.ProjectName;
    public string StartingBranch => Selection.StartingBranch;
    public string FeatureBranch => Selection.FeatureBranch;
    public string Folder { get; }
}
