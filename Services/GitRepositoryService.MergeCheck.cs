using System.Globalization;
using System.Text;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    public static Task<GitMergeCheckResult> CheckReleaseMergeAsync(string root, string remote, string sourceBranch,
        CancellationToken token = default, IProgress<string>? progress = null, Func<CancellationToken, Task>? ensureIdle = null,
        bool skipUpToDateSimulation = false) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            await RequireNoPendingSyncAsync(root, token).ConfigureAwait(false);
            if (snapshot.IsDetached || snapshot.IsUnborn || !IsObjectId(snapshot.HeadCommit))
                throw new InvalidOperationException("Start a Git-only conflict test from a named local branch with an existing commit.");
            await ValidateBranchAsync(root, sourceBranch, token).ConfigureAwait(false);
            var url = await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false);
            var savedRelease = await ReadReleaseBranchAsync(root, remote, token).ConfigureAwait(false);
            if (savedRelease != sourceBranch)
                throw new InvalidOperationException("The saved release branch changed or is unavailable. Refresh and choose the release source before checking conflicts.");
            IReadOnlyList<string> networkOptions = [];
            if (ensureIdle is not null)
            {
                var helpers = await GitConnectionService.ReadAutomaticHelperOptionsAsync(root, url, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Custom Git credential helpers cannot run in the background. Review the connection in Git before retrying the conflict test.");
                networkOptions = [.. helpers, "-c", "http.followRedirects=false", "-c", "http." + url + ".sslVerify=true"];
                // The repository lease can have waited behind another Git command.
                // Confirm agent inactivity again after acquiring it, before remote access.
                await ensureIdle(token).ConfigureAwait(false);
            }

            progress?.Report($"Checking the latest {remote}/{sourceBranch} commit…");
            var sourceCommit = await ReadMergeCheckSourceAsync(root, url, sourceBranch, token, networkOptions, ensureIdle is not null).ConfigureAwait(false);
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));

            // Fetch the advertised immutable object, without a destination ref. Empty refmap
            // suppresses configured tracking mappings; FETCH_HEAD and maintenance stay untouched.
            if (ensureIdle is not null) await ensureIdle(token).ConfigureAwait(false);
            progress?.Report("Fetching the selected remote commit without changing branches…");
            var fetched = await GitAsync(root,
                [.. networkOptions, "-c", "fetch.pruneTags=false", "fetch", "--no-prune", "--no-prune-tags", "--no-tags",
                    "--no-recurse-submodules", "--no-write-fetch-head", "--no-auto-maintenance", "--no-write-commit-graph",
                    "--refmap=", "--", url, sourceCommit], token, network: true, strictSsh: ensureIdle is not null).ConfigureAwait(false);
            EnsureSuccess(fetched);
            var source = await GitAsync(root, ["rev-parse", "--verify", sourceCommit + "^{commit}"], token).ConfigureAwait(false);
            EnsureSuccess(source);
            if (source.Output.Trim() != sourceCommit)
                throw new InvalidOperationException("The freshly fetched release commit could not be verified. No conflict result is available.");

            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
            if (ensureIdle is not null) await ensureIdle(token).ConfigureAwait(false);
            progress?.Report("Checking whether the remote branch has commits missing locally…");
            var incoming = await GitAsync(root,
                ["rev-list", "--count", snapshot.HeadCommit + ".." + sourceCommit, "--"], token).ConfigureAwait(false);
            EnsureSuccess(incoming);
            if (!long.TryParse(incoming.Output.TrimEnd('\r', '\n'), NumberStyles.None, CultureInfo.InvariantCulture, out var incomingCommits)
                || incomingCommits < 0)
                throw new InvalidOperationException("Git returned an invalid incoming-commit count. No conflict result is available.");

            var runSimulation = !skipUpToDateSimulation || incomingCommits > 0;
            var hasConflicts = false;
            IReadOnlyList<string> paths = [];
            var diagnostics = "The current branch already includes the fetched remote commit. No merge simulation was needed.";
            if (runSimulation)
            {
                await RequireMergeTreeSupportAsync(root, token).ConfigureAwait(false);
                await RequireNoExternalMergeCommandsAsync(root, token).ConfigureAwait(false);
                if (ensureIdle is not null) await ensureIdle(token).ConfigureAwait(false);
                progress?.Report($"Testing {incomingCommits} incoming commit(s) for merge conflicts with Git only…");
                // merge-tree creates Git objects but never reads or writes the index or working tree.
                // No fallback to merge, checkout, stash, commit or conflict resolution is permitted.
                var simulation = await GitAsync(root,
                    ["-c", "submodule.recurse=false", "-c", "rerere.enabled=false", "-c", "rerere.autoupdate=false",
                        "merge-tree", "--write-tree", "--name-only", "--messages", "-z", snapshot.HeadCommit, sourceCommit], token).ConfigureAwait(false);
                if (simulation.ExitCode is not (0 or 1))
                {
                    if (simulation.ExitCode == 129)
                        throw new InvalidOperationException("This Git installation does not support the required merge-tree options. Install Git for Windows 2.38 or newer. No merge was started.");
                    ThrowCommandFailure(simulation);
                }
                (paths, diagnostics) = ParseMergeCheckOutput(root, simulation);
                hasConflicts = simulation.ExitCode == 1;
            }
            else progress?.Report("The current branch already includes the remote changes. Verifying this result…");

            // An external Git client can bypass the launcher lease. Reject detected identity
            // changes instead of attaching a result to a different branch or release tip.
            progress?.Report("Verifying the current branch and selected remote branch…");
            if (ensureIdle is not null) await ensureIdle(token).ConfigureAwait(false);
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            if (await ReadMergeCheckSourceAsync(root, url, sourceBranch, token, networkOptions, ensureIdle is not null).ConfigureAwait(false) != sourceCommit)
                throw new InvalidOperationException("The release branch changed during the conflict test. Run the test again to compare its new commit; this result was discarded.");
            if (runSimulation) await RequireNoExternalMergeCommandsAsync(root, token).ConfigureAwait(false);
            if (await ReadReleaseBranchAsync(root, remote, token).ConfigureAwait(false) != savedRelease)
                throw new InvalidOperationException("The saved release branch changed during the conflict test. Refresh and review the release choice; this result was discarded.");
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
            if (ensureIdle is not null) await ensureIdle(token).ConfigureAwait(false);

            return new GitMergeCheckResult
            {
                CurrentBranch = snapshot.Branch, CurrentCommit = snapshot.HeadCommit,
                Remote = remote, SourceBranch = sourceBranch, SourceCommit = sourceCommit,
                IncomingCommits = incomingCommits,
                HasConflicts = hasConflicts, ConflictedPaths = paths,
                HasUncommittedChanges = snapshot.Changes.Count > 0, CheckedAt = DateTimeOffset.UtcNow,
                GitDiagnostics = diagnostics,
                Message = !runSimulation ? "The current branch is up to date with the selected remote branch. No merge is needed."
                    : hasConflicts ? "Git found merge conflicts between the compared commits."
                    : "Git found no merge conflicts between the compared commits."
            };
        });

    private static async Task RequireMergeTreeSupportAsync(string root, CancellationToken token)
    {
        var help = await GitAsync(root, ["merge-tree", "-h"], token).ConfigureAwait(false);
        if (help.ExitCode is not (0 or 129)) ThrowCommandFailure(help);
        if (!(help.Output + help.Error).Contains("--write-tree", StringComparison.Ordinal))
            throw new InvalidOperationException("Git-only conflict tests require Git for Windows 2.38 or newer with merge-tree --write-tree support. No merge was started.");
    }

    private static async Task RequireNoExternalMergeCommandsAsync(string root, CancellationToken token)
    {
        // Inspect names only: configured command values can contain credentials and must
        // never enter a displayed error. A driver could change files outside Git's control.
        var drivers = await GitAsync(root, ["config", "--null", "--name-only", "--get-regexp", "^merge\\..*\\.driver$"], token).ConfigureAwait(false);
        if (drivers.ExitCode is not (0 or 1)) ThrowCommandFailure(drivers);
        if (drivers.ExitCode == 0 || drivers.Output.Length != 0)
            throw new InvalidOperationException("External Git merge drivers are configured. They can run commands that change files, so this Git-only conflict test cannot run safely. No conflict result is available; review those drivers with Git.");

        // Renormalization performs a virtual checkout/check-in and can invoke external
        // clean, smudge or process filters even when no custom merge driver is selected.
        var renormalize = await GitAsync(root, ["config", "--type=bool", "--get", "merge.renormalize"], token).ConfigureAwait(false);
        if (renormalize.ExitCode is not (0 or 1)) ThrowCommandFailure(renormalize);
        if (renormalize.ExitCode == 1 || renormalize.Output.Trim() == "false") return;
        if (renormalize.Output.Trim() != "true")
            throw new InvalidOperationException("Git merge renormalization configuration could not be verified. No conflict result is available.");
        var filters = await GitAsync(root,
            ["config", "--null", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$"], token).ConfigureAwait(false);
        if (filters.ExitCode is not (0 or 1)) ThrowCommandFailure(filters);
        if (filters.ExitCode == 0 || filters.Output.Length != 0)
            throw new InvalidOperationException("Git merge renormalization and external content filters are configured. Those filters can run commands that change files, so this Git-only conflict test cannot run safely. No conflict result is available; review those filters with Git.");
    }

    private static async Task<string> ReadMergeCheckSourceAsync(string root, string url, string sourceBranch, CancellationToken token,
        IReadOnlyList<string>? networkOptions = null, bool strictSsh = false)
    {
        var remote = await GitAsync(root, [.. networkOptions ?? [], "ls-remote", "--exit-code", "--heads", "--", url, "refs/heads/" + sourceBranch],
            token, network: true, strictSsh: strictSsh).ConfigureAwait(false);
        if (remote.ExitCode == 2)
            throw new InvalidOperationException("The release branch is no longer present on the selected remote. Reload the remote branch list; no conflict result is available.");
        EnsureSuccess(remote);
        var branches = ParseRemoteBranches(remote.Output);
        if (branches.Count != 1 || branches[0].Name != sourceBranch)
            throw new InvalidOperationException("The remote release commit response could not be verified. No conflict result is available.");
        return branches[0].CommitId;
    }

    private static (IReadOnlyList<string> Paths, string Diagnostics) ParseMergeCheckOutput(string root, CommandResult result)
    {
        // Git's NUL format is: tree OID, conflicted paths, empty separator, then
        // repeated path-count / paths / conflict-type / message records. Exit 1 can
        // have no conflicted paths (for example, directory rename conflicts).
        if (result.Output.Length == 0 || result.Output[^1] != '\0')
            throw new InvalidOperationException("Git returned an incomplete conflict-test response. No conflict result is available.");
        var fields = result.Output.Split('\0');
        if (!IsObjectId(fields[0]))
            throw new InvalidOperationException("Git returned an invalid conflict-test tree identity. No conflict result is available.");
        var paths = new List<string>();
        var index = 1;
        while (index < fields.Length - 1 && fields[index].Length > 0)
        {
            ValidateRelativePath(root, fields[index]);
            paths.Add(Sanitize(fields[index++]));
        }
        if (index < fields.Length) index++;
        if (result.ExitCode == 0 && paths.Count > 0)
            throw new InvalidOperationException("Git returned conflicting conflict-test status and paths. No conflict result is available.");
        var messages = new StringBuilder();
        while (index < fields.Length - 1)
        {
            if (!int.TryParse(fields[index++], NumberStyles.None, CultureInfo.InvariantCulture, out var pathCount)
                || pathCount < 0 || pathCount > fields.Length - index - 3)
                throw new InvalidOperationException("Git returned an incomplete conflict-test message. No conflict result is available.");
            for (var path = 0; path < pathCount; path++)
                if (fields[index++].Length == 0)
                    throw new InvalidOperationException("Git returned an invalid conflict-test message path. No conflict result is available.");
            var conflictType = fields[index++];
            var message = fields[index++];
            if (conflictType.Length == 0 || message.Length == 0)
                throw new InvalidOperationException("Git returned an invalid conflict-test message. No conflict result is available.");
            if (messages.Length > 0) messages.AppendLine();
            messages.AppendLine(conflictType).Append(message.TrimEnd('\r', '\n'));
        }
        if (index != fields.Length - 1 && index != fields.Length)
            throw new InvalidOperationException("Git returned an ambiguous conflict-test response. No conflict result is available.");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            if (messages.Length > 0) messages.AppendLine();
            messages.Append(result.Error.TrimEnd('\r', '\n'));
        }
        return (paths.Distinct(StringComparer.Ordinal).ToArray(), Sanitize(messages.ToString()));
    }
}
