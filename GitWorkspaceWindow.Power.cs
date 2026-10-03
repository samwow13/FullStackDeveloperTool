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
        ? $"Release source: {remote.Name}/{source}" : "Release source: choose a remote branch the first time.";
    public string PowerAvailabilityText => _powerReadError ?? (!IsRepository ? "Select or initialize a repository first."
        : !HasRemote ? "Connect a remote in Config first."
        : HasSyncRecovery ? "A local merge is tracked below. Use Commit all & push when it is ready."
        : _snapshot!.IsDetached ? "Switch to a local branch first."
        : _snapshot.IsUnborn ? "Create the first local commit before merging an existing release branch."
        : _snapshot.OperationState != null ? "Finish the active Git operation first."
        : "Merge the selected release branch into your current local branch. This action does not create a branch or push. Use Commit all & push separately when ready.");
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
        catch (Exception ex)
        {
            _powerReadError = "Git workflow settings could not be read. Refresh before committing or merging a release branch. See Activity for details.";
            AddActivity("Read Git workflow settings", root, SafeError(ex));
        }
        Changed();
    }

    private async Task<bool> ChooseReleaseBranchAsync()
    {
        if (!CanChangeReleaseBranch || Root is not { } root || SelectedRemote is not { } remote) return false;
        IReadOnlyList<GitRemoteBranchChoice>? branches = null;
        if (!await ExecuteAsync("Read remote release branches", async token =>
        {
            branches = await GitRepositoryService.ListRemoteBranchesAsync(root, remote.Name, token);
            return $"Read {branches.Count:N0} branch(es) from {remote.Name}. No files were merged or pushed.";
        }, refresh: false, useResultAsStatus: true) || branches == null) return false;
        if (branches.Count == 0)
        {
            SetStatus("This remote has no branches yet. Publish an initial branch before choosing a release source.");
            return false;
        }
        var picker = new GitReleaseBranchWindow(root, remote.Name, remote.FetchUrl, branches, _powerDetails?.ReleaseBranch) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedBranch is not { } selected) return false;
        return await ExecuteAsync("Save release branch", token => GitRepositoryService.SaveReleaseBranchAsync(root, remote.Name, selected, token), useResultAsStatus: true)
            && _powerDetails?.ReleaseBranch == selected;
    }

    private async void ChangeReleaseBranch_Click(object sender, RoutedEventArgs e) => await ChooseReleaseBranchAsync();

    private async void CheckReleaseMerge_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCheckReleaseMerge) return;
        await RefreshAsync();
        if (!CanCheckReleaseMerge) return;
        if (string.IsNullOrEmpty(_powerDetails?.ReleaseBranch) && !await ChooseReleaseBranchAsync()) return;
        if (!CanCheckReleaseMerge || Root is not { } root || SelectedRemote is not { } remote
            || _powerDetails?.ReleaseBranch is not { } source) return;
        var scope = new DashboardGitComparisonScope(root, _snapshot!.Branch, remote.Name, AgentGitChangeStore.ConnectionId(remote));
        if (Owner is MainWindow dashboard && dashboard.QueueDashboardGitConflictCheck(scope))
            SetStatus($"Git-only conflict test queued against {remote.Name}/{source}. It runs when all local agents finish. View its result and Details in Next commit. Uncommitted edits are excluded.");
        else SetStatus("Open the Git workspace from the launcher dashboard to queue a conflict test after all local agents finish.");
    }

    private async void SyncRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPrepareSync) return;
        await RefreshAsync();
        if (!CanPrepareSync) return;
        if (string.IsNullOrEmpty(_powerDetails?.ReleaseBranch) && !await ChooseReleaseBranchAsync()) return;
        if (!CanPrepareSync || _snapshot is not { } snapshot || Root is not { } root || SelectedRemote is not { } remote
            || _powerDetails?.ReleaseBranch is not { } source) return;
        var prompt = new GitCommitWindow(snapshot, remote, null,
            mode: GitCommitMode.PrepareSync, sourceLabel: $"{remote.Name}/{source}") { Owner = this };
        var accepted = prompt.ShowDialog() == true;
        if (!accepted) return;
        _syncMessageDraft = "";
        GitSyncResult? result = null;
        var prepared = await ExecuteAsync("Fetch and merge release branch", async token =>
        {
            result = await GitRepositoryService.PrepareSyncAsync(root, remote.Name, source, token);
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
            AddActivity("Prepare commit review", Root ?? "", detail);
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
