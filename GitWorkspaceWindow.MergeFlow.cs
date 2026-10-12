using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private async void SyncRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPrepareSync || Root is not { } root || SelectedRemote is not { } remote) return;
        var branch = _snapshot!.Branch;
        var savedSource = _powerDetails?.ReleaseBranch;
        var flow = new GitReleaseBranchWindow(root, remote.Name,
            token => LoadMergeSourceBranchesAsync(root, remote, branch, token),
            savedSource ?? _powerDetails?.SuggestedReleaseBranch, branch, GitReleaseBranchMode.VerifyLocalMerge,
            suggestReleaseBranch: savedSource is null, cachedDefaultBranch: _powerDetails?.CachedRemoteDefaultBranch,
            compare: (source, token, progress) => CompareLocalMergeFlowAsync(root, remote, branch, source, token, progress),
            merge: (review, token, progress) => PrepareLocalMergeFlowAsync(root, remote, review, token, progress)) { Owner = DialogOwner };
        flow.ShowDialog();
        if (flow.UnconfirmedCommand is { } command) HoldUnconfirmedBranchCommand(command);
    }

    private void RequireMergeFlowScope(string root, GitRemoteInfo remote, string branch)
    {
        if (_unconfirmedGitCommand is { } command) throw command;
        if (!CanPrepareSync) throw new InvalidOperationException(PowerAvailabilityText);
        if (!string.Equals(Root, root, StringComparison.OrdinalIgnoreCase) || _snapshot?.Branch != branch
            || SelectedRemote is not { } currentRemote || currentRemote.Name != remote.Name
            || AgentGitChangeStore.ConnectionId(currentRemote) != AgentGitChangeStore.ConnectionId(remote))
            throw new InvalidOperationException("The local branch or active remote changed. Return to Git, review its current state, and reopen Merge Locally.");
    }

    private async Task<IReadOnlyList<GitRemoteBranchChoice>> LoadMergeSourceBranchesAsync(string root,
        GitRemoteInfo remote, string branch, CancellationToken token)
    {
        // An explicit comparison needs current local state, not the passive display cache.
        await RefreshAsync(showLoading: false, cancellationToken: token, forceRefresh: true);
        token.ThrowIfCancellationRequested();
        RequireMergeFlowScope(root, remote, branch);
        return await ExecuteMergeFlowReadAsync("Read remote release branches", async operationToken =>
            await GitRepositoryService.ListRemoteBranchesAsync(root, remote.Name, operationToken), token);
    }

    private async Task<GitLocalMergeReview> CompareLocalMergeFlowAsync(string root, GitRemoteInfo remote,
        string branch, string source, CancellationToken token, IProgress<string> progress)
    {
        progress.Report("Refreshing local Git status…");
        await RefreshAsync(showLoading: false, cancellationToken: token, forceRefresh: true);
        token.ThrowIfCancellationRequested();
        RequireMergeFlowScope(root, remote, branch);
        if (_powerDetails?.ReleaseBranch != source)
        {
            progress.Report("Saving the reviewed source branch…");
            await ExecuteMergeFlowReadAsync("Save release branch",
                operationToken => GitRepositoryService.SaveReleaseBranchAsync(root, remote.Name, source, operationToken),
                token, refresh: true);
            RequireMergeFlowScope(root, remote, branch);
            if (_powerDetails?.ReleaseBranch != source)
                throw new InvalidOperationException("The reviewed source could not be confirmed. Refresh and compare again.");
        }
        // Retain this exact snapshot through the preview and approval. Later shared updates never replace it.
        var reviewedSnapshot = _snapshot!;
        var comparison = await ExecuteMergeFlowReadAsync($"Compare {remote.Name}/{source}",
            operationToken => GitRepositoryService.CheckReleaseMergeAsync(root, remote.Name, source,
                operationToken, progress, skipUpToDateSimulation: true), token, reviewedSnapshot: reviewedSnapshot);
        if (Owner is MainWindow dashboard)
            dashboard.RecordWorkspaceGitConflictCheck(new(root, comparison.CurrentBranch, remote.Name,
                AgentGitChangeStore.ConnectionId(remote)), comparison);
        return new(reviewedSnapshot, comparison);
    }

    private async Task<T> ExecuteMergeFlowReadAsync<T>(string title, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken, bool refresh = false, GitRepositorySnapshot? reviewedSnapshot = null) where T : class
    {
        T? result = null;
        Exception? failure = null;
        var success = await ExecuteAsync(title, async token =>
        {
            try { result = await action(token); return $"{title} completed."; }
            catch (Exception exception) { failure = exception; throw; }
        }, refresh: refresh, useResultAsStatus: true, reviewedSnapshot: reviewedSnapshot,
            cancellationToken: cancellationToken,
            cancellationContext: " Canceled before a local merge. Review local status before continuing.");
        if (_unconfirmedGitCommand is { } command) throw command;
        cancellationToken.ThrowIfCancellationRequested();
        if (!success || result == null)
            throw failure ?? new InvalidOperationException(string.IsNullOrWhiteSpace(LatestErrorDetails)
                ? StatusText : LatestErrorDetails);
        if (_cacheRefreshFailed)
            throw new InvalidOperationException($"{StatusText}\n{LatestErrorDetails}");
        return result;
    }

    private async Task<GitLocalMergeOutcome> PrepareLocalMergeFlowAsync(string root, GitRemoteInfo remote,
        GitLocalMergeReview review, CancellationToken token, IProgress<string> progress)
    {
        RequireMergeFlowScope(root, remote, review.Comparison.CurrentBranch);
        _syncMessageDraft = "";
        GitSyncResult? result = null;
        var success = await ExecuteAsync($"Merge {remote.Name}/{review.Comparison.SourceBranch} locally", async operationToken =>
        {
            result = await GitRepositoryService.PrepareSyncAsync(root, remote.Name, review.Comparison.SourceBranch,
                operationToken, reviewedCheck: review.Comparison, reviewedSnapshot: review.Snapshot, progress: progress);
            return result.Message;
        }, useResultAsStatus: true, reviewedSnapshot: review.Snapshot, cancellationToken: token,
            cancellationContext: " A local checkpoint or merge may already exist. Inspect the local result before another action.");
        var conflicts = (result?.ConflictedPaths ?? []).Concat(_powerDetails?.ConflictedPaths ?? [])
            .Concat(Changes.Where(change => change.IsConflict).Select(change => change.Path)).Distinct(StringComparer.Ordinal).ToArray();
        var pending = result?.PendingSync ?? _powerDetails?.PendingSync;
        var paths = conflicts.Length == 0 ? "" : "Conflicted paths:\n" + string.Join("\n", conflicts);
        var details = SensitiveDataProtection.Redact(string.Join("\n\n", new[]
        {
            $"Repository: {root}", result?.Message, StatusText,
            HasSyncRecovery ? SyncRecoverySummary : null, paths,
            !success || _cacheRefreshFailed || _unconfirmedGitCommand != null ? LatestErrorDetails : null
        }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct()));
        return new(result, success, conflicts, details, pending, _unconfirmedGitCommand);
    }
}
