namespace FullStackLauncher.Models;

public sealed record AzureDevOpsProjectTarget
{
    public string Organization { get; init; } = "";
    public string Project { get; init; } = "";
    public string ProjectUrl { get; init; } = "";
}

public sealed record AzureDevOpsImportRepository
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string CloneUrl { get; init; } = "";
    public string DefaultBranch { get; init; } = "";
}

public sealed record AzureDevOpsWorkspaceRepository
{
    public AzureDevOpsImportRepository Repository { get; init; } = new();
    public string ProjectUrl { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string LocalFolderName { get; init; } = "";
    public string StartingBranch { get; init; } = "";
    public string FeatureBranch { get; init; } = "";
}
