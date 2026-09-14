using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Explicit Git operations. Passive repository reads never contact a remote.</summary>
public static class GitRepositoryService
{
    private const int OutputLimit = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<GitRepositorySnapshot?> ExpectedSnapshot = new();
    private static readonly Regex RemoteNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex ScpUrlPattern = new("^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9][A-Za-z0-9.-]*):(?<path>[^\\s:]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex UrlInOutput = new(@"[A-Za-z][A-Za-z0-9+.-]*://[^\s<>""']+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static Task<GitRepositorySnapshot> ReadAsync(string folder, CancellationToken token = default) =>
        Task.Run(() => ReadCoreAsync(folder, token), token);

    public static Task<GitRemoteComparison> ReadComparisonAsync(string root, string remote, string branch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            var comparison = new GitRemoteComparison
            {
                Remote = remote, Branch = branch,
                UntrackedFiles = snapshot.Changes.Count(change => change.IndexStatus == "?" && change.WorkTreeStatus == "?")
            };
            if (snapshot.IsDetached || snapshot.Branch != branch)
                return comparison with { UnavailableReason = "Select a current local branch to compare with its remote." };
            if (snapshot.IsUnborn)
                return comparison with { UnavailableReason = "Create the first commit to compare this branch with its remote." };
            if (snapshot.Changes.Any(change => change.IsConflict))
                return comparison with { UnavailableReason = "Resolve the merge conflicts before comparing line totals." };
            await ValidateBranchAsync(root, branch, token).ConfigureAwait(false);
            var fetchUrl = await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            var pushUrl = await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
            if (!string.Equals(fetchUrl, pushUrl, StringComparison.Ordinal))
                return comparison with { UnavailableReason = "This remote has different fetch and push targets; their line totals cannot be compared here." };
            var remoteBranch = snapshot.Branches.FirstOrDefault(item => item.IsRemote && item.Name == remote + "/" + branch);
            if (remoteBranch is null)
                return comparison with { UnavailableReason = "No cached remote branch. Fetch to check for it, or push to publish this branch." };
            var counts = await GitAsync(root, ["rev-list", "--left-right", "--count", snapshot.HeadCommit + "..." + remoteBranch.CommitId, "--"], token).ConfigureAwait(false);
            EnsureSuccess(counts);
            var numbers = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 2 || !int.TryParse(numbers[0], out var ahead) || ahead < 0 || !int.TryParse(numbers[1], out var behind) || behind < 0)
                throw new InvalidOperationException("Git returned invalid ahead/behind counts.");
            var diff = await GitAsync(root,
                ["diff", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--ignore-submodules=none", remoteBranch.CommitId, "--"], token).ConfigureAwait(false);
            EnsureSuccess(diff);
            var totals = ParseNumStat(diff.Output);
            var final = await ReadCoreAsync(root, token).ConfigureAwait(false);
            if (final.StateFingerprint != snapshot.StateFingerprint)
                throw new InvalidOperationException("The repository changed while reading its comparison. Refresh to obtain current totals.");
            return comparison with
            {
                Available = true, Ahead = ahead, Behind = behind, ChangedFiles = totals.Files,
                AddedLines = totals.Added, DeletedLines = totals.Deleted, BinaryFiles = totals.Binary
            };
        });

    /// <summary>Retains the reviewed repository/index/remote identity across UI confirmation and command validation.</summary>
    public static async Task<string> RunWithSnapshotAsync(GitRepositorySnapshot expected, Func<CancellationToken, Task<string>> action, CancellationToken token = default)
    {
        var previous = ExpectedSnapshot.Value;
        ExpectedSnapshot.Value = expected;
        try { return await action(token).ConfigureAwait(false); }
        finally { ExpectedSnapshot.Value = previous; }
    }

    public static Task<string> InitializeAsync(string folder, string branch, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            var fullFolder = RequireFolder(folder);
            var gate = RepositoryGates.GetOrAdd(fullFolder, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ValidateBranchAsync(fullFolder, branch, token).ConfigureAwait(false);
                var existing = await GitAsync(fullFolder, ["rev-parse", "--git-dir"], token).ConfigureAwait(false);
                var metadata = await GitBranchReader.ReadAsync(fullFolder, token).ConfigureAwait(false);
                if (existing.ExitCode == 0 || metadata.State != GitBranchState.NotRepository)
                    throw new InvalidOperationException("This folder is already inside a repository, or its Git metadata cannot be verified. Refresh and select the existing repository.");
                var result = await GitAsync(fullFolder, ["init", "--initial-branch=" + branch, "--", fullFolder], token).ConfigureAwait(false);
                return Result(result, "Repository initialized. Review files before staging and creating the first commit.");
            }
            finally { gate.Release(); }
        }, token);

    public static Task<string> SaveRemoteAsync(string root, string name, string url, bool replace, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            ValidateRemoteName(name);
            var cleanUrl = ValidateRemoteUrl(url);
            var exists = snapshot.Remotes.Any(remote => remote.Name == name);
            if (exists != replace)
                throw new InvalidOperationException(exists ? "That remote now exists. Refresh and explicitly choose to update it." : "That remote no longer exists. Refresh and add it again.");
            if (replace)
            {
                await RequireSingleRemoteAsync(root, name, false, token, validateUrls: false).ConfigureAwait(false);
                var push = await GitAsync(root, ["config", "--get-all", $"remote.{name}.pushurl"], token).ConfigureAwait(false);
                if (push.ExitCode == 0 && !string.IsNullOrWhiteSpace(push.Output))
                    throw new InvalidOperationException("This remote has a separate push URL. Update its fetch/push configuration with Git before editing it here.");
                if (push.ExitCode is not (0 or 1)) ThrowCommandFailure(push);
            }
            var args = replace ? new[] { "remote", "set-url", name, cleanUrl } : new[] { "remote", "add", name, cleanUrl };
            return Result(await GitAsync(root, args, token).ConfigureAwait(false), "Remote saved. The repository has not been pushed.");
        });

    public static Task<string> FetchAsync(string root, string remote, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async _ =>
        {
            await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            return Result(await GitAsync(root,
                ["-c", "fetch.pruneTags=false", "-c", $"remote.{remote}.pruneTags=false", "fetch", "--prune", "--no-prune-tags", "--no-tags", "--no-recurse-submodules", "--", remote, $"+refs/heads/*:refs/remotes/{remote}/*"], token, network: true).ConfigureAwait(false),
                "Fetch completed. Remote branch information is now refreshed.");
        });

    public static Task<string> PullAsync(string root, string remote, string branch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireCurrentBranch(snapshot, branch);
            RequireClean(snapshot);
            RequireNoOperation(snapshot);
            await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            return Result(await GitAsync(root,
                ["-c", $"branch.{branch}.mergeOptions=--no-overwrite-ignore", "pull", "--ff-only", "--no-rebase", "--no-autostash", "--no-prune", "--no-tags", "--no-recurse-submodules", $"--refmap=+refs/heads/*:refs/remotes/{remote}/*", "--", remote, "refs/heads/" + branch], token, network: true).ConfigureAwait(false),
                "Pull completed using fast-forward only.");
        });

    public static Task<string> PushAsync(string root, string remote, string branch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireCurrentBranch(snapshot, branch);
            RequireNoOperation(snapshot);
            await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
            return await PushCommitAsync(root, remote, branch, snapshot.HeadCommit, token).ConfigureAwait(false);
        });

    public static Task<string> StageAllCommitAndPushAsync(string root, string message, string remote, string branch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            ValidateCommitMessage(message);
            RequireNamedBranch(snapshot, branch);
            RequireNoOperation(snapshot);
            if (snapshot.Changes.Any(change => change.IsConflict)) throw new InvalidOperationException("Resolve all conflicts before committing.");
            if (snapshot.Changes.Count == 0) throw new InvalidOperationException("There are no changes to commit. Use Push for existing local commits.");
            await ValidateBranchAsync(root, branch, token).ConfigureAwait(false);
            var destination = await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
            // Check identity before staging so a missing Git identity leaves the index alone.
            EnsureSuccess(await GitAsync(root, ["var", "GIT_AUTHOR_IDENT"], token).ConfigureAwait(false));
            EnsureSuccess(await GitAsync(root, ["var", "GIT_COMMITTER_IDENT"], token).ConfigureAwait(false));
            var staged = false;
            var committed = false;
            try
            {
                EnsureSuccess(await GitAsync(root, ["add", "--all", "--", ":/"], token).ConfigureAwait(false));
                staged = true;
                var stagedSnapshot = await ReadCoreAsync(root, token).ConfigureAwait(false);
                RequireExactRoot(root, stagedSnapshot);
                RequireNamedBranch(stagedSnapshot, branch);
                RequireNoOperation(stagedSnapshot);
                if (stagedSnapshot.HeadCommit != snapshot.HeadCommit)
                    throw new InvalidOperationException("The branch commit changed while staging. Review the staged changes before retrying.");
                if (stagedSnapshot.Changes.Any(change => change.IsConflict))
                    throw new InvalidOperationException("Resolve all conflicts before committing.");
                if (!stagedSnapshot.Changes.Any(change => change.HasStaged))
                    throw new InvalidOperationException("There are no staged file changes to commit. Changes inside submodules must be committed in their own repositories.");
                await RequireUnchangedPushDestinationAsync(root, remote, destination, token).ConfigureAwait(false);
                var tree = await GitAsync(root, ["write-tree"], token).ConfigureAwait(false);
                EnsureSuccess(tree);
                var stagedTree = tree.Output.Trim();
                if (!IsObjectId(stagedTree)) throw new InvalidOperationException("The staged tree could not be verified. Review the index before committing.");
                var commit = await GitAsync(root, ["commit", "--no-status", "--message", message], token).ConfigureAwait(false);
                EnsureSuccess(commit);
                committed = true;
                var committedSnapshot = await ReadCoreAsync(root, token).ConfigureAwait(false);
                RequireExactRoot(root, committedSnapshot);
                RequireCurrentBranch(committedSnapshot, branch);
                RequireNoOperation(committedSnapshot);
                if (committedSnapshot.HeadCommit == snapshot.HeadCommit)
                    throw new InvalidOperationException("Git reported a commit, but the new branch tip could not be verified. Inspect the local history before pushing.");
                var identity = await GitAsync(root, ["show", "--no-patch", "--no-notes", "--no-show-signature", "--format=%T%x00%P", committedSnapshot.HeadCommit, "--"], token).ConfigureAwait(false);
                EnsureSuccess(identity);
                var fields = identity.Output.TrimEnd('\r', '\n').Split('\0');
                if (fields.Length != 2 || fields[0] != stagedTree || fields[1] != snapshot.HeadCommit)
                    throw new InvalidOperationException("The resulting commit differs from the staged tree or expected parent. Inspect the local history before pushing.");
                await RequireUnchangedPushDestinationAsync(root, remote, destination, token).ConfigureAwait(false);
                return "All changes committed. " + await PushCommitAsync(root, remote, branch, committedSnapshot.HeadCommit, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                var state = committed
                    ? "The commit was created locally, but pushing was not confirmed. Refresh and inspect the remote status; use Push for this commit when ready."
                    : staged
                        ? "All changes were staged, but the commit was not confirmed and nothing was pushed. Review the local history and staged changes before retrying."
                        : "Staging was not confirmed; no commit or push was attempted. Refresh and review the index before retrying.";
                throw new GitCommitPushException(state + "\n" + Sanitize(exception.Message), committed, exception);
            }
        });

    public static Task<string> MergeBranchAsync(string root, string branchName, string currentBranch, CancellationToken token = default, bool sourceIsRemote = false) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireCurrentBranch(snapshot, currentBranch);
            RequireClean(snapshot);
            RequireNoOperation(snapshot);
            var source = snapshot.Branches.FirstOrDefault(item => item.Name == branchName && item.IsRemote == sourceIsRemote && !item.IsCurrent)
                ?? throw new InvalidOperationException("Select another available local or fetched remote branch to merge.");
            await ValidateBranchAsync(root, source.Name, token).ConfigureAwait(false);
            var merge = await GitAsync(root,
                ["-c", $"branch.{currentBranch}.mergeOptions=", "-c", "submodule.recurse=false", "-c", "rerere.enabled=false", "-c", "rerere.autoupdate=false", "merge", "--ff", "--commit", "--no-edit", "--no-autostash", "--no-overwrite-ignore", "--no-squash", "--message", $"Merge branch '{source.Name}' into {currentBranch}", "--", source.CommitId], token).ConfigureAwait(false);
            if (merge.ExitCode != 0)
            {
                try { ThrowCommandFailure(merge); }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException("Merge did not complete. Any conflicts remain in the working tree; resolve or abort the merge with Git before continuing.\n" + exception.Message, exception);
                }
            }
            return Result(merge, "Merged " + source.Name + " into " + currentBranch + ".");
        });

    public static Task<string> CheckRemoteBranchAsync(string root, string remote, string branch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async _ =>
        {
            await ValidateBranchAsync(root, branch, token).ConfigureAwait(false);
            await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            var result = await GitAsync(root, ["ls-remote", "--exit-code", "--heads", "--", remote, "refs/heads/" + branch], token, network: true).ConfigureAwait(false);
            if (result.ExitCode == 2) return "The branch does not exist on the selected remote (checked now).";
            EnsureSuccess(result);
            var matches = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.TrimEnd('\r').EndsWith("\trefs/heads/" + branch, StringComparison.Ordinal));
            if (!matches) throw new InvalidOperationException("The remote branch response could not be verified. Its existence is unknown.");
            return "The branch exists on the selected remote (checked now).";
        });

    public static Task<string> CreateBranchAsync(string root, string name, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            await ValidateBranchAsync(root, name, token).ConfigureAwait(false);
            RequireNoOperation(snapshot);
            RequireClean(snapshot);
            if (snapshot.IsUnborn) throw new InvalidOperationException("Create the first commit before creating another branch.");
            if (snapshot.Branches.Any(branch => !branch.IsRemote && branch.Name == name))
                throw new InvalidOperationException("That local branch already exists. Refresh and select Switch branch.");
            return Result(await GitAsync(root, ["switch", "--no-guess", "--no-overwrite-ignore", "--no-recurse-submodules", "-c", name], token).ConfigureAwait(false), "Branch created and checked out.");
        });

    public static Task<string> SwitchBranchAsync(string root, string name, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            RequireClean(snapshot);
            var local = snapshot.Branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == name);
            if (local is not null)
            {
                await ValidateBranchAsync(root, name, token).ConfigureAwait(false);
                return Result(await GitAsync(root, ["switch", "--no-guess", "--no-overwrite-ignore", "--no-recurse-submodules", name], token).ConfigureAwait(false), "Branch checked out.");
            }
            var remote = snapshot.Branches.FirstOrDefault(branch => branch.IsRemote && branch.Name == name);
            if (remote is null) throw new InvalidOperationException("That branch is no longer available. Refresh the branch list.");
            var slash = name.IndexOf('/');
            if (slash < 1) throw new InvalidOperationException("The remote branch cannot be mapped to a local branch.");
            var localName = name[(slash + 1)..];
            await ValidateBranchAsync(root, localName, token).ConfigureAwait(false);
            if (snapshot.Branches.Any(branch => !branch.IsRemote && branch.Name == localName))
                throw new InvalidOperationException("A local branch with that name already exists. Select the local branch explicitly.");
            return Result(await GitAsync(root, ["switch", "--no-guess", "--no-overwrite-ignore", "--no-recurse-submodules", "--track", "-c", localName, "refs/remotes/" + name], token).ConfigureAwait(false), "Local tracking branch created and checked out.");
        });

    public static Task<string> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            var selected = ValidatePaths(root, paths, snapshot, staged: false);
            return Result(await GitAsync(root, ["--literal-pathspecs", "add", "--", .. selected], token).ConfigureAwait(false), "Selected paths staged. Review the staged diff before committing.");
        });

    public static Task<string> UnstageAsync(string root, IReadOnlyList<string> paths, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            var selected = ValidatePaths(root, paths, snapshot, staged: true);
            // On an unborn branch, remove only the selected index entries. --cached always
            // preserves working files; --force also permits files edited after staging.
            var args = snapshot.IsUnborn
                ? new[] { "--literal-pathspecs", "rm", "--cached", "--force", "--ignore-unmatch", "--" }.Concat(selected).ToArray()
                : new[] { "--literal-pathspecs", "restore", "--staged", "--" }.Concat(selected).ToArray();
            return Result(await GitAsync(root, args, token).ConfigureAwait(false), "Selected paths unstaged. Working files were preserved.");
        });

    public static Task<string> CommitAsync(string root, string message, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            if (snapshot.IsDetached) throw new InvalidOperationException("Switch to a local branch before committing here.");
            if (snapshot.Changes.Any(change => change.IsConflict)) throw new InvalidOperationException("Resolve all conflicts before committing.");
            if (!snapshot.Changes.Any(change => change.HasStaged)) throw new InvalidOperationException("Stage the files to include before committing.");
            ValidateCommitMessage(message);
            return Result(await GitAsync(root, ["commit", "--no-status", "--message", message], token).ConfigureAwait(false), "Commit created from the staged changes.");
        });

    public static Task<string> DiffAsync(string root, string path, bool staged, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            var selected = snapshot.Changes.FirstOrDefault(change => change.Path == path)
                ?? throw new InvalidOperationException("That path is no longer changed. Refresh the working tree.");
            ValidateRelativePath(root, path);
            if (!staged && selected.IndexStatus == "?" && selected.WorkTreeStatus == "?")
                return "Untracked file. Stage it to inspect its added contents in the staged diff.";
            var paths = selected.OriginalPath is null ? new[] { path } : new[] { path, selected.OriginalPath };
            var args = new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames" };
            if (staged) args.Add("--cached");
            args.Add("--");
            args.AddRange(paths);
            var result = await GitAsync(root, args, token).ConfigureAwait(false);
            EnsureSuccess(result);
            return string.IsNullOrWhiteSpace(result.Output) ? "No diff in this view. Binary files and submodules may have limited textual output." : DisplayOutput(result.Output);
        });

    public static Task<string> LookupAzureRemoteAsync(string folder, string organization, string project, string repositoryName, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            var organizationUrl = ValidateAzureOrganization(organization);
            ValidateAzureName(project, "project");
            ValidateAzureName(repositoryName, "repository");
            var result = await AzureAsync(RequireFolder(folder),
                ["repos", "show", "--organization", organizationUrl, "--project", project.Trim(), "--repository", repositoryName.Trim(), "--detect", "false", "--query", "remoteUrl", "--output", "tsv", "--only-show-errors"], token).ConfigureAwait(false);
            EnsureSuccess(result);
            return CleanAzureCloneUrl(result.Output.Trim(), organizationUrl);
        }, token);

    public static Task<string> CreateAzureRemoteAsync(string root, string remoteName, string organization, string project, string repositoryName, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            ValidateRemoteName(remoteName);
            if (snapshot.Remotes.Any(remote => remote.Name == remoteName)) throw new InvalidOperationException("That local remote already exists. Choose a new remote name before creating a repository.");
            var organizationUrl = ValidateAzureOrganization(organization);
            ValidateAzureName(project, "project");
            ValidateAzureName(repositoryName, "repository");
            var created = await AzureAsync(root,
                ["repos", "create", "--organization", organizationUrl, "--project", project.Trim(), "--name", repositoryName.Trim(), "--detect", "false", "--query", "remoteUrl", "--output", "tsv", "--only-show-errors"], token).ConfigureAwait(false);
            EnsureSuccess(created);
            string cloneUrl;
            try { cloneUrl = CleanAzureCloneUrl(created.Output.Trim(), organizationUrl); }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("Azure DevOps reported repository creation, but its clone URL could not be verified. Check the repository in Azure DevOps and use Look up existing; do not retry creation automatically.");
            }
            try
            {
                var current = await ReadCoreAsync(root, token).ConfigureAwait(false);
                RequireExactRoot(root, current);
                if (current.StateFingerprint != snapshot.StateFingerprint) throw new InvalidOperationException("The local repository changed while Azure DevOps was creating the remote repository.");
                if (current.Remotes.Any(remote => remote.Name == remoteName)) throw new InvalidOperationException("The local remote name was added by another operation.");
                EnsureSuccess(await GitAsync(root, ["remote", "add", remoteName, cloneUrl], token).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
                throw new InvalidOperationException($"Azure DevOps repository created at {cloneUrl}, but attaching the local remote was not confirmed. Refresh and attach this existing URL; do not create it again. {Sanitize(exception.Message)}");
            }
            return "Azure DevOps repository created and its local remote attached. Access follows the Azure project permissions. No commits were pushed.\n" + cloneUrl;
        });

    private static Task<T> InRepositoryAsync<T>(string root, CancellationToken token, Func<GitRepositorySnapshot, Task<T>> action) =>
        Task.Run(async () =>
        {
            var fullRoot = RequireFolder(root);
            var gate = RepositoryGates.GetOrAdd(fullRoot, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var snapshot = await ReadCoreAsync(fullRoot, token).ConfigureAwait(false);
                RequireExactRoot(fullRoot, snapshot);
                if (ExpectedSnapshot.Value is { } expected && (expected.RepositoryRoot is null
                    || !string.Equals(expected.RepositoryRoot, snapshot.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
                    || expected.StateFingerprint.Length == 0 || expected.StateFingerprint != snapshot.StateFingerprint))
                    throw new InvalidOperationException("The repository branch, staged files, working tree, or remote configuration changed since the displayed review. Refresh and review the current state before trying this action again.");
                return await action(snapshot).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }, token);

    private static async Task<GitRepositorySnapshot> ReadCoreAsync(string folder, CancellationToken token)
    {
        var fullFolder = RequireFolder(folder);
        var top = await GitAsync(fullFolder, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
        if (top.ExitCode != 0)
        {
            var metadata = await GitBranchReader.ReadAsync(fullFolder, token).ConfigureAwait(false);
            var bare = await GitAsync(fullFolder, ["rev-parse", "--is-bare-repository"], token).ConfigureAwait(false);
            if (bare.ExitCode == 0 && bare.Output.Trim() == "true")
                throw new InvalidOperationException("Bare repositories have no working tree and are not supported in this workspace.");
            if (metadata.State == GitBranchState.NotRepository)
                return new GitRepositorySnapshot { Folder = fullFolder };
            ThrowCommandFailure(top);
        }
        var root = RequireFolder(top.Output.TrimEnd('\r', '\n'));
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none"], token).ConfigureAwait(false);
        EnsureSuccess(status);
        var changes = ParseChanges(status.Output);
        var symbolic = await GitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
        if (symbolic.ExitCode is not (0 or 1)) ThrowCommandFailure(symbolic);
        var head = await GitAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], token).ConfigureAwait(false);
        var detached = symbolic.ExitCode == 1;
        var unborn = !detached && head.ExitCode != 0;
        if (detached && head.ExitCode != 0) ThrowCommandFailure(head);
        if (unborn)
        {
            var branchRef = await GitAsync(root, ["show-ref", "--verify", "--quiet", "refs/heads/" + symbolic.Output.TrimEnd('\r', '\n')], token).ConfigureAwait(false);
            if (branchRef.ExitCode != 1) ThrowCommandFailure(head);
        }
        var branch = detached ? head.Output.Trim()[..Math.Min(12, head.Output.Trim().Length)] : symbolic.Output.TrimEnd('\r', '\n');
        var refs = await GitAsync(root, ["for-each-ref", "--format=%(refname)%00%(HEAD)%00%(upstream:short)%00%(symref)%00%(objectname)", "refs/heads/", "refs/remotes/"], token).ConfigureAwait(false);
        EnsureSuccess(refs);
        var branches = new List<GitBranchInfo>();
        foreach (var line in refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length != 5 || !IsObjectId(fields[4])) throw new InvalidOperationException("Git returned an incomplete branch list. Refresh before continuing.");
            if (fields[3].Length != 0) continue; // Remote HEAD is a symbolic alias, not a branch.
            var remote = fields[0].StartsWith("refs/remotes/", StringComparison.Ordinal);
            var prefix = remote ? "refs/remotes/" : "refs/heads/";
            if (!fields[0].StartsWith(prefix, StringComparison.Ordinal)) continue;
            branches.Add(new GitBranchInfo { Name = fields[0][prefix.Length..], CommitId = fields[4], IsRemote = remote, IsCurrent = fields[1] == "*", Upstream = NullIfEmpty(fields[2]) });
        }
        var upstream = branches.FirstOrDefault(item => item.IsCurrent)?.Upstream;
        var ahead = 0;
        var behind = 0;
        var trackingAvailable = false;
        if (!unborn && !detached && upstream is not null)
        {
            var counts = await GitAsync(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}", "--"], token).ConfigureAwait(false);
            if (counts.ExitCode == 0)
            {
                var numbers = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (numbers.Length != 2 || !int.TryParse(numbers[0], out ahead) || !int.TryParse(numbers[1], out behind))
                    throw new InvalidOperationException("Git returned invalid ahead/behind counts.");
                trackingAvailable = true;
            }
        }
        var names = await GitAsync(root, ["remote"], token).ConfigureAwait(false);
        EnsureSuccess(names);
        var remotes = new List<GitRemoteInfo>();
        var remoteIdentity = new StringBuilder();
        foreach (var name in names.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsValidRemoteName(name))
            {
                remotes.Add(new GitRemoteInfo { Name = Sanitize(name), FetchUrl = "Unsupported remote name", PushUrl = "Unsupported remote name" });
                continue;
            }
            var fetchResult = await GitAsync(root, ["remote", "get-url", "--all", name], token).ConfigureAwait(false);
            var pushResult = await GitAsync(root, ["remote", "get-url", "--push", "--all", name], token).ConfigureAwait(false);
            EnsureSuccess(fetchResult);
            EnsureSuccess(pushResult);
            var fetchUrls = SplitUrls(fetchResult.Output);
            var pushUrls = SplitUrls(pushResult.Output);
            remoteIdentity.Append(name).Append('\0').Append(fetchResult.Output).Append('\0').Append(pushResult.Output).Append('\0');
            var canCopy = fetchUrls.Length == 1 && pushUrls.Length == 1 && IsSafeRemoteUrl(fetchUrls[0]) && IsSafeRemoteUrl(pushUrls[0]);
            remotes.Add(new GitRemoteInfo
            {
                Name = name,
                FetchUrl = string.Join("\n", fetchUrls.Select(DisplayRemoteUrl)),
                PushUrl = string.Join("\n", pushUrls.Select(DisplayRemoteUrl)),
                UrlCanCopy = canCopy
            });
        }
        var operation = await ReadOperationStateAsync(root, token).ConfigureAwait(false);
        var indexEntries = await GitAsync(root, ["ls-files", "--stage", "-z"], token).ConfigureAwait(false);
        EnsureSuccess(indexEntries);
        var finalSymbolic = await GitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
        var finalHead = await GitAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], token).ConfigureAwait(false);
        if (symbolic.ExitCode != finalSymbolic.ExitCode || symbolic.Output != finalSymbolic.Output || head.ExitCode != finalHead.ExitCode || head.Output != finalHead.Output)
            throw new InvalidOperationException("The checked-out branch or commit changed while reading Git status. Refresh to obtain a consistent view.");
        var identity = string.Join('\0', root, symbolic.Output, head.Output, status.Output, indexEntries.Output, refs.Output, names.Output, remoteIdentity.ToString(), operation ?? "");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new GitRepositorySnapshot
        {
            Folder = fullFolder, RepositoryRoot = root, IsRepository = true, Branch = branch, HeadCommit = unborn ? "" : head.Output.Trim(),
            IsDetached = detached, IsUnborn = unborn, Upstream = upstream, Ahead = ahead, Behind = behind, TrackingAvailable = trackingAvailable,
            StateFingerprint = fingerprint,
            Changes = changes, Branches = branches, Remotes = remotes, OperationState = operation
        };
    }

    private static IReadOnlyList<GitChange> ParseChanges(string output)
    {
        if (output.Length == 0) return [];
        if (output[^1] != '\0') throw new InvalidOperationException("Git returned an incomplete working tree listing.");
        var records = output.Split('\0');
        var result = new List<GitChange>();
        for (var index = 0; index < records.Length - 1; index++)
        {
            var entry = records[index];
            if (entry.Length < 4 || entry[2] != ' ') throw new InvalidOperationException("Git returned an unsupported working tree record.");
            var x = entry[0];
            var y = entry[1];
            if (!" MADRCU?!T".Contains(x) || !" MADRCU?!T".Contains(y)) throw new InvalidOperationException("Git returned an unknown working tree status.");
            var path = entry[3..];
            string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                if (++index >= records.Length - 1 || string.IsNullOrEmpty(records[index])) throw new InvalidOperationException("Git returned an incomplete renamed path.");
                original = records[index];
            }
            var pair = entry[..2];
            var conflict = x == 'U' || y == 'U' || pair is "AA" or "DD";
            var untracked = pair == "??";
            result.Add(new GitChange
            {
                Path = path, OriginalPath = original, IndexStatus = x.ToString(), WorkTreeStatus = y.ToString(),
                HasStaged = !untracked && x is not (' ' or '!' or '?') && !conflict,
                HasUnstaged = untracked || y is not (' ' or '!') || conflict,
                IsConflict = conflict,
                Summary = conflict ? "Conflict" : untracked ? "Untracked" : $"Index: {StatusLabel(x)} · Working tree: {StatusLabel(y)}"
            });
        }
        return result;
    }

    private static (int Files, long Added, long Deleted, int Binary) ParseNumStat(string output)
    {
        if (output.Length == 0) return (0, 0, 0, 0);
        if (output[^1] != '\0') throw new InvalidOperationException("Git returned incomplete line totals.");
        var records = output.Split('\0');
        var files = 0;
        var binary = 0;
        long added = 0;
        long deleted = 0;
        for (var index = 0; index < records.Length - 1; index++)
        {
            var record = records[index];
            var firstTab = record.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
            if (firstTab <= 0 || secondTab <= firstTab + 1)
                throw new InvalidOperationException("Git returned invalid line totals.");
            var additions = record[..firstTab];
            var deletions = record[(firstTab + 1)..secondTab];
            if (secondTab == record.Length - 1)
            {
                // Rename records have a separate NUL-delimited old path and new path.
                if (index + 2 >= records.Length - 1 || records[index + 1].Length == 0 || records[index + 2].Length == 0)
                    throw new InvalidOperationException("Git returned an incomplete renamed-path line total.");
                index += 2;
            }
            files++;
            if (additions == "-" && deletions == "-") { binary++; continue; }
            if (!long.TryParse(additions, NumberStyles.None, CultureInfo.InvariantCulture, out var plus)
                || !long.TryParse(deletions, NumberStyles.None, CultureInfo.InvariantCulture, out var minus)
                || plus > long.MaxValue - added || minus > long.MaxValue - deleted)
                throw new InvalidOperationException("Git returned invalid line totals.");
            added += plus;
            deleted += minus;
        }
        return (files, added, deleted, binary);
    }

    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private static string StatusLabel(char status) => status switch
    {
        ' ' => "unchanged", 'M' => "modified", 'A' => "added", 'D' => "deleted", 'R' => "renamed",
        'C' => "copied", 'T' => "type changed", '?' => "untracked", '!' => "ignored", _ => "unmerged"
    };

    private static async Task<string?> ReadOperationStateAsync(string root, CancellationToken token)
    {
        var locations = new[] { ("MERGE_HEAD", "Merge in progress"), ("rebase-merge", "Rebase in progress"), ("rebase-apply", "Rebase / patch application in progress"), ("CHERRY_PICK_HEAD", "Cherry-pick in progress"), ("REVERT_HEAD", "Revert in progress"), ("BISECT_LOG", "Bisect in progress"), ("sequencer", "Sequenced Git operation in progress") };
        foreach (var (name, state) in locations)
        {
            var result = await GitAsync(root, ["rev-parse", "--git-path", name], token).ConfigureAwait(false);
            EnsureSuccess(result);
            var location = Path.GetFullPath(result.Output.TrimEnd('\r', '\n'), root);
            try { _ = File.GetAttributes(location); return state; }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("The repository operation state could not be read. No changes can be made until it is available."); }
        }
        return null;
    }

    private static string RequireFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.Any(char.IsControl)) throw new ArgumentException();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(full)) throw new ArgumentException();
            return full;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new InvalidOperationException("The selected Git folder is missing, inaccessible, or invalid."); }
    }

    private static void RequireExactRoot(string root, GitRepositorySnapshot snapshot)
    {
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null || !string.Equals(RequireFolder(root), snapshot.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected repository root changed or is unavailable. Refresh before continuing.");
    }

    private static void RequireCurrentBranch(GitRepositorySnapshot snapshot, string branch)
    {
        if (snapshot.IsDetached) throw new InvalidOperationException("Switch to a local branch before pulling or pushing.");
        if (snapshot.IsUnborn) throw new InvalidOperationException("Create the first commit before pulling or pushing this branch.");
        if (snapshot.Branch != branch) throw new InvalidOperationException("The current branch changed. Refresh and review the branch before continuing.");
    }

    private static void RequireNamedBranch(GitRepositorySnapshot snapshot, string branch)
    {
        if (snapshot.IsDetached) throw new InvalidOperationException("Switch to a local branch before committing.");
        if (snapshot.Branch != branch) throw new InvalidOperationException("The current branch changed. Refresh before continuing.");
    }

    private static void ValidateCommitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 16_000 || message.Contains('\0'))
            throw new InvalidOperationException("Enter a commit message of at most 16,000 characters.");
    }

    private static async Task RequireUnchangedPushDestinationAsync(string root, string remote, string destination, CancellationToken token)
    {
        var current = await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
        if (!string.Equals(current, destination, StringComparison.Ordinal))
            throw new InvalidOperationException("The push destination changed during this operation. Refresh and review it before pushing.");
    }

    private static async Task<string> PushCommitAsync(string root, string remote, string branch, string commitId, CancellationToken token)
    {
        if (!IsObjectId(commitId)) throw new InvalidOperationException("The commit to push could not be verified. Refresh before continuing.");
        // Pin the reviewed commit: another Git client can move the branch while this command runs.
        // A raw object source cannot use push --set-upstream, so configure tracking only after success.
        var push = await GitAsync(root,
            ["-c", $"remote.{remote}.mirror=false", "push", "--porcelain", "--no-force", "--no-follow-tags", "--recurse-submodules=no", "--", remote, $"{commitId}:refs/heads/{branch}"], token, network: true).ConfigureAwait(false);
        EnsureSuccess(push);
        var success = "Pushed to " + remote + "/" + branch + ".";
        try
        {
            var local = await GitAsync(root, ["show-ref", "--verify", "--hash", "refs/heads/" + branch], token).ConfigureAwait(false);
            EnsureSuccess(local);
            if (local.Output.Trim() != commitId)
                return success + " The local branch changed during the push; its upstream settings were left unchanged. Refresh before continuing.";
            EnsureSuccess(await GitAsync(root, ["config", "--local", "--replace-all", $"branch.{branch}.remote", remote], token).ConfigureAwait(false));
            EnsureSuccess(await GitAsync(root, ["config", "--local", "--replace-all", $"branch.{branch}.merge", "refs/heads/" + branch], token).ConfigureAwait(false));
            return success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // The remote update is confirmed even if cancellation or a local config error follows.
            return success + " Upstream tracking was not confirmed. Refresh and review the local branch settings.\n" + Sanitize(exception.Message);
        }
    }

    private static void RequireClean(GitRepositorySnapshot snapshot)
    {
        if (snapshot.Changes.Count != 0) throw new InvalidOperationException("The working tree must be clean for this action. Commit or otherwise handle the changes with Git first.");
    }

    private static void RequireNoOperation(GitRepositorySnapshot snapshot)
    {
        if (snapshot.OperationState is not null) throw new InvalidOperationException(snapshot.OperationState + ". Finish or abort it with Git before using this action.");
    }

    private static async Task ValidateBranchAsync(string root, string branch, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') || branch.StartsWith('@') || branch.Length > 250 || branch.Any(char.IsControl))
            throw new InvalidOperationException("Enter a valid, explicit Git branch name.");
        var result = await GitAsync(root, ["check-ref-format", "--branch", branch], token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("The branch name is invalid. Use a name such as feature/my-change.");
    }

    private static bool IsValidRemoteName(string name) => !string.IsNullOrEmpty(name) && !name.Any(char.IsControl) && RemoteNamePattern.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !name.EndsWith('.');
    private static void ValidateRemoteName(string name)
    {
        if (!IsValidRemoteName(name)) throw new InvalidOperationException("Use a remote name containing letters, numbers, dots, dashes or underscores, such as origin.");
    }

    private static async Task<string> RequireSingleRemoteAsync(string root, string remote, bool push, CancellationToken token, bool validateUrls = true)
    {
        ValidateRemoteName(remote);
        var args = push ? new[] { "remote", "get-url", "--push", "--all", remote } : new[] { "remote", "get-url", "--all", remote };
        var result = await GitAsync(root, args, token).ConfigureAwait(false);
        EnsureSuccess(result);
        var urls = SplitUrls(result.Output);
        if (urls.Length != 1) throw new InvalidOperationException("This remote has multiple or missing target URLs. Configure one explicit target with Git before using this action.");
        return validateUrls ? ValidateRemoteUrl(urls[0]) : urls[0];
    }

    private static string[] SplitUrls(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSafeRemoteUrl(string url)
    {
        try { _ = ValidateRemoteUrl(url); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static string ValidateRemoteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url != url.Trim() || url.Length > 4000 || url.Any(char.IsControl) || url.StartsWith('-') || url.Contains('\\'))
            throw new InvalidOperationException("Use a credential-free HTTPS or SSH Git clone URL.");
        var scp = ScpUrlPattern.Match(url);
        if (scp.Success && !scp.Groups["user"].Value.StartsWith('-') && !url.Contains("?", StringComparison.Ordinal) && !url.Contains('#') && !SensitiveDataProtection.ContainsLiteralCredential(url))
            return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Use a credential-free HTTPS or SSH Git clone URL. Local paths and custom remote helpers are not supported.");
        if (uri.Scheme == "https")
        {
            if (uri.UserInfo.Length != 0)
            {
                // Azure's ordinary clone URL contains an organization username, not a secret.
                // Accept only that exact known username, and never return it to display/copy.
                var organizationName = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                    ? uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    : Regex.IsMatch(uri.Host, "^[A-Za-z0-9][A-Za-z0-9-]*\\.visualstudio\\.com$", RegexOptions.IgnoreCase) ? uri.Host.Split('.')[0] : null;
                if (organizationName is null || uri.UserInfo.Contains(':') || uri.UserInfo.Contains('%') || !uri.UserInfo.Equals(organizationName, StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.Contains("/_git/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Remove the username, password, token and query parameters from the HTTPS clone URL. Git will use your command-line credentials.");
                url = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
            }
            if (SensitiveDataProtection.ContainsLiteralCredential(url))
                throw new InvalidOperationException("Remove the username, password, token and query parameters from the HTTPS clone URL. Git will use your command-line credentials.");
        }
        else if (uri.Scheme == "ssh")
        {
            if (uri.UserInfo.Contains(':') || uri.UserInfo.Contains('%') || !Regex.IsMatch(uri.UserInfo, "^(?:[A-Za-z0-9_][A-Za-z0-9._-]*)?$") || SensitiveDataProtection.ContainsLiteralCredential(uri.UserInfo + " " + uri.Host + uri.AbsolutePath))
                throw new InvalidOperationException("Use an SSH clone URL with only an optional SSH username and no embedded credentials.");
        }
        else throw new InvalidOperationException("Only HTTPS and SSH Git remotes are supported. Insecure or executable remote protocols are disabled.");
        return url;
    }

    private static string DisplayRemoteUrl(string url) => IsSafeRemoteUrl(url) ? ValidateRemoteUrl(url) : "[URL withheld: remove credentials or unsupported remote settings with Git]";

    private static IReadOnlyList<string> ValidatePaths(string root, IReadOnlyList<string> paths, GitRepositorySnapshot snapshot, bool staged)
    {
        if (paths.Count == 0 || paths.Count > 500) throw new InvalidOperationException("Select between 1 and 500 changed paths for this action.");
        var available = snapshot.Changes.Where(change => staged ? change.HasStaged : change.HasUnstaged)
            .SelectMany(change => change.OriginalPath is null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath }).ToHashSet(StringComparer.Ordinal);
        var selected = paths.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in selected)
        {
            ValidateRelativePath(root, path);
            if (!available.Contains(path)) throw new InvalidOperationException("Some selected changes are no longer available in this view. Refresh and review the selection.");
        }
        if (selected.Sum(path => path.Length + 3) > 24000) throw new InvalidOperationException("Too many path characters for one command. Select fewer files.");
        return selected;
    }

    private static void ValidateRelativePath(string root, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0') || Path.IsPathRooted(path)) throw new ArgumentException();
            var full = Path.GetFullPath(path, root);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
            if (path.Split(['/', '\\']).Any(part => part is ".." or ".git")) throw new ArgumentException();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { throw new InvalidOperationException("A selected path is outside the repository or is not a supported working-tree path."); }
    }

    private static string ValidateAzureOrganization(string organization)
    {
        var value = organization.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://dev.azure.com/" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Enter an Azure organization URL such as https://dev.azure.com/your-organization.");
        var modern = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(uri.AbsolutePath, "^/[A-Za-z0-9][A-Za-z0-9-]*$");
        var legacy = Regex.IsMatch(uri.Host, "^[A-Za-z0-9][A-Za-z0-9-]*\\.visualstudio\\.com$", RegexOptions.IgnoreCase) && uri.AbsolutePath == "/";
        if (!modern && !legacy) throw new InvalidOperationException("Use a hosted Azure DevOps organization at dev.azure.com or organization.visualstudio.com.");
        return value;
    }

    private static void ValidateAzureName(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl) || value.Trim().StartsWith('-'))
            throw new InvalidOperationException($"Enter a valid Azure DevOps {kind} name or ID.");
    }

    private static string CleanAzureCloneUrl(string raw, string organization)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Contains(':'))
            throw new InvalidOperationException("Azure DevOps did not return a supported credential-free HTTPS clone URL.");
        var organizationUri = new Uri(organization);
        var organizationName = organizationUri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            ? organizationUri.AbsolutePath.Trim('/') : organizationUri.Host.Split('.')[0];
        var correctModern = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/" + organizationName + "/", StringComparison.OrdinalIgnoreCase);
        var correctLegacy = uri.Host.Equals(organizationName + ".visualstudio.com", StringComparison.OrdinalIgnoreCase);
        if ((!correctModern && !correctLegacy) || !uri.AbsolutePath.Contains("/_git/", StringComparison.Ordinal))
            throw new InvalidOperationException("Azure DevOps returned a clone URL outside the selected organization.");
        return ValidateRemoteUrl(new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri);
    }

    private static Task<CommandResult> GitAsync(string folder, IReadOnlyList<string> arguments, CancellationToken token, bool network = false)
    {
        var args = new List<string>
        {
            "--no-pager", "-c", "color.ui=false", "-c", "credential.interactive=false", "-c", "protocol.allow=never",
            "-c", "protocol.https.allow=always", "-c", "protocol.ssh.allow=always"
        };
        args.AddRange(arguments);
        return RunAsync("git.exe", args, folder, token, network ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(45), azure: false);
    }

    private static Task<CommandResult> AzureAsync(string folder, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var (executable, prefix) = ResolveAzureExecutable();
        return RunAsync(executable, [.. prefix, .. arguments], folder, token, TimeSpan.FromMinutes(3), azure: true);
    }

    private static (string Executable, string[] Prefix) ResolveAzureExecutable()
    {
        foreach (var item in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = item.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                var native = Path.Combine(directory, "az.exe");
                if (File.Exists(native)) return (native, []);
                var shim = Path.Combine(directory, "az.cmd");
                if (!File.Exists(shim) || new FileInfo(shim).Length > 32 * 1024) continue;
                var script = File.ReadAllText(shim);
                // The official Windows MSI launcher; never pass arbitrary arguments through cmd.exe.
                if (!Regex.IsMatch(script, "(?i)%~dp0[\\\\/]?\\.\\.[\\\\/]python\\.exe\"?\\s+-IBm\\s+azure\\.cli\\s+%\\*")) continue;
                var python = Path.GetFullPath(Path.Combine(directory, "..", "python.exe"));
                if (File.Exists(python)) return (python, ["-IBm", "azure.cli"]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        throw new InvalidOperationException("Azure CLI was not found in a supported installation. Install the official Azure CLI for Windows and its azure-devops extension, then sign in from the command line. Git credentials alone do not authorize Azure repository creation or lookup.");
    }

    private static async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string folder, CancellationToken token, TimeSpan timeout, bool azure)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process();
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var repositoryVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_NAMESPACE", "GIT_PREFIX", "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_LITERAL_PATHSPECS",
            "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS", "GIT_QUARANTINE_PATH", "GIT_EXEC_PATH"
        };
        foreach (var key in start.Environment.Keys.ToArray())
            if (repositoryVariables.Contains(key) || key.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase)) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        start.Environment["LC_ALL"] = "C";
        if (azure)
        {
            start.Environment["AZ_INSTALLER"] = "MSI";
            start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no";
            start.Environment["AZURE_CORE_NO_COLOR"] = "true";
            start.Environment["PYTHONIOENCODING"] = "utf-8";
        }
        process.StartInfo = start;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The command-line process could not be started.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            throw new InvalidOperationException(azure
                ? "Azure CLI could not start. Install the official Azure CLI and azure-devops extension, and sign in from the command line."
                : "Git could not start. Install Git for Windows and make git.exe available on PATH, then reopen this workspace.");
        }
        process.StandardInput.Close();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var error = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (stdout.Truncated || stderr.Truncated)
                throw new InvalidOperationException("Git or Azure CLI output exceeded the workspace limit. The command may have completed; refresh and inspect with the command line before retrying.");
            var result = new CommandResult(process.ExitCode, stdout.Text, stderr.Text);
            if (azure && result.ExitCode != 0)
                return result with { Error = "Azure CLI needs the azure-devops extension and a signed-in account with access to this organization/project. Git credentials alone are insufficient.\n" + result.Error };
            return result;
        }
        catch (OperationCanceledException)
        {
            TryKillOwnedProcess(process);
            await ObserveReadersAsync(output, error).ConfigureAwait(false);
            throw new OperationCanceledException(token.IsCancellationRequested
                ? "Operation canceled. Its local or remote outcome may be uncertain; refresh and inspect before retrying."
                : "The command timed out. Its local or remote outcome may be uncertain; refresh and inspect before retrying.", token);
        }
        catch (DecoderFallbackException)
        {
            TryKillOwnedProcess(process);
            await ObserveReadersAsync(output, error).ConfigureAwait(false);
            throw new InvalidOperationException("Git returned text or filenames that cannot be decoded safely. Inspect this repository with the command line.");
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            var keep = Math.Min(count, OutputLimit - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        return (text.ToString(), truncated);
    }

    private static void TryKillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static async Task ObserveReadersAsync(params Task[] readers)
    {
        try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException or DecoderFallbackException) { }
    }

    private static string Result(CommandResult result, string success)
    {
        EnsureSuccess(result);
        var detail = DisplayOutput((result.Output + "\n" + result.Error).Trim());
        return string.IsNullOrWhiteSpace(detail) ? success : success + "\n" + detail;
    }

    private static void EnsureSuccess(CommandResult result)
    {
        if (result.ExitCode != 0) ThrowCommandFailure(result);
    }

    private static void ThrowCommandFailure(CommandResult result)
    {
        var detail = Sanitize((result.Error + "\n" + result.Output).Trim());
        if (detail.Length > 12000) detail = detail[..12000] + "\n[Output shortened after credential filtering.]";
        throw new InvalidOperationException($"Command failed (exit {result.ExitCode})." + (detail.Length == 0 ? "" : "\n" + detail));
    }

    private static string Sanitize(string text)
    {
        try
        {
            var filtered = UrlInOutput.Replace(text, match =>
            {
                var value = match.Value;
                var scheme = value.IndexOf("://", StringComparison.Ordinal) + 3;
                var authorityEnd = value.IndexOf('/', scheme);
                if (authorityEnd < 0) authorityEnd = value.Length;
                var at = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - scheme);
                if (at >= scheme) value = value[..scheme] + "[redacted]@" + value[(at + 1)..];
                var query = value.IndexOfAny(['?', '#']);
                if (query >= 0) value = value[..query] + "[parameters withheld]";
                return value;
            });
            return SensitiveDataProtection.Redact(filtered);
        }
        catch (RegexMatchTimeoutException) { return "[Output withheld: credential filtering limit reached.]"; }
    }

    private static string DisplayOutput(string text)
    {
        var safe = Sanitize(text);
        return safe.Length <= 200_000 ? safe : safe[..200_000] + "\n[Preview shortened after credential filtering. Inspect the complete diff with Git.]";
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
