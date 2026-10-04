using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    private const string PendingSyncFile = "launcher-power-sync.json";
    private static readonly JsonSerializerOptions SyncJson = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    public static Task<GitPowerDetails> ReadPowerDetailsAsync(string root, string remote, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            if (remote.Length > 0) ValidateRemoteName(remote);
            var pending = await ReadPendingSyncAsync(root, token).ConfigureAwait(false);
            var (lastBranch, lastAt) = await ReadLastCreatedBranchAsync(root, snapshot, token).ConfigureAwait(false);
            var release = remote.Length == 0 ? null : await ReadReleaseBranchAsync(root, remote, token).ConfigureAwait(false);
            var conflicts = ConflictPaths(snapshot);
            var ready = pending is not null && await CanFinishSyncAsync(root, remote, snapshot, pending, token).ConfigureAwait(false);
            return new GitPowerDetails
            {
                LastCreatedBranch = lastBranch, LastCreatedAt = lastAt,
                LastCreatedEvidence = lastBranch is null ? "No retained branch-creation record is available." : "Latest branch creation found in retained local reflogs; this is not a commit date.",
                ReleaseBranch = release, PendingSync = pending, ConflictedPaths = conflicts, ReadyToFinish = ready,
                CanDismiss = pending is not null && snapshot.OperationState is null,
                RecoveryMessage = pending is null ? null : RecoveryMessage(pending, remote, conflicts, ready)
            };
        });

    public static Task<IReadOnlyList<GitRemoteBranchChoice>> ListRemoteBranchesAsync(string root, string remote, CancellationToken token = default) =>
        InRepositoryAsync<IReadOnlyList<GitRemoteBranchChoice>>(root, token, async _ =>
        {
            await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            var result = await GitAsync(root, ["ls-remote", "--heads", "--", remote], token, network: true).ConfigureAwait(false);
            EnsureSuccess(result);
            return ParseRemoteBranches(result.Output);
        });

    public static Task<string> SaveReleaseBranchAsync(string root, string remote, string sourceBranch, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async _ =>
        {
            await RequireNoPendingSyncAsync(root, token).ConfigureAwait(false);
            await ValidateBranchAsync(root, sourceBranch, token).ConfigureAwait(false);
            var url = await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
            await RequireRemoteBranchAsync(root, remote, sourceBranch, mustExist: true, token).ConfigureAwait(false);
            await SaveReleaseBranchCoreAsync(root, remote, sourceBranch, url, token).ConfigureAwait(false);
            return $"Release source saved as {remote}/{sourceBranch} for this repository and remote URL. Nothing was merged or pushed.";
        });

    public static Task<string> CommitAllAndPushToBranchAsync(string root, string remote, string destinationBranch, string message, CancellationToken token = default, IProgress<string>? progress = null,
        AgentGitSummaryBatch? reviewedSummaries = null) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            progress?.Report("Checking the branch and remote…");
            ValidateCommitMessage(message);
            RequireNoOperation(snapshot);
            if (snapshot.IsDetached) throw new InvalidOperationException("Switch to a named local branch before using Commit all & push.");
            if (await ReadPendingSyncAsync(root, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A tracked release sync already exists. Finish it or explicitly dismiss its tracking before starting another commit-and-push action.");
            var url = await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false);
            RequireReviewedAgentSummaries(root, remote, snapshot, reviewedSummaries);
            var previewBranch = snapshot.Branch;
            var previewConnectionId = AgentGitChangeStore.ConnectionId(snapshot.Remotes.Single(item => item.Name == remote));
            await ValidateDestinationAsync(root, remote, destinationBranch, snapshot, token).ConfigureAwait(false);
            var branchCreated = false;
            var committed = false;
            var summaryNotice = "";
            try
            {
                // An unborn HEAD cannot be the start point of a new branch. Create the reviewed
                // initial commit first; both the original and new branch then retain that commit.
                if (snapshot.IsUnborn && snapshot.Branch != destinationBranch)
                {
                    progress?.Report("Creating the first local commit…");
                    snapshot = await CommitAllCoreAsync(root, snapshot, message, null, token).ConfigureAwait(false);
                    committed = true;
                    GitCommitPreviewState.Clear(root, previewBranch, previewConnectionId);
                    summaryNotice = await ConsumeReviewedAgentSummariesAsync(reviewedSummaries, snapshot.HeadCommit).ConfigureAwait(false);
                    reviewedSummaries = null;
                }
                if (snapshot.Branch != destinationBranch)
                {
                    progress?.Report("Creating your destination branch…");
                    snapshot = await CreateDestinationCoreAsync(root, snapshot, destinationBranch, token).ConfigureAwait(false);
                    branchCreated = true;
                }
                var before = snapshot.HeadCommit;
                progress?.Report("Saving local changes…");
                snapshot = await CommitAllCoreAsync(root, snapshot, message, null, token).ConfigureAwait(false);
                committed |= snapshot.HeadCommit != before;
                if (snapshot.HeadCommit != before)
                {
                    GitCommitPreviewState.Clear(root, previewBranch, previewConnectionId);
                    summaryNotice += await ConsumeReviewedAgentSummariesAsync(reviewedSummaries, snapshot.HeadCommit).ConfigureAwait(false);
                }
                RequireClean(snapshot);
                RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
                await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
                progress?.Report($"Pushing {destinationBranch} to {remote}…");
                var pushed = await PushCommitAsync(root, remote, destinationBranch, snapshot.HeadCommit, token).ConfigureAwait(false);
                progress?.Report("Push confirmed. Refreshing local status…");
                return (branchCreated ? $"Created and checked out {destinationBranch} from the reviewed local history. " : "")
                    + (committed ? "All local changes committed. " : "No new commit was needed. ") + pushed + summaryNotice;
            }
            catch (Exception exception) when (IsPowerFailure(exception))
            {
                var preserved = (branchCreated ? $"The new branch {destinationBranch} remains checked out. " : "")
                    + (committed ? "The confirmed local commit remains saved. " : "A local commit was not confirmed; inspect the working tree and local history. ");
                throw new GitCommitPushException(preserved + "Pushing was not confirmed. Refresh and review; use Push for existing commits rather than recommitting.\n" + Sanitize(exception.Message) + summaryNotice, committed, exception);
            }
        });

    public static Task<GitSyncResult> PrepareSyncAsync(string root, string remote, string sourceBranch, CancellationToken token = default,
        GitMergeCheckResult? reviewedCheck = null, IProgress<string>? progress = null, GitRepositorySnapshot? reviewedSnapshot = null) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            progress?.Report("Verifying the compared local branch and remote source…");
            RequireNoOperation(snapshot);
            if (snapshot.IsDetached || snapshot.IsUnborn)
                throw new InvalidOperationException("Start a release merge from a named local branch with an existing commit and shared release history.");
            if (snapshot.Changes.Any(change => change.IsConflict)) throw new InvalidOperationException("Resolve existing conflicts before merging a release branch.");
            if (await ReadPendingSyncAsync(root, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A release merge is already tracked. Use Commit all & push when ready, or review its recovery details before starting another.");
            await ValidateBranchAsync(root, sourceBranch, token).ConfigureAwait(false);
            var url = await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false);
            var destinationBranch = snapshot.Branch;
            if (reviewedCheck is null || !IsObjectId(reviewedCheck.SourceCommit)
                || reviewedCheck.CurrentBranch != destinationBranch || reviewedCheck.CurrentCommit != snapshot.HeadCommit
                || reviewedCheck.Remote != remote || reviewedCheck.SourceBranch != sourceBranch)
                throw new InvalidOperationException("The reviewed branch comparison is missing or no longer matches this local branch and remote source. Run Merge Locally again to compare the current branches before merging.");
            if (reviewedSnapshot is not null)
            {
                RequireSameLocalState(reviewedSnapshot, snapshot);
                if (reviewedSnapshot.StateFingerprint.Length == 0 || reviewedSnapshot.StateFingerprint != snapshot.StateFingerprint)
                    throw new InvalidOperationException("The repository or remote configuration changed after the merge review. Refresh and compare the branches again before merging.");
            }
            if (await ReadReleaseBranchAsync(root, remote, token).ConfigureAwait(false) != sourceBranch)
                throw new InvalidOperationException("The selected remote source changed after the branch comparison. Refresh and compare the selected branch again before merging.");

            progress?.Report($"Checking the latest {remote}/{sourceBranch} commit…");
            if (await ReadMergeCheckSourceAsync(root, url, sourceBranch, token).ConfigureAwait(false) != reviewedCheck.SourceCommit)
                throw new InvalidOperationException("The remote source changed after the branch comparison. Run Merge Locally again to compare its latest commit; no local checkpoint or merge was started.");
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));

            // An exact successful fetch is required. A failure must never consume an older cached ref.
            // Until incoming history is confirmed, keep local files, history and recovery tracking untouched.
            progress?.Report($"Fetching {remote}/{sourceBranch} for the local merge…");
            var fetched = await GitAsync(root,
                ["-c", "fetch.pruneTags=false", "fetch", "--no-prune", "--no-prune-tags", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--", remote,
                    $"+refs/heads/{sourceBranch}:refs/remotes/{remote}/{sourceBranch}"], token, network: true).ConfigureAwait(false);
            EnsureSuccess(fetched);
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            var source = await GitAsync(root, ["rev-parse", "--verify", $"refs/remotes/{remote}/{sourceBranch}^{{commit}}"], token).ConfigureAwait(false);
            EnsureSuccess(source);
            var sourceCommit = source.Output.Trim();
            if (!IsObjectId(sourceCommit) || sourceCommit != reviewedCheck.SourceCommit)
                throw new InvalidOperationException("The fetched remote source no longer matches the reviewed branch comparison. Run Merge Locally again; no local checkpoint or merge was started.");
            if (await ReadMergeCheckSourceAsync(root, url, sourceBranch, token).ConfigureAwait(false) != sourceCommit)
                throw new InvalidOperationException("The remote source changed while preparing the local merge. Run Merge Locally again to compare its latest commit; no local checkpoint or merge was started.");
            await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
            var refreshed = await ReadCoreAsync(root, token).ConfigureAwait(false);
            RequireSameLocalState(snapshot, refreshed);
            snapshot = refreshed;
            if (await ReadReleaseBranchAsync(root, remote, token).ConfigureAwait(false) != sourceBranch)
                throw new InvalidOperationException("The selected remote source changed while preparing the local merge. Refresh and compare the selected branch again; no local checkpoint or merge was started.");

            progress?.Report($"Checking for remote commits missing from {destinationBranch}…");
            if (await IsAncestorAsync(root, sourceCommit, snapshot.HeadCommit, token).ConfigureAwait(false))
            {
                RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
                progress?.Report("Local branch is up to date. Returning to the Git workspace…");
                return new GitSyncResult
                {
                    Message = $"{destinationBranch} is up to date with {remote}/{sourceBranch}; the selected remote source has no commits missing locally. No checkpoint or merge was needed. Continue reviewing and committing your local work in the Git workspace."
                };
            }
            // The reviewed comparison uses these exact immutable commits. Recheck configuration
            // that could have changed since its simulation before any checkpoint or merge begins.
            await RequireMergeTreeSupportAsync(root, token).ConfigureAwait(false);
            await RequireNoExternalMergeCommandsAsync(root, token).ConfigureAwait(false);
            // Merge stays on the reviewed current branch. Branch creation belongs to Commit all & push.
            await RequireCommitIdentityAsync(root, token).ConfigureAwait(false);
            var pending = new GitPendingSync
            {
                RepositoryRoot = snapshot.RepositoryRoot!, Remote = remote, RemoteFingerprint = RemoteTargetFingerprint(url),
                DestinationBranch = destinationBranch, SourceBranch = sourceBranch, OriginalHead = snapshot.HeadCommit,
                SourceCommit = sourceCommit, PreparedHead = snapshot.HeadCommit, Stage = GitSyncStage.BranchReady
            };
            progress?.Report("Recording recovery details for the local merge…");
            await WritePendingSyncAsync(root, pending, create: true, token).ConfigureAwait(false);
            try
            {
                if (snapshot.Changes.Count > 0)
                {
                    progress?.Report("Preserving uncommitted changes in a local checkpoint…");
                    var checkpointMessage = $"Save local changes before merging {remote}/{sourceBranch} into {destinationBranch}";
                    snapshot = await CommitAllCoreAsync(root, snapshot, checkpointMessage, null, token).ConfigureAwait(false);
                    pending = pending with { CheckpointCommit = snapshot.HeadCommit, PreparedHead = snapshot.HeadCommit, Stage = GitSyncStage.Checkpointed };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                }
                RequireClean(snapshot);
                RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
                await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
                pending = pending with { PreparedHead = snapshot.HeadCommit, Stage = GitSyncStage.MergeStarted };
                await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                progress?.Report($"Integrating {remote}/{sourceBranch} into {destinationBranch} locally…");
                var merge = await GitAsync(root,
                    ["-c", $"branch.{destinationBranch}.mergeOptions=", "-c", "submodule.recurse=false", "-c", "rerere.enabled=false", "-c", "rerere.autoupdate=false",
                        "merge", "--no-ff", "--no-commit", "--no-edit", "--no-autostash", "--no-overwrite-ignore", "--no-squash", "--message",
                        $"Merge {remote}/{sourceBranch} into {destinationBranch}", "--", sourceCommit], token).ConfigureAwait(false);
                progress?.Report("Checking the local merge result…");
                var merged = await ReadCoreAsync(root, token).ConfigureAwait(false);
                RequirePendingBranch(pending, merged);
                var mergeHead = await ReadMergeHeadAsync(root, token).ConfigureAwait(false);
                var conflicts = ConflictPaths(merged);
                if (mergeHead == sourceCommit && conflicts.Count > 0)
                {
                    pending = pending with { Stage = GitSyncStage.Conflicts };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                    progress?.Report("Local merge stopped with conflicts. Resolve them before committing.");
                    return new GitSyncResult
                    {
                        PendingSync = pending, ConflictedPaths = conflicts,
                        Message = $"Fetched {remote}/{sourceBranch}. " + CheckpointDescription(pending)
                            + "Merge stopped with conflicts. Nothing was pushed. Resolve and stage these paths in Git or your editor, then refresh and use Commit all & push:\n"
                            + string.Join("\n", conflicts)
                    };
                }
                EnsureSuccess(merge);
                if (mergeHead is not null && mergeHead != sourceCommit)
                    throw new InvalidOperationException("The active merge does not match the fetched release commit. Inspect it with Git before continuing.");
                if (mergeHead is null && !await IsAncestorAsync(root, sourceCommit, merged.HeadCommit, token).ConfigureAwait(false))
                    throw new InvalidOperationException("The fetched release commit is neither pending in this merge nor confirmed in local history.");
                if (conflicts.Count != 0) throw new InvalidOperationException("Unresolved Git conflicts remain. Review them before continuing.");
                pending = pending with { Stage = GitSyncStage.AwaitingConfirmation };
                await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                progress?.Report("Local merge is ready for review. Nothing was pushed.");
                return new GitSyncResult
                {
                    PendingSync = pending, ReadyToFinish = true,
                    Message = $"Fetched {remote}/{sourceBranch} at {sourceCommit[..12]}. " + CheckpointDescription(pending)
                        + (mergeHead is null ? "The release commit is already included. " : "Merge prepared without a merge commit. ")
                        + "No conflicts found. Changes remain local on " + destinationBranch + ". Use Commit all & push when you are ready to choose the destination branch and publish. Nothing was pushed."
                };
            }
            catch (GitCommandExitUnconfirmedException)
            {
                // Keep the last durable recovery phase and the owned process identity.
                // The workspace must confirm exit before any follow-up Git command.
                throw;
            }
            catch (Exception exception) when (IsPowerFailure(exception))
            {
                await MarkSyncForReviewAsync(root, pending).ConfigureAwait(false);
                throw new InvalidOperationException(CheckpointDescription(pending)
                    + "Release merge did not finish. Nothing was pushed by this action. Existing branches, commits, files and any merge remain in place. Refresh to inspect recovery status; do not blindly repeat the operation.\n"
                    + Sanitize(exception.Message), exception);
            }
        });

    public static Task<string> FinishSyncAsync(string root, string remote, string destinationBranch, string message, string expectedAttemptId, CancellationToken token = default, IProgress<string>? progress = null,
        AgentGitSummaryBatch? reviewedSummaries = null) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            progress?.Report("Checking the local merge and destination…");
            ValidateCommitMessage(message);
            var pending = await ReadPendingSyncAsync(root, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No release sync is tracked for this checkout. Refresh before continuing.");
            if (pending.AttemptId != expectedAttemptId)
                throw new InvalidOperationException("The tracked release sync changed after your review. Refresh and review the current attempt before finishing.");
            if (!await CanFinishSyncAsync(root, remote, snapshot, pending, token).ConfigureAwait(false))
                throw new InvalidOperationException("The tracked merge, branch, commit or remote changed, or conflicts remain. Refresh and follow the recovery details before finishing.");
            RequireReviewedAgentSummaries(root, remote, snapshot, reviewedSummaries);
            var previewBranch = snapshot.Branch;
            var previewConnectionId = AgentGitChangeStore.ConnectionId(snapshot.Remotes.Single(item => item.Name == remote));
            await ValidateDestinationAsync(root, remote, destinationBranch, snapshot, token).ConfigureAwait(false);
            var url = await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false);
            var alreadyCommitted = pending.Stage is GitSyncStage.ReadyToPush or GitSyncStage.PushUncertain;
            var committed = alreadyCommitted;
            var summaryNotice = "";
            try
            {
                if (!alreadyCommitted)
                {
                    progress?.Report("Committing the local merge…");
                    var mergeHead = await ReadMergeHeadAsync(root, token).ConfigureAwait(false);
                    pending = pending with { Stage = GitSyncStage.Finalizing };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                    var before = snapshot.HeadCommit;
                    snapshot = await CommitAllCoreAsync(root, snapshot, message, mergeHead, token).ConfigureAwait(false);
                    committed = true;
                    if (snapshot.HeadCommit != before)
                    {
                        GitCommitPreviewState.Clear(root, previewBranch, previewConnectionId);
                        summaryNotice = await ConsumeReviewedAgentSummariesAsync(reviewedSummaries, snapshot.HeadCommit).ConfigureAwait(false);
                    }
                    pending = pending with { FinalCommit = snapshot.HeadCommit, FinalBranch = snapshot.Branch, Stage = GitSyncStage.ReadyToPush };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                }
                if (snapshot.Branch != destinationBranch)
                {
                    progress?.Report("Creating your destination branch…");
                    pending = pending with { Stage = GitSyncStage.Finalizing };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                    snapshot = await CreateDestinationCoreAsync(root, snapshot, destinationBranch, token).ConfigureAwait(false);
                    pending = pending with { FinalBranch = snapshot.Branch, Stage = GitSyncStage.ReadyToPush };
                    await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                }
                RequireClean(snapshot);
                RequireSameLocalState(snapshot, await ReadCoreAsync(root, token).ConfigureAwait(false));
                await RequireUnchangedPowerRemoteAsync(root, remote, url, token).ConfigureAwait(false);
                pending = pending with { Stage = GitSyncStage.Pushing };
                await WritePendingSyncAsync(root, pending, false, token).ConfigureAwait(false);
                progress?.Report($"Pushing {destinationBranch} to {remote}…");
                var result = await PushCommitAsync(root, remote, destinationBranch, snapshot.HeadCommit, token).ConfigureAwait(false);
                progress?.Report("Push confirmed. Refreshing local status…");
                try { await DeletePendingSyncAsync(root, pending, token).ConfigureAwait(false); }
                catch (Exception exception) when (IsPowerFailure(exception))
                {
                    return "Push completed. " + result + "\nThe launcher recovery record could not be cleared. Refresh and explicitly dismiss its tracking; do not repeat the push solely to clear it." + summaryNotice;
                }
                return "Commit and push after merge completed. " + result + summaryNotice;
            }
            catch (Exception exception) when (IsPowerFailure(exception))
            {
                var interrupted = IsInterruptedPowerFailure(exception);
                await MarkSyncForReviewAsync(root, pending with { Stage = committed && !interrupted ? GitSyncStage.PushUncertain : GitSyncStage.NeedsReview }).ConfigureAwait(false);
                throw new GitCommitPushException((committed ? "The final local commit remains saved. " : "The final commit was not confirmed; inspect local history and any active merge. ")
                    + "Push completion was not confirmed. Refresh and review the exact recorded commit before explicitly retrying or dismissing tracking.\n" + Sanitize(exception.Message) + summaryNotice, committed, exception);
            }
        });

    public static Task<string> DismissSyncAsync(string root, string remote, string expectedAttemptId, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async snapshot =>
        {
            RequireNoOperation(snapshot);
            var pending = await ReadPendingSyncAsync(root, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No release sync is tracked for this checkout.");
            if (pending.AttemptId != expectedAttemptId)
                throw new InvalidOperationException("The tracked release sync changed after your review. Refresh before dismissing its tracking.");
            if (pending.Remote != remote) throw new InvalidOperationException("Select the tracked sync's remote before dismissing its recovery record.");
            await DeletePendingSyncAsync(root, pending, token).ConfigureAwait(false);
            return "Launcher sync tracking cleared. Existing files, branches and checkpoint commits were preserved. This does not establish whether an earlier push completed.";
        });

    private static void RequireReviewedAgentSummaries(string root, string remoteName, GitRepositorySnapshot snapshot, AgentGitSummaryBatch? summaries)
    {
        if (summaries == null) return;
        var remote = snapshot.Remotes.FirstOrDefault(item => item.Name == remoteName);
        if (!string.Equals(RequireFolder(root), RequireFolder(summaries.RepositoryRoot), StringComparison.OrdinalIgnoreCase)
            || summaries.Branch != snapshot.Branch || summaries.RemoteName != remoteName || remote == null
            || summaries.ConnectionId != AgentGitChangeStore.ConnectionId(remote))
            throw new InvalidOperationException("The checkout, branch or Git connection changed after reviewing agent summaries. Refresh and review before committing.");
    }

    private static async Task<string> ConsumeReviewedAgentSummariesAsync(AgentGitSummaryBatch? summaries, string commitId)
    {
        if (summaries is not { Entries.Count: > 0 }) return "";
        try
        {
            // A confirmed commit remains saved even when push or user cancellation follows.
            // Consume only the captured IDs; submissions arriving during review stay pending.
            await AgentGitChangeStore.ConsumeAsync(summaries, commitId, CancellationToken.None).ConfigureAwait(false);
            return "";
        }
        catch (Exception exception) when (IsPowerFailure(exception))
        {
            return "\nThe local commit was confirmed, but its reviewed agent summaries could not be cleared. They remain pending; exclude already committed summaries from the next message. " + Sanitize(exception.Message);
        }
    }

    private static async Task<GitRepositorySnapshot> CommitAllCoreAsync(string root, GitRepositorySnapshot snapshot, string message, string? mergeHead, CancellationToken token)
    {
        ValidateCommitMessage(message);
        if (snapshot.Changes.Any(change => change.IsConflict))
            throw new InvalidOperationException("Resolve and stage all Git conflicts in your editor before finishing. The launcher will not mark unresolved paths as resolved.");
        if (mergeHead is null) RequireNoOperation(snapshot);
        else if (snapshot.OperationState != "Merge in progress" || await ReadMergeHeadAsync(root, token).ConfigureAwait(false) != mergeHead)
            throw new InvalidOperationException("The active merge identity changed before committing.");
        if (snapshot.Changes.Count == 0 && mergeHead is null)
        {
            if (snapshot.IsUnborn) throw new InvalidOperationException("There are no files to create the initial commit. Add files or use an existing branch.");
            return snapshot;
        }
        await RequireCommitIdentityAsync(root, token).ConfigureAwait(false);
        var beforeStage = await ReadCoreAsync(root, token).ConfigureAwait(false);
        RequireSameLocalState(snapshot, beforeStage);
        EnsureSuccess(await GitAsync(root, ["add", "--all", "--", ":/"], token).ConfigureAwait(false));
        var staged = await ReadCoreAsync(root, token).ConfigureAwait(false);
        RequireExactRoot(root, staged);
        if (staged.Branch != snapshot.Branch || staged.HeadCommit != snapshot.HeadCommit || staged.IsDetached || staged.Changes.Any(change => change.IsConflict))
            throw new InvalidOperationException("The branch or conflict state changed while staging. Inspect the staged files before retrying.");
        if (mergeHead is not null && await ReadMergeHeadAsync(root, token).ConfigureAwait(false) != mergeHead)
            throw new InvalidOperationException("The merge source changed while staging. Inspect the active merge before continuing.");
        if (mergeHead is null) RequireNoOperation(staged);
        if (mergeHead is null && !staged.Changes.Any(change => change.HasStaged))
            throw new InvalidOperationException("No staged file changes were available. Dirty submodules must be committed in their own repositories first.");
        var tree = await GitAsync(root, ["write-tree"], token).ConfigureAwait(false);
        EnsureSuccess(tree);
        var expectedTree = tree.Output.Trim();
        if (!IsObjectId(expectedTree)) throw new InvalidOperationException("The staged tree could not be verified.");
        EnsureSuccess(await GitAsync(root, ["commit", "--no-status", "--message", message], token).ConfigureAwait(false));
        var committed = await ReadCoreAsync(root, token).ConfigureAwait(false);
        RequireExactRoot(root, committed);
        RequireNoOperation(committed);
        if (committed.IsDetached || committed.Branch != snapshot.Branch || committed.HeadCommit == snapshot.HeadCommit)
            throw new InvalidOperationException("Git reported a commit but its resulting branch tip could not be verified. Inspect local history before retrying.");
        var identity = await GitAsync(root, ["show", "--no-patch", "--no-notes", "--no-show-signature", "--format=%T%x00%P", committed.HeadCommit, "--"], token).ConfigureAwait(false);
        EnsureSuccess(identity);
        var fields = identity.Output.TrimEnd('\r', '\n').Split('\0');
        var expectedParents = mergeHead is null ? snapshot.HeadCommit : snapshot.HeadCommit + " " + mergeHead;
        if (fields.Length != 2 || fields[0] != expectedTree || fields[1] != expectedParents)
            throw new InvalidOperationException("The new commit differs from the reviewed staged tree or parents. It remains local; inspect history before pushing.");
        return committed;
    }

    private static async Task RequireCommitIdentityAsync(string root, CancellationToken token)
    {
        EnsureSuccess(await GitAsync(root, ["var", "GIT_AUTHOR_IDENT"], token).ConfigureAwait(false));
        EnsureSuccess(await GitAsync(root, ["var", "GIT_COMMITTER_IDENT"], token).ConfigureAwait(false));
    }

    private static async Task ValidateDestinationAsync(string root, string remote, string destinationBranch, GitRepositorySnapshot snapshot, CancellationToken token)
    {
        await ValidateBranchAsync(root, destinationBranch, token).ConfigureAwait(false);
        if (snapshot.Branch == destinationBranch && !snapshot.IsDetached) return;
        if (snapshot.Branches.Any(branch => !branch.IsRemote && branch.Name.Equals(destinationBranch, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That destination is another existing local branch. Switch and review it separately; this flow only keeps the current branch or creates a new branch from its complete history.");
        await RequireRemoteBranchAsync(root, remote, destinationBranch, mustExist: false, token).ConfigureAwait(false);
    }

    private static async Task<GitRepositorySnapshot> CreateDestinationCoreAsync(string root, GitRepositorySnapshot snapshot, string destinationBranch, CancellationToken token)
    {
        RequireNoOperation(snapshot);
        if (snapshot.IsUnborn || snapshot.IsDetached) throw new InvalidOperationException("A verified local commit is required before creating the new destination branch.");
        var current = await ReadCoreAsync(root, token).ConfigureAwait(false);
        RequireSameLocalState(snapshot, current);
        if (current.Branches.Any(branch => !branch.IsRemote && branch.Name.Equals(destinationBranch, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The new destination branch was created by another operation. Refresh and review it separately.");
        EnsureSuccess(await GitAsync(root,
            ["switch", "--no-guess", "--no-overwrite-ignore", "--no-recurse-submodules", "-c", destinationBranch, snapshot.HeadCommit], token).ConfigureAwait(false));
        var created = await ReadCoreAsync(root, token).ConfigureAwait(false);
        if (created.IsDetached || created.Branch != destinationBranch || created.HeadCommit != snapshot.HeadCommit
            || created.WorkingTreeFingerprint != snapshot.WorkingTreeFingerprint)
            throw new InvalidOperationException("The branch creation outcome could not be verified. Refresh and inspect local history and files; no reset or rollback was attempted.");
        return created;
    }

    private static void RequireSameLocalState(GitRepositorySnapshot expected, GitRepositorySnapshot current)
    {
        if (!current.IsRepository || current.RepositoryRoot != expected.RepositoryRoot || current.Branch != expected.Branch
            || current.HeadCommit != expected.HeadCommit || current.IsDetached != expected.IsDetached
            || current.WorkingTreeFingerprint != expected.WorkingTreeFingerprint || current.OperationState != expected.OperationState)
            throw new InvalidOperationException("Local branch, staged files or working tree changed during this operation. Refresh and review before continuing.");
    }

    private static async Task<string> RequirePowerRemoteAsync(string root, string remote, CancellationToken token)
    {
        var fetch = await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
        var push = await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
        if (fetch != push) throw new InvalidOperationException("Use one connection with the same fetch and push destination for this workflow. Configure a separate remote for a different repository.");
        return fetch;
    }

    private static async Task RequireUnchangedPowerRemoteAsync(string root, string remote, string expectedUrl, CancellationToken token)
    {
        if (await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false) != expectedUrl)
            throw new InvalidOperationException("The reviewed remote destination changed during this operation. Refresh before continuing.");
    }

    private static async Task RequireRemoteBranchAsync(string root, string remote, string branch, bool mustExist, CancellationToken token)
    {
        await RequireSingleRemoteAsync(root, remote, mustExist ? false : true, token).ConfigureAwait(false);
        var result = await GitAsync(root, ["ls-remote", "--exit-code", "--heads", "--", remote, "refs/heads/" + branch], token, network: true).ConfigureAwait(false);
        if (result.ExitCode == 2)
        {
            if (mustExist) throw new InvalidOperationException("That release branch is no longer present on the selected remote. Reload the remote branch list.");
            return;
        }
        EnsureSuccess(result);
        var branches = ParseRemoteBranches(result.Output);
        if (branches.Count != 1 || branches[0].Name != branch)
            throw new InvalidOperationException("The remote branch response could not be verified. Branch presence is unknown.");
        if (!mustExist) throw new InvalidOperationException("That destination already exists on the selected remote. Switch to and review its corresponding local branch separately, or choose a new branch name.");
    }

    private static IReadOnlyList<GitRemoteBranchChoice> ParseRemoteBranches(string output)
    {
        var branches = new List<GitRemoteBranchChoice>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 2 || !IsObjectId(fields[0]) || !fields[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                || !names.Add(fields[1]) || fields[1].Any(char.IsControl))
                throw new InvalidOperationException("Git returned an incomplete or ambiguous remote branch list.");
            branches.Add(new GitRemoteBranchChoice(fields[1]["refs/heads/".Length..], fields[0]));
        }
        return branches.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string?> ReadReleaseBranchAsync(string root, string remote, CancellationToken token)
    {
        var url = await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false);
        var branch = await GitAsync(root, ["config", "--local", "--get-all", $"remote.{remote}.launcherReleaseBranch"], token).ConfigureAwait(false);
        var target = await GitAsync(root, ["config", "--local", "--get-all", $"remote.{remote}.launcherReleaseTarget"], token).ConfigureAwait(false);
        if (branch.ExitCode is not (0 or 1)) ThrowCommandFailure(branch);
        if (target.ExitCode is not (0 or 1)) ThrowCommandFailure(target);
        if (branch.ExitCode != 0 || target.Output.TrimEnd('\r', '\n') != RemoteTargetFingerprint(url)) return null;
        var name = branch.Output.TrimEnd('\r', '\n');
        try { await ValidateBranchAsync(root, name, token).ConfigureAwait(false); return name; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task SaveReleaseBranchCoreAsync(string root, string remote, string sourceBranch, string url, CancellationToken token)
    {
        if (await RequireSingleRemoteAsync(root, remote, false, token).ConfigureAwait(false) != url)
            throw new InvalidOperationException("The remote changed before the release branch choice could be saved.");
        EnsureSuccess(await GitAsync(root, ["config", "--local", "--replace-all", $"remote.{remote}.launcherReleaseBranch", sourceBranch], token).ConfigureAwait(false));
        EnsureSuccess(await GitAsync(root, ["config", "--local", "--replace-all", $"remote.{remote}.launcherReleaseTarget", RemoteTargetFingerprint(url)], token).ConfigureAwait(false));
    }

    private static async Task<(string? Branch, DateTimeOffset? At)> ReadLastCreatedBranchAsync(string root, GitRepositorySnapshot snapshot, CancellationToken token)
    {
        CommandResult result;
        try { result = await GitAsync(root, ["reflog", "show", "--all", "--max-count=5000", "--date=unix", "--format=%gD%x00%gs"], token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, null); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { return (null, null); }
        if (result.ExitCode != 0) return (null, null);
        var known = snapshot.Branches.Where(branch => !branch.IsRemote).Select(branch => branch.Name).ToHashSet(StringComparer.Ordinal);
        string? latest = null;
        DateTimeOffset? latestAt = null;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length != 2 || !fields[0].StartsWith("refs/heads/", StringComparison.Ordinal)
                || !(fields[1].StartsWith("branch: Created from ", StringComparison.Ordinal) || fields[1].StartsWith("commit (initial):", StringComparison.Ordinal))) continue;
            var marker = fields[0].LastIndexOf("@{", StringComparison.Ordinal);
            if (marker < "refs/heads/".Length || !fields[0].EndsWith('}')) continue;
            var branch = fields[0]["refs/heads/".Length..marker];
            if (!known.Contains(branch) || !long.TryParse(fields[0][(marker + 2)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) continue;
            DateTimeOffset when;
            try { when = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (latestAt is null || when > latestAt) { latest = branch; latestAt = when; }
        }
        return (latest, latestAt);
    }

    private static IReadOnlyList<string> ConflictPaths(GitRepositorySnapshot snapshot) => snapshot.Changes.Where(change => change.IsConflict)
        .Select(change => change.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string CheckpointDescription(GitPendingSync pending) => pending.CheckpointCommit is { } commit
        ? $"Local changes were preserved in checkpoint {commit[..Math.Min(12, commit.Length)]}. "
        : "No protective checkpoint commit was confirmed. ";

    private static string RecoveryMessage(GitPendingSync pending, string remote, IReadOnlyList<string> conflicts, bool ready) =>
        remote.Length == 0 ? $"A release sync for {pending.Remote}/{pending.SourceBranch} remains tracked on {pending.DestinationBranch}, but no remote is selected. Inspect local history and any interrupted command. Finish or abort an active merge with Git before dismissing tracking; existing files and commits stay in place."
        : pending.Remote != remote ? $"A release sync for {pending.Remote}/{pending.SourceBranch} is tracked on {pending.DestinationBranch}. Select {pending.Remote} to finish it, or inspect the interrupted command and local/remote history before dismissing this tracking. Existing files and commits stay in place."
        : conflicts.Count > 0 ? CheckpointDescription(pending) + "Resolve and stage the listed conflicts with Git or your editor, then refresh. Nothing is automatically retried."
        : ready ? pending.Stage is GitSyncStage.ReadyToPush or GitSyncStage.PushUncertain
            ? "The recorded local commit is ready for an explicit push attempt. An earlier push may already have completed; inspect the remote if its outcome was uncertain."
            : "The release merge is ready locally. Use Commit all & push to review the destination and publish when ready."
        : "The tracked sync needs review. Inspect the current branch, local history and any Git operation. Finish or abort a merge with Git before dismissing tracking. Checkpoints and branch changes remain in place.";

    private static void RequirePendingBranch(GitPendingSync pending, GitRepositorySnapshot snapshot)
    {
        if (snapshot.IsDetached || snapshot.Branch != pending.DestinationBranch || snapshot.HeadCommit != pending.PreparedHead)
            throw new InvalidOperationException("The branch or starting commit changed during release sync. Inspect local history before continuing.");
    }

    private static async Task<bool> CanFinishSyncAsync(string root, string remote, GitRepositorySnapshot snapshot, GitPendingSync pending, CancellationToken token)
    {
        if (pending.Remote != remote || snapshot.IsDetached || ConflictPaths(snapshot).Count > 0) return false;
        string url;
        try { url = await RequirePowerRemoteAsync(root, remote, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return false; }
        if (RemoteTargetFingerprint(url) != pending.RemoteFingerprint) return false;
        if (pending.Stage is GitSyncStage.ReadyToPush or GitSyncStage.PushUncertain)
            return pending.FinalBranch == snapshot.Branch && pending.FinalCommit == snapshot.HeadCommit
                && snapshot.OperationState is null && snapshot.Changes.Count == 0;
        if (pending.Stage is not (GitSyncStage.Conflicts or GitSyncStage.AwaitingConfirmation)
            || !IsObjectId(pending.SourceCommit) || snapshot.Branch != pending.DestinationBranch || snapshot.HeadCommit != pending.PreparedHead) return false;
        var mergeHead = await ReadMergeHeadAsync(root, token).ConfigureAwait(false);
        if (mergeHead is not null) return snapshot.OperationState == "Merge in progress" && mergeHead == pending.SourceCommit;
        return snapshot.OperationState is null && await IsAncestorAsync(root, pending.SourceCommit, snapshot.HeadCommit, token).ConfigureAwait(false);
    }

    private static async Task<bool> IsAncestorAsync(string root, string ancestor, string descendant, CancellationToken token)
    {
        if (!IsObjectId(ancestor) || !IsObjectId(descendant)) return false;
        var result = await GitAsync(root, ["merge-base", "--is-ancestor", ancestor, descendant], token).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1)) ThrowCommandFailure(result);
        return result.ExitCode == 0;
    }

    private static async Task<string?> ReadMergeHeadAsync(string root, CancellationToken token)
    {
        var path = await GitMetadataPathAsync(root, "MERGE_HEAD", token).ConfigureAwait(false);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256) throw new InvalidOperationException("The active merge contains unsupported or unreadable source identities.");
        var value = (await File.ReadAllTextAsync(path, token).ConfigureAwait(false)).TrimEnd('\r', '\n');
        if (!IsObjectId(value)) throw new InvalidOperationException("The active merge is not a single verified source commit.");
        return value;
    }

    private static async Task<string> GitMetadataPathAsync(string root, string name, CancellationToken token)
    {
        var result = await GitAsync(root, ["rev-parse", "--git-path", name], token).ConfigureAwait(false);
        EnsureSuccess(result);
        return Path.GetFullPath(result.Output.TrimEnd('\r', '\n'), root);
    }

    private static async Task<GitPendingSync?> ReadPendingSyncAsync(string root, CancellationToken token)
    {
        var path = await GitMetadataPathAsync(root, PendingSyncFile, token).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > 32 * 1024) throw new InvalidDataException();
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var pending = JsonSerializer.Deserialize<GitPendingSync>(bytes, SyncJson);
            if (pending is null || pending.Version != 1 || !Guid.TryParseExact(pending.AttemptId, "N", out _)
                || !string.Equals(pending.RepositoryRoot, RequireFolder(root), StringComparison.OrdinalIgnoreCase)
                || !IsValidRemoteName(pending.Remote) || pending.RemoteFingerprint is not { Length: 64 } || !pending.RemoteFingerprint.All(char.IsAsciiHexDigit)
                || !IsObjectId(pending.OriginalHead) || !IsObjectId(pending.PreparedHead)
                || !Enum.IsDefined(pending.Stage) || string.IsNullOrEmpty(pending.DestinationBranch) || string.IsNullOrEmpty(pending.SourceBranch)
                || pending.DestinationBranch.Any(char.IsControl) || pending.SourceBranch.Any(char.IsControl)
                || pending.SourceCommit is null || (pending.SourceCommit.Length > 0 && !IsObjectId(pending.SourceCommit))
                || (pending.CheckpointCommit is { } checkpoint && !IsObjectId(checkpoint))
                || (pending.FinalCommit is { } final && !IsObjectId(final))) throw new InvalidDataException();
            return pending;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The checkout's launcher sync recovery record is unreadable or incompatible. Preserve it and inspect the repository with Git; no new sync was started."); }
    }

    private static async Task WritePendingSyncAsync(string root, GitPendingSync pending, bool create, CancellationToken token)
    {
        var path = await GitMetadataPathAsync(root, PendingSyncFile, token).ConfigureAwait(false);
        if (!create)
        {
            var current = await ReadPendingSyncAsync(root, token).ConfigureAwait(false);
            if (current?.AttemptId != pending.AttemptId) throw new InvalidOperationException("The launcher sync recovery identity changed. Inspect the checkout before continuing.");
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, pending, SyncJson, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: !create);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task DeletePendingSyncAsync(string root, GitPendingSync pending, CancellationToken token)
    {
        var current = await ReadPendingSyncAsync(root, token).ConfigureAwait(false);
        if (current?.AttemptId != pending.AttemptId) throw new InvalidOperationException("The launcher sync recovery identity changed; tracking was not cleared.");
        File.Delete(await GitMetadataPathAsync(root, PendingSyncFile, token).ConfigureAwait(false));
    }

    private static async Task MarkSyncForReviewAsync(string root, GitPendingSync pending)
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await WritePendingSyncAsync(root, pending with { Stage = pending.Stage == GitSyncStage.PushUncertain ? GitSyncStage.PushUncertain : GitSyncStage.NeedsReview }, false, recovery.Token).ConfigureAwait(false);
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (Exception exception) when (IsPowerFailure(exception)) { /* Preserve the last durable phase when a recovery write fails. */ }
    }

    private static bool IsPowerFailure(Exception exception) => exception is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException;

    private static bool IsInterruptedPowerFailure(Exception exception) => exception is OperationCanceledException or IOException or UnauthorizedAccessException or DecoderFallbackException
        || exception.InnerException is { } inner && IsInterruptedPowerFailure(inner);

    private static async Task<FileStream> AcquireRepositoryOperationLeaseAsync(string root, CancellationToken token)
    {
        var common = await GitAsync(root, ["rev-parse", "--git-common-dir"], token).ConfigureAwait(false);
        EnsureSuccess(common);
        var directory = Path.GetFullPath(common.Output.TrimEnd('\r', '\n'), root);
        try
        {
            // File sharing is enforced across launcher processes and is not thread-affine across awaits.
            // The common Git directory also serializes linked-worktree operations that share branch refs.
            return new FileStream(Path.Combine(directory, "launcher-operation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException)
        { throw new InvalidOperationException("Another launcher Git operation is using this repository, or its operation lock cannot be opened. Wait for that operation to finish and retry."); }
        catch (UnauthorizedAccessException)
        { throw new InvalidOperationException("The repository operation lock is not writable. Check Git metadata permissions before continuing."); }
    }

    private static async Task RequireNoPendingSyncAsync(string root, CancellationToken token)
    {
        if (await ReadPendingSyncAsync(root, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A tracked release sync is pending. Finish it or explicitly dismiss its tracking before using another mutating Git action.");
    }
}
