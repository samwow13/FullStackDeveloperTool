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
    public bool CanFinishSync => IsIdle && HasRemote && _powerDetails?.ReadyToFinish == true;
    public bool CanDismissSync => IsIdle && !_cacheRefreshFailed && !_sharedRefreshing && _powerDetails?.CanDismiss == true;
    public Visibility SyncRecoveryVisibility => HasSyncRecovery || Changes.Any(change => change.IsConflict) ? Visibility.Visible : Visibility.Collapsed;
    public string ReleaseSourceLabel => _powerDetails?.ReleaseBranch is { Length: > 0 } source && SelectedRemote is { } remote
        ? $"Release source: {remote.Name}/{source}"
        : _powerDetails?.SuggestedReleaseBranch is { Length: > 0 } suggested && SelectedRemote is { } selectedRemote
            ? $"Suggested release source: {selectedRemote.Name}/{suggested} · auto-detected"
            : "Release source: choose a remote branch in Config.";
    public string ReleaseSourceTooltip => _powerDetails?.ReleaseBranch is { Length: > 0 }
        ? "Saved release branch for comparisons and Merge Locally. Fetch after online integration to update comparison totals. Merge Locally asks you to refresh before the conflict preview."
        : (_powerDetails?.ReleaseBranchDetectionDetail ?? "Load a repository and active remote to detect a release source.")
            + " Confirm the source in the branch chooser to enable release comparison, merging, or conflict testing.";
    public string PowerAvailabilityText => _powerReadError ?? (!IsRepository ? "Select or initialize a repository first."
        : _sharedRefreshing ? "Background Git refresh is running. Wait for it to finish before merging."
        : _cacheRefreshFailed ? "Force refresh to confirm current Git state before merging."
        : !HasRemote ? "Connect a remote in Config first."
        : HasSyncRecovery ? "A local merge is tracked below. Use Commit all & push when it is ready."
        : _snapshot!.IsDetached ? "Switch to a local branch first."
        : _snapshot.IsUnborn ? "Create the first local commit before merging an existing release branch."
        : _snapshot.OperationState != null ? "Finish the active Git operation first."
        : "Review the saved or suggested source immediately, then refresh and automatically preview incoming commits and conflicts. Review the result before approving a local merge. Use Commit all & push separately when ready.");
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

    private async Task<bool> ChooseReleaseBranchAsync(GitReleaseBranchMode mode = GitReleaseBranchMode.Choose)
    {
        if (!CanChangeReleaseBranch || Root is not { } root || SelectedRemote is not { } remote) return false;
        var currentBranch = _snapshot!.Branch;
        var savedSource = _powerDetails?.ReleaseBranch;
        var picker = new GitReleaseBranchWindow(root, remote.Name, async pickerToken =>
        {
            // The picker renders first. Refresh local state before validating its remote read.
            await RefreshAsync(showLoading: false, cancellationToken: pickerToken, forceRefresh: true);
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
            cachedDefaultBranch: _powerDetails?.CachedRemoteDefaultBranch) { Owner = DialogOwner };
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

    private async Task ReviewCommitAllPushAsync()
    {
        if (!CanCommitAllPush) return;
        await RefreshAsync(showLoading: false, forceRefresh: false);
        if (CanFinishSync) { await ReviewAndFinishSyncAsync(); return; }
        if (!CanCommitAllPush || _snapshot is not { } snapshot || Root is not { } root || SelectedRemote is not { } remote) return;
        var destinationBranch = PublishBranchDraft.Trim();
        var agentSummaries = await ReadSharedAgentSummariesAsync(root, snapshot, remote);
        if (agentSummaries == null) return;
        var prompt = CreateCommitReview(() =>
        {
            var messageDraft = ReadScopedCommitDraft(root, snapshot.Branch, remote);
            return new GitCommitWindow(snapshot, remote, _powerDetails?.LastCreatedBranch,
                destinationBranch, messageDraft, agentSummaries: agentSummaries, freezeDestination: true) { Owner = DialogOwner };
        });
        if (prompt == null) return;
        var accepted = prompt.ShowDialog() == true;
        _commitDraft = prompt.MessageWasEdited ? prompt.Message : "";
        if (prompt.UnconfirmedCommand is { } unconfirmed)
        {
            HoldUnconfirmedBranchCommand(unconfirmed);
            return;
        }
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
            pushedRemote: remote, reviewedSnapshot: snapshot);
        CompletePublishFeedback(pushed, StatusText);
    }

    private async Task ReviewAndFinishSyncAsync()
    {
        if (!CanFinishSync || _snapshot is not { } snapshot || _powerDetails?.PendingSync is not { } pending
            || Root is not { } root || SelectedRemote is not { } remote) return;
        var destinationBranch = PublishBranchDraft.Trim();
        var agentSummaries = await ReadSharedAgentSummariesAsync(root, snapshot, remote);
        if (agentSummaries == null) return;
        var prompt = CreateCommitReview(() =>
        {
            var messageDraft = ReadScopedCommitDraft(root, snapshot.Branch, remote);
            return new GitCommitWindow(snapshot, remote, _powerDetails!.LastCreatedBranch,
                destinationBranch, messageDraft, GitCommitMode.FinishSync, $"{pending.Remote}/{pending.SourceBranch}",
                _powerDetails.RecoveryMessage, agentSummaries, freezeDestination: true) { Owner = DialogOwner };
        });
        if (prompt == null) return;
        var accepted = prompt.ShowDialog() == true;
        _syncMessageDraft = prompt.MessageWasEdited ? prompt.Message : "";
        if (prompt.UnconfirmedCommand is { } unconfirmed)
        {
            HoldUnconfirmedBranchCommand(unconfirmed);
            return;
        }
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
            pushedRemote: remote, reviewedSnapshot: snapshot);
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

    private async Task<AgentGitSummaryBatch?> ReadSharedAgentSummariesAsync(string root,
        GitRepositorySnapshot snapshot, GitRemoteInfo remote)
    {
        if (!IsIdle) return null;
        _busy = true;
        _operation = new();
        Changed();
        try
        {
            await _gitService.RefreshLedgerAsync(root, _operation.Token);
            var state = await _gitService.GetRepositoryAsync(root, remote.Name, _operation.Token);
            if (state.Failure is GitCommandExitUnconfirmedException unconfirmed) throw unconfirmed;
            if (state.IsRefreshing)
                throw new InvalidOperationException("Background Git refresh is running. Wait for it to finish before commit review.");
            if (state.Error != null || state.LedgerError != null)
                throw new InvalidOperationException(state.Error ?? state.LedgerError);
            var summaries = state.PendingSummaries;
            if (summaries == null || state.Snapshot?.Branch != snapshot.Branch
                || state.Snapshot.StateFingerprint != snapshot.StateFingerprint
                || !string.Equals(summaries.RepositoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || summaries.Branch != snapshot.Branch || summaries.RemoteName != remote.Name
                || summaries.ConnectionId != AgentGitChangeStore.ConnectionId(remote))
                throw new InvalidOperationException("Git state changed before commit review. Force refresh and review the current branch.");
            return summaries;
        }
        catch (Exception exception)
        {
            if (exception is GitCommandExitUnconfirmedException command) HoldUnconfirmedBranchCommand(command);
            ReportError(SafeError(exception));
            SetStatus(SafeError(exception));
            return null;
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            Changed();
            await ConsumeSharedUpdateAsync();
        }
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
    private void ShowSyncRecovery(GitSyncResult? operationResult = null)
    {
        if (_busy || Root is not { } root) return;
        var conflicts = (operationResult?.ConflictedPaths ?? [])
            .Concat(_powerDetails?.ConflictedPaths ?? [])
            .Concat(Changes.Where(change => change.IsConflict).Select(change => change.Path))
            .Distinct(StringComparer.Ordinal).ToArray();
        var details = string.Join("\n\n", new[] { operationResult?.Message, SyncRecoverySummary }.Where(text => !string.IsNullOrWhiteSpace(text)));
        var pending = operationResult?.PendingSync ?? _powerDetails?.PendingSync;
        new GitConflictWindow(root, conflicts, details, pending?.DestinationBranch,
            pending == null ? null : $"{pending.Remote}/{pending.SourceBranch}", pending?.SourceCommit) { Owner = DialogOwner }.ShowDialog();
    }
    private async void DismissSync_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDismissSync || Root is not { } root || _powerDetails?.PendingSync is not { } pending) return;
        if (!Confirm("Clear merge tracking", $"Clear tracking for {pending.Remote}/{pending.SourceBranch} into {pending.DestinationBranch}?\n\nConfirm that any interrupted Git command has finished and you have inspected the local and remote Git state.\n\nThis does not stop a command, undo a merge, remove a checkpoint, discard files, or verify a remote push. Existing files, branches and commits stay in place.")) return;
        await ExecuteAsync("Clear merge tracking", token => GitRepositoryService.DismissSyncAsync(root, pending.Remote, pending.AttemptId, token), useResultAsStatus: true);
    }
}
