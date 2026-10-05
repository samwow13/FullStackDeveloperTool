using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private GitPowerDetails? _powerDetails;
    private string? _powerReadError;
    private string _syncMessageDraft = "";
    private CommitDraftScope? _commitDraftScope;
    private bool HasSyncRecovery => _powerDetails?.PendingSync != null || !string.IsNullOrEmpty(_powerDetails?.RecoveryMessage);
    public bool CanPrepareSync => IsIdle && HasRemote && _powerDetails != null && !HasSyncRecovery
        && _snapshot is { IsDetached: false, IsUnborn: false, OperationState: null }
        && !Changes.Any(change => change.IsConflict);
    public bool CanChangeReleaseBranch => CanPrepareSync;
    public bool CanCheckReleaseMerge => CanPrepareSync;
    public bool CanFinishSync => IsIdle && HasRemote && _powerDetails?.ReadyToFinish == true;
    public bool CanDismissSync => IsIdle && _powerDetails?.CanDismiss == true;
    public Visibility SyncRecoveryVisibility => HasSyncRecovery || Changes.Any(change => change.IsConflict) ? Visibility.Visible : Visibility.Collapsed;
    public string ReleaseSourceLabel => _powerDetails?.ReleaseBranch is { Length: > 0 } source && SelectedRemote is { } remote
        ? $"Release source: {remote.Name}/{source}"
        : _powerDetails?.SuggestedReleaseBranch is { Length: > 0 } suggested && SelectedRemote is { } selectedRemote
            ? $"Suggested release source: {selectedRemote.Name}/{suggested} · auto-detected"
            : "Release source: choose a remote branch in Config.";
    public string ReleaseSourceTooltip => _powerDetails?.ReleaseBranch is { Length: > 0 }
        ? "Saved release source for this repository and remote URL. Confirm against the fresh remote branch list before merging or testing."
        : (_powerDetails?.ReleaseBranchDetectionDetail ?? "Load a repository and active remote to detect a release source.")
            + " Confirm the source in the branch chooser before saving, merging, or testing.";
    public string PowerAvailabilityText => _powerReadError ?? (!IsRepository ? "Select or initialize a repository first."
        : !HasRemote ? "Connect a remote in Config first."
        : HasSyncRecovery ? "A local merge is tracked below. Use Commit all & push when it is ready."
        : _snapshot!.IsDetached ? "Switch to a local branch first."
        : _snapshot.IsUnborn ? "Create the first local commit before merging an existing release branch."
        : _snapshot.OperationState != null ? "Finish the active Git operation first."
        : "Verify the remote source, then check for incoming commits and merge conflicts. If your current branch already includes the source, return here to commit your work. Otherwise review the local merge. Use Commit all & push separately when ready.");
    public string SyncRecoverySummary
    {
        get
        {
            var conflicts = _powerDetails?.ConflictedPaths ?? Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
            var pending = _powerDetails?.PendingSync;
            var header = pending == null ? "Git needs attention."
                : $"Local merge: {pending.Remote}/{pending.SourceBranch} into {pending.DestinationBranch}";
            return header + (conflicts.Count > 0 ? $"\n{conflicts.Count:N0} conflicted file(s). Resolve and stage them in your editor or Git, then refresh."
                    : _powerDetails?.ReadyToFinish == true ? pending?.FinalCommit != null
                        ? "\nThe local commit is saved. Review the earlier push outcome before using Commit all & push."
                        : "\nNo unresolved Git conflicts. Changes remain local. Use Commit all & push to choose the branch and publish."
                    : "\nReview the saved operation before continuing.")
                + (string.IsNullOrWhiteSpace(_powerDetails?.RecoveryMessage) ? "" : "\n" + _powerDetails.RecoveryMessage);
        }
    }

    private async Task RefreshPowerDetailsAsync(CancellationToken token)
    {
        _powerDetails = null;
        _powerReadError = null;
        if (Root is not { } root) { Changed(); return; }
        try { _powerDetails = await GitRepositoryService.ReadPowerDetailsAsync(root, SelectedRemote?.Name ?? "", token); }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (Exception ex)
        {
            _powerReadError = "Git workflow settings could not be read. Refresh before committing or merging a release branch. Review error details below.";
            ReportError(SafeError(ex));
        }
        Changed();
    }

    private async Task<bool> ChooseReleaseBranchAsync(GitReleaseBranchMode mode = GitReleaseBranchMode.Choose)
    {
        if (!CanChangeReleaseBranch || Root is not { } root || SelectedRemote is not { } remote) return false;
        var currentBranch = _snapshot!.Branch;
        var savedSource = _powerDetails?.ReleaseBranch;
        var picker = new GitReleaseBranchWindow(root, remote.Name, remote.FetchUrl, async pickerToken =>
        {
            // The picker renders first and owns loading feedback. Local status stays behind it.
            await RefreshAsync(showLoading: false, cancellationToken: pickerToken);
            if (_unconfirmedGitCommand is { } unconfirmed) throw unconfirmed;
            pickerToken.ThrowIfCancellationRequested();
            if (_gitLoadFailed || _snapshot == null)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(LatestErrorDetails) ? GitLoadFailure : LatestErrorDetails);
            if (!CanChangeReleaseBranch)
                throw new InvalidOperationException(PowerAvailabilityText);
            if (_snapshot.Branch != currentBranch || !string.Equals(Root, root, StringComparison.OrdinalIgnoreCase)
                || SelectedRemote is not { } currentRemote || currentRemote.Name != remote.Name
                || AgentGitChangeStore.ConnectionId(currentRemote) != AgentGitChangeStore.ConnectionId(remote))
                throw new InvalidOperationException("The current branch or active remote changed. Close this chooser, review the Git workspace, and verify the source branch again.");

            IReadOnlyList<GitRemoteBranchChoice>? branches = null;
            Exception? readFailure = null;
            var loaded = await ExecuteAsync("Read remote release branches", async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, pickerToken);
                try
                {
                    branches = await GitRepositoryService.ListRemoteBranchesAsync(root, remote.Name, linked.Token);
                    return $"Read {branches.Count:N0} branch(es) from {remote.Name}. No files were merged or pushed.";
                }
                catch (Exception exception) { readFailure = exception; throw; }
            }, refresh: false, useResultAsStatus: true,
                cancellationContext: " Branch loading canceled. No merge, checkpoint or push was started.");
            if (_unconfirmedGitCommand is { } command) throw command;
            pickerToken.ThrowIfCancellationRequested();
            if (!loaded || branches == null)
                throw readFailure ?? new InvalidOperationException("Remote branches could not load. Retry in this chooser or return to Git to review the connection.");
            return branches;
        }, savedSource ?? _powerDetails?.SuggestedReleaseBranch,
            currentBranch, mode, suggestReleaseBranch: savedSource is null,
            cachedDefaultBranch: _powerDetails?.CachedRemoteDefaultBranch) { Owner = this };
        var accepted = picker.ShowDialog() == true;
        if (picker.UnconfirmedCommand is { } pendingCommand)
        {
            HoldUnconfirmedBranchCommand(pendingCommand);
            return false;
        }
        if (!accepted || picker.SelectedBranch is not { } selected) return false;
        savedSource = _powerDetails?.ReleaseBranch;
        // Confirming the saved source is read-only; save only an explicitly changed choice.
        if (selected != savedSource
            && (!await ExecuteAsync("Save release branch", token => GitRepositoryService.SaveReleaseBranchAsync(root, remote.Name, selected, token), useResultAsStatus: true)
                || _powerDetails?.ReleaseBranch != selected)) return false;
        if (mode != GitReleaseBranchMode.Choose
            && (_snapshot?.Branch != currentBranch || !string.Equals(Root, root, StringComparison.OrdinalIgnoreCase)
                || SelectedRemote is not { } currentRemote || currentRemote.Name != remote.Name
                || AgentGitChangeStore.ConnectionId(currentRemote) != AgentGitChangeStore.ConnectionId(remote)))
        {
            SetStatus("The current branch or active remote changed during verification. The saved release choice is retained. Verify the release branch again.");
            return false;
        }
        return true;
    }

    private async void ChangeReleaseBranch_Click(object sender, RoutedEventArgs e) => await ChooseReleaseBranchAsync();

    private async void CheckReleaseMerge_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCheckReleaseMerge) return;
        if (!await ChooseReleaseBranchAsync(GitReleaseBranchMode.VerifyConflictTest)) return;
        if (!CanCheckReleaseMerge || Root is not { } root || SelectedRemote is not { } remote
            || _powerDetails?.ReleaseBranch is not { } source) return;
        var result = await CheckVerifiedReleaseAsync(root, remote, source, forLocalMerge: false);
        if (result != null) new GitMergeCheckWindow(root, result) { Owner = this }.ShowDialog();
    }

    private async Task<GitMergeCheckResult?> CheckVerifiedReleaseAsync(string root, GitRemoteInfo remote, string source, bool forLocalMerge)
    {
        GitMergeCheckResult? result = null;
        CancellationTokenSource? operation = null;
        var progress = new Progress<string>(message =>
        {
            if (_busy && ReferenceEquals(_operation, operation)) SetStatus(message);
        });
        var checkedSource = await ExecuteAsync($"Compare current branch with {remote.Name}/{source}", async token =>
        {
            operation = _operation;
            result = await GitRepositoryService.CheckReleaseMergeAsync(root, remote.Name, source, token, progress,
                skipUpToDateSimulation: forLocalMerge);
            return result.IncomingCommits == 0
                ? $"Local branch {result.CurrentBranch} is up to date with {remote.Name}/{source}; it already includes every source commit. No merge or checkpoint is needed. Use Commit all & push when ready."
                : $"{remote.Name}/{source} has {result.IncomingCommits:N0} incoming commit(s) for {result.CurrentBranch}. "
                    + (result.HasConflicts ? "Git found merge conflicts." : "Git found no merge conflicts between the committed branches.")
                    + " Uncommitted edits are excluded. Review the comparison details before continuing.";
        }, refresh: false, useResultAsStatus: true,
            cancellationContext: " Conflict test canceled. No merge, checkpoint or push was started; local files remain unchanged.");
        if (!checkedSource || result == null) return null;
        if (Owner is MainWindow dashboard)
            dashboard.RecordWorkspaceGitConflictCheck(new(root, result.CurrentBranch, remote.Name, AgentGitChangeStore.ConnectionId(remote)), result);
        return result;
    }

    private async void SyncRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPrepareSync) return;
        if (!await ChooseReleaseBranchAsync(GitReleaseBranchMode.VerifyLocalMerge)) return;
        if (!CanPrepareSync || _snapshot is not { } snapshot || Root is not { } root || SelectedRemote is not { } remote
            || _powerDetails?.ReleaseBranch is not { } source) return;
        var comparison = await CheckVerifiedReleaseAsync(root, remote, source, forLocalMerge: true);
        if (comparison == null) return;
        var continueToReview = new GitMergeCheckWindow(root, comparison, reviewLocalMerge: true) { Owner = this }.ShowDialog() == true;
        if (comparison.IncomingCommits == 0 || !continueToReview) return;
        if (!CanPrepareSync) return;
        var prompt = new GitCommitWindow(snapshot, remote, null,
            mode: GitCommitMode.PrepareSync, sourceLabel: $"{remote.Name}/{source}",
            recoveryNote: comparison.HasConflicts
                ? "The Git comparison found conflicts between the committed branches. Continuing starts a local merge and leaves conflicts for you to resolve and stage in Git or your editor. Uncommitted edits may add more conflicts."
                : "The Git comparison found no conflicts between the committed branches. Uncommitted edits are excluded and may produce conflicts after the protective checkpoint.") { Owner = this };
        var accepted = prompt.ShowDialog() == true;
        if (!accepted) return;
        _syncMessageDraft = "";
        GitSyncResult? result = null;
        CancellationTokenSource? operation = null;
        var progress = new Progress<string>(message =>
        {
            if (_busy && ReferenceEquals(_operation, operation)) SetStatus(message);
        });
        var prepared = await ExecuteAsync($"Merge {remote.Name}/{source} into {snapshot.Branch} locally", async token =>
        {
            operation = _operation;
            result = await GitRepositoryService.PrepareSyncAsync(root, remote.Name, source, token,
                reviewedCheck: comparison, reviewedSnapshot: snapshot, progress: progress);
            return result.Message;
        }, useResultAsStatus: true);
        if ((result?.ConflictedPaths.Count ?? 0) > 0 || (!prepared && HasSyncRecovery))
            ShowSyncRecovery(result?.Message);
    }

    private async Task ReviewCommitAllPushAsync()
    {
        if (!CanCommitAllPush) return;
        await RefreshAsync();
        if (CanFinishSync) { await ReviewAndFinishSyncAsync(); return; }
        if (!CanCommitAllPush || _snapshot is not { } snapshot || Root is not { } root || SelectedRemote is not { } remote) return;
        var destinationBranch = PublishBranchDraft.Trim();
        AgentGitSummaryBatch? agentSummaries = null;
        if (!await ExecuteAsync("Read agent change summaries", async token =>
        {
            agentSummaries = await AgentGitChangeStore.ReadPendingAsync(root, snapshot.Branch, remote.Name, AgentGitChangeStore.ConnectionId(remote), token);
            return $"Read {agentSummaries.Entries.Count:N0} pending agent update(s) for {snapshot.Branch} and {remote.Name}.";
        }, refresh: false)) return;
        var prompt = CreateCommitReview(() =>
        {
            var messageDraft = ReadScopedCommitDraft(root, snapshot.Branch, remote);
            return new GitCommitWindow(snapshot, remote, _powerDetails?.LastCreatedBranch,
                destinationBranch, messageDraft, agentSummaries: agentSummaries, freezeDestination: true) { Owner = this };
        });
        if (prompt == null) return;
        var accepted = prompt.ShowDialog() == true;
        _commitDraft = prompt.MessageWasEdited ? prompt.Message : "";
        if (!accepted) return;
        var branch = prompt.TargetBranch;
        var message = prompt.Message;
        var progress = new Progress<string>(SetPublishPhase);
        BeginPublishFeedback(branch, remote.Name);
        var reviewedSummaries = prompt.ReviewedAgentSummaries;
        var pushed = await ExecuteAsync("Commit all & push", async token =>
        {
            await AgentGitChangeStore.SelectConnectionAsync(root, remote.Name, AgentGitChangeStore.ConnectionId(remote), token);
            return await GitRepositoryService.CommitAllAndPushToBranchAsync(root, remote.Name, branch, message, token, progress, reviewedSummaries);
        },
            completed: _ => { _commitDraft = ""; ResetPublishBranchDraftAfterSuccess(); }, useResultAsStatus: true,
            pushedRemote: remote);
        CompletePublishFeedback(pushed, StatusText);
    }

    private async Task ReviewAndFinishSyncAsync()
    {
        if (!CanFinishSync || _snapshot is not { } snapshot || _powerDetails?.PendingSync is not { } pending
            || Root is not { } root || SelectedRemote is not { } remote) return;
        var destinationBranch = PublishBranchDraft.Trim();
        AgentGitSummaryBatch? agentSummaries = null;
        if (!await ExecuteAsync("Read agent change summaries", async token =>
        {
            agentSummaries = await AgentGitChangeStore.ReadPendingAsync(root, snapshot.Branch, remote.Name, AgentGitChangeStore.ConnectionId(remote), token);
            return $"Read {agentSummaries.Entries.Count:N0} pending agent update(s) for {snapshot.Branch} and {remote.Name}.";
        }, refresh: false)) return;
        var prompt = CreateCommitReview(() =>
        {
            var messageDraft = ReadScopedCommitDraft(root, snapshot.Branch, remote);
            return new GitCommitWindow(snapshot, remote, _powerDetails!.LastCreatedBranch,
                destinationBranch, messageDraft, GitCommitMode.FinishSync, $"{pending.Remote}/{pending.SourceBranch}",
                _powerDetails.RecoveryMessage, agentSummaries, freezeDestination: true) { Owner = this };
        });
        if (prompt == null) return;
        var accepted = prompt.ShowDialog() == true;
        _syncMessageDraft = prompt.MessageWasEdited ? prompt.Message : "";
        if (!accepted)
        {
            SetStatus("Review closed. Local work is preserved; no new push was attempted. Choose Commit all & push when ready.");
            return;
        }
        var branch = prompt.TargetBranch;
        var message = prompt.Message;
        var progress = new Progress<string>(SetPublishPhase);
        BeginPublishFeedback(branch, remote.Name);
        var reviewedSummaries = prompt.ReviewedAgentSummaries;
        var pushed = await ExecuteAsync("Commit all & push after merge", async token =>
        {
            await AgentGitChangeStore.SelectConnectionAsync(root, remote.Name, AgentGitChangeStore.ConnectionId(remote), token);
            return await GitRepositoryService.FinishSyncAsync(root, remote.Name, branch, message, pending.AttemptId, token, progress, reviewedSummaries);
        },
            completed: _ => { _syncMessageDraft = ""; _commitDraft = ""; ResetPublishBranchDraftAfterSuccess(); }, useResultAsStatus: true,
            pushedRemote: remote);
        CompletePublishFeedback(pushed, StatusText);
    }

    private string? ReadScopedCommitDraft(string root, string branch, GitRemoteInfo remote)
    {
        var connectionId = AgentGitChangeStore.ConnectionId(remote);
        if (_commitDraftScope is not { } scope
            || !string.Equals(scope.Root, root, StringComparison.OrdinalIgnoreCase)
            || scope.Branch != branch || scope.ConnectionId != connectionId)
        {
            _commitDraft = "";
            _syncMessageDraft = "";
        }
        _commitDraftScope = new(root, branch, connectionId);
        // The shared checkout/branch/connection draft is authoritative. A folder draft must
        // never carry a manually edited message into a different branch or remote.
        return GitCommitPreviewState.Read(root, branch, connectionId) is { IsCustom: true } shared
            ? shared.Message : null;
    }

    private sealed record CommitDraftScope(string Root, string Branch, string ConnectionId);

    private GitCommitWindow? CreateCommitReview(Func<GitCommitWindow> create)
    {
        try { return create(); }
        catch (InvalidOperationException exception)
        {
            var detail = SafeError(exception);
            SetStatus(detail);
            ReportError(detail);
            return null;
        }
    }

    private void SyncDetails_Click(object sender, RoutedEventArgs e) => ShowSyncRecovery();
    private void ShowSyncRecovery(string? operationMessage = null)
    {
        if (_busy || Root is not { } root) return;
        var conflicts = _powerDetails?.ConflictedPaths ?? Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
        var details = string.Join("\n\n", new[] { operationMessage, SyncRecoverySummary }.Where(text => !string.IsNullOrWhiteSpace(text)));
        new GitConflictWindow(root, conflicts, details) { Owner = this }.ShowDialog();
    }
    private async void DismissSync_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDismissSync || Root is not { } root || _powerDetails?.PendingSync is not { } pending) return;
        if (!Confirm("Clear merge tracking", $"Clear tracking for {pending.Remote}/{pending.SourceBranch} into {pending.DestinationBranch}?\n\nConfirm that any interrupted Git command has finished and you have inspected the local and remote Git state.\n\nThis does not stop a command, undo a merge, remove a checkpoint, discard files, or verify a remote push. Existing files, branches and commits stay in place.")) return;
        await ExecuteAsync("Clear merge tracking", token => GitRepositoryService.DismissSyncAsync(root, pending.Remote, pending.AttemptId, token), useResultAsStatus: true);
    }
}
