using System.IO;
using System.Runtime.InteropServices;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    /// <summary>Reviews a new workspace and its repositories without downloading or creating folders.</summary>
    public static Task<GitWorkspaceCloneReview> PrepareWorkspaceCloneAsync(string parentFolder, string workspaceName,
        IReadOnlyList<AzureDevOpsWorkspaceRepository> repositories, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        var selections = repositories.ToArray();
        return Task.Run(async () =>
        {
            var parent = RequireFolder(parentFolder);
            var workspace = RequireWorkspaceChildPath(parent, workspaceName, "workspace");
            using var directory = OpenCloneDirectory(parent, out var parentIdentity);
            await RequireNonRepositoryCloneFolderAsync(parent, token).ConfigureAwait(false);
            RequireMissingWorkspaceFolder(workspace);
            var planned = await ReviewWorkspaceRepositoriesAsync(parent, workspace, selections, token).ConfigureAwait(false);
            RequireMissingWorkspaceFolder(workspace);
            return new GitWorkspaceCloneReview(parent, workspaceName, workspace, parentIdentity, planned);
        }, token);
    }

    /// <summary>Creates the reviewed workspace once, then clones and creates each local feature branch in order.</summary>
    public static Task<string> CloneWorkspaceAsync(GitWorkspaceCloneReview review, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(review);
            var parent = RequireFolder(review.ParentFolder);
            var workspace = RequireWorkspaceChildPath(parent, review.WorkspaceName, "workspace");
            if (!string.Equals(workspace, review.Folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The workspace destination changed. Review it again before cloning.");
            var gate = RepositoryGates.GetOrAdd(parent, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            string? currentRepository = null;
            try
            {
                await using var parentLease = await AcquireGitSetupOperationLeaseAsync(parent, token).ConfigureAwait(false);
                using var parentDirectory = OpenCloneDirectory(parent, out var parentIdentity);
                if (!string.Equals(parentIdentity, review.ParentDirectoryIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException("The parent folder changed. Review the workspace again before cloning.");
                await RequireNonRepositoryCloneFolderAsync(parent, token).ConfigureAwait(false);
                RequireMissingWorkspaceFolder(workspace);
                var planned = await ReviewWorkspaceRepositoriesAsync(parent, workspace,
                    review.Repositories.Select(repository => repository.Selection).ToArray(), token).ConfigureAwait(false);
                if (planned.Count != review.Repositories.Count || planned.Where((repository, index) =>
                    repository.Selection != review.Repositories[index].Selection
                    || !string.Equals(repository.Folder, review.Repositories[index].Folder, StringComparison.OrdinalIgnoreCase)).Any())
                    throw new InvalidOperationException("The repository selections changed. Review the workspace again before cloning.");

                token.ThrowIfCancellationRequested();
                CreateNewWorkspaceDirectory(workspace);
                using var workspaceDirectory = OpenCloneDirectory(workspace, out _);
                await using var workspaceLease = await AcquireGitSetupOperationLeaseAsync(workspace, token).ConfigureAwait(false);
                await RequireNonRepositoryCloneFolderAsync(workspace, token).ConfigureAwait(false);
                if (Directory.EnumerateFileSystemEntries(workspace).Any())
                    throw new InvalidOperationException("The new workspace contains unexpected files. Inspect it before continuing.");
                var results = new List<string>();
                foreach (var repository in planned)
                {
                    currentRepository = repository.Repository.Name;
                    token.ThrowIfCancellationRequested();
                    progress?.Report("Cloning " + Sanitize(currentRepository) + "…");
                    CreateNewWorkspaceDirectory(repository.Folder);
                    // Pin each newly created child until its complete clone and feature setup finish.
                    using var repositoryDirectory = OpenCloneDirectory(repository.Folder, out var directoryIdentity);
                    var cloneReview = new GitCloneReview(repository.Folder, GitHostingProvider.AzureDevOps,
                        repository.Repository.CloneUrl, directoryIdentity, repository.StartingBranch, repository.FeatureBranch);
                    var result = await CloneAsync(cloneReview, token).ConfigureAwait(false);
                    results.Add(Sanitize(currentRepository) + ": " + result);
                }
                progress?.Report("Workspace cloned.");
                return "Workspace cloned.\n" + workspace + "\n" + string.Join("\n", results);
            }
            catch (GitCommandExitUnconfirmedException) { throw; }
            catch (OperationCanceledException exception)
            {
                var scope = currentRepository is null ? "workspace setup" : "repository '" + Sanitize(currentRepository) + "'";
                throw new OperationCanceledException("Cloning canceled during " + scope + ". Inspect the workspace before retrying:\n"
                    + workspace + "\n" + Sanitize(exception.Message), exception, exception.CancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                var scope = currentRepository is null ? "Workspace setup" : "Repository '" + Sanitize(currentRepository) + "'";
                throw new InvalidOperationException(scope + " did not complete. Inspect the workspace before retrying:\n"
                    + workspace + "\n" + Sanitize(exception.Message), exception);
            }
            finally { gate.Release(); }
        }, token);

    private static async Task<IReadOnlyList<GitWorkspaceCloneRepositoryReview>> ReviewWorkspaceRepositoriesAsync(
        string parent, string workspace, IReadOnlyList<AzureDevOpsWorkspaceRepository> selections, CancellationToken token)
    {
        if (selections.Count is < 1 or > 128)
            throw new InvalidOperationException("Select between 1 and 128 repositories.");
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<GitWorkspaceCloneRepositoryReview>(selections.Count);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (selection is null || selection.Repository is null)
                throw new InvalidOperationException("A repository identity is missing or invalid. Load the repositories again.");
            var repository = selection.Repository;
            if (string.IsNullOrWhiteSpace(repository.Id)
                || repository.Id.Length > 128 || repository.Id.Any(char.IsControl))
                throw new InvalidOperationException("A repository identity is missing or invalid. Load the repositories again.");
            var projectUrl = "";
            if (!string.IsNullOrEmpty(selection.ProjectUrl))
            {
                projectUrl = AzureDevOpsImportService.ParseProjectUrl(selection.ProjectUrl).ProjectUrl;
                if (new Uri(selection.ProjectUrl.Trim(), UriKind.Absolute).UserInfo.Length != 0)
                    throw new InvalidOperationException("Use an Azure DevOps project URL without credentials.");
            }
            var projectName = selection.ProjectName ?? "";
            if (projectName.Length > 256 || projectName.Any(char.IsControl))
                throw new InvalidOperationException("The Azure DevOps project name is invalid. Load the project again.");
            var folderName = string.IsNullOrEmpty(selection.LocalFolderName) ? repository.Name : selection.LocalFolderName;
            var folder = RequireWorkspaceChildPath(workspace, folderName, "repository");
            if (!folderNames.Add(folderName) || !identities.Add(repository.Id))
                throw new InvalidOperationException("Each repository needs a unique local folder and selection in this workspace.");
            var cleanUrl = GitConnectionService.ValidateUrl(GitHostingProvider.AzureDevOps, repository.CloneUrl);
            if (!urls.Add(cleanUrl))
                throw new InvalidOperationException("The same repository URL was selected more than once.");
            await ValidateWorkspaceBranchesAsync(parent, selection.StartingBranch, selection.FeatureBranch, token).ConfigureAwait(false);
            await RequireCloneRemoteEnvironmentAsync(parent, cleanUrl, token).ConfigureAwait(false);
            var captured = selection with
            {
                Repository = repository with { CloneUrl = cleanUrl },
                ProjectUrl = projectUrl,
                ProjectName = projectName
            };
            planned.Add(new GitWorkspaceCloneRepositoryReview(captured, folder));
        }
        return planned.AsReadOnly();
    }

    private static async Task ValidateWorkspaceBranchesAsync(string folder, string startingBranch, string featureBranch,
        CancellationToken token)
    {
        await ValidateBranchAsync(folder, startingBranch, token).ConfigureAwait(false);
        await ValidateBranchAsync(folder, featureBranch, token).ConfigureAwait(false);
        if (CloneBranchNamesConflict(startingBranch, featureBranch))
            throw new InvalidOperationException("Choose a new feature branch that does not conflict with the starting branch.");
    }

    private static async Task CreateCloneFeatureBranchAsync(string folder, GitRepositorySnapshot snapshot,
        string startingBranch, string featureBranch, string hooksFolder, CancellationToken token)
    {
        RequireNoOperation(snapshot);
        RequireClean(snapshot);
        if (snapshot.IsDetached || snapshot.IsUnborn || snapshot.Branch != startingBranch || !IsObjectId(snapshot.HeadCommit))
            throw new InvalidOperationException("The starting branch tip could not be verified. Inspect the cloned repository.");
        var startingTip = await GitAsync(folder,
            ["show-ref", "--verify", "--hash", "refs/remotes/origin/" + startingBranch], token).ConfigureAwait(false);
        EnsureSuccess(startingTip);
        if (startingTip.Output.Trim() != snapshot.HeadCommit)
            throw new InvalidOperationException("The cloned branch does not match its downloaded starting tip. Inspect the repository.");
        var current = await ReadCoreAsync(folder, token).ConfigureAwait(false);
        RequireSameLocalState(snapshot, current);
        if (current.Branches.Any(branch => !branch.IsRemote && CloneBranchNamesConflict(branch.Name, featureBranch)))
            throw new InvalidOperationException("The feature branch already exists locally. Inspect it before continuing.");
        if (current.Branches.Any(branch => branch.IsRemote && branch.Name.StartsWith("origin/", StringComparison.Ordinal)
            && CloneBranchNamesConflict(branch.Name["origin/".Length..], featureBranch)))
            throw new InvalidOperationException("The feature branch conflicts with an existing remote branch. Choose a new feature branch name.");
        EnsureSuccess(await GitAsync(folder,
            ["-c", "core.hooksPath=" + hooksFolder, "-c", "core.fsmonitor=false", "-c", "core.protectNTFS=true",
                "-c", "core.protectHFS=true", "switch", "--no-guess", "--no-track", "--no-overwrite-ignore",
                "--no-recurse-submodules", "-c", featureBranch, snapshot.HeadCommit], token).ConfigureAwait(false));
        var created = await ReadCoreAsync(folder, token).ConfigureAwait(false);
        RequireExactRoot(folder, created);
        if (created.IsDetached || created.IsUnborn || created.Branch != featureBranch || created.HeadCommit != snapshot.HeadCommit
            || created.WorkingTreeFingerprint != snapshot.WorkingTreeFingerprint || created.OperationState is not null)
            throw new InvalidOperationException("Feature branch creation could not be verified. Inspect the repository; no rollback was attempted.");
    }

    private static bool CloneBranchNamesConflict(string first, string second) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);

    private static string RequireWorkspaceChildPath(string parent, string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 255 || name.Any(char.IsControl)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')
            || name is "." or ".." || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a valid Windows " + kind + " folder name without slashes or trailing spaces or dots.");
        var stem = name.Split('.')[0].TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
            throw new InvalidOperationException("That " + kind + " folder name is reserved by Windows. Choose another name.");
        try
        {
            var folder = Path.GetFullPath(Path.Combine(parent, name));
            if (!string.Equals(Path.GetDirectoryName(folder), parent, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException();
            return folder;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { throw new InvalidOperationException("The " + kind + " folder path is invalid."); }
    }

    private static void RequireMissingWorkspaceFolder(string folder)
    {
        try
        {
            _ = File.GetAttributes(folder);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The new workspace destination cannot be verified. Check its parent folder permissions."); }
        throw new InvalidOperationException("That workspace destination already exists. Choose a new workspace name.");
    }

    private static void CreateNewWorkspaceDirectory(string folder)
    {
        // CreateDirectoryW fails if any entry already has this name; never adopt another operation's folder.
        if (CreateWorkspaceDirectory(folder, IntPtr.Zero)) return;
        var error = Marshal.GetLastWin32Error();
        if (error is 80 or 183)
            throw new InvalidOperationException("A workspace or repository folder already exists. Choose a new workspace name; no files were overwritten.");
        throw new IOException("The new workspace or repository folder could not be created (Windows error " + error + ").");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWorkspaceDirectory(string path, IntPtr security);
}
