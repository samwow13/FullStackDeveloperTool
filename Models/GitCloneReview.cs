namespace FullStackLauncher.Models;

/// <summary>A validated, empty configured folder and one credential-free clone destination.</summary>
public sealed class GitCloneReview
{
    internal GitCloneReview(string folder, GitHostingProvider provider, string cloneUrl, string directoryIdentity)
    {
        Folder = folder;
        Provider = provider;
        CloneUrl = cloneUrl;
        DirectoryIdentity = directoryIdentity;
    }

    public string Folder { get; }
    public GitHostingProvider Provider { get; }
    public string CloneUrl { get; }
    internal string DirectoryIdentity { get; }
}
