using System.IO;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private static readonly TimeSpan DashboardGitCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DashboardGitLocalRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _dashboardGitTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _dashboardGitLifetime = new();
    private readonly Dictionary<string, DashboardGitWatch> _dashboardGitWatches = [];
    private CodexGitCheckIdleGate? _dashboardGitIdleGate;
    private DashboardGitWatch? _dashboardGitSelectedWatch;
    private DashboardGitWatch? _dashboardGitRunningWatch;
    private CancellationTokenSource? _dashboardGitRunCancellation;
    private CancellationTokenSource? _dashboardGitPollCancellation;
    private TaskCompletionSource? _dashboardGitPollCompletion;
    private Task? _dashboardGitRunTask;
    private bool _dashboardGitInitialized;
    private bool _dashboardGitPolling;
    private GitCommandExitUnconfirmedException? _dashboardGitUnconfirmedCommand;

    private sealed class DashboardGitWatch(ProjectProfile project, DashboardGitComparisonScope scope,
        string configuredFolders, DateTimeOffset now)
    {
        internal ProjectProfile Project { get; } = project;
        internal DashboardGitComparisonScope Scope { get; } = scope;
        internal string ConfiguredFolders { get; } = configuredFolders;
        internal DashboardGitComparison? Comparison { get; set; }
        internal GitMergeCheckResult? Result { get; set; }
        internal DateTimeOffset NextCheck { get; set; } = now + DashboardGitCheckInterval;
        internal DateTimeOffset NextLocalRead { get; set; } = now;
        internal bool Pending { get; set; }
        internal string Status { get; set; } = "No conflict test yet.";
    }

    public string DashboardGitComparisonLabel => _dashboardGitSelectedWatch?.Comparison is { Available: true } read
        ? $"Compared with {read.Scope.RemoteName}/{read.Scope.Branch} · last fetched"
        : "Local changes · remote comparison unavailable";
    public string DashboardGitChangedFiles => _dashboardGitSelectedWatch?.Comparison is { Available: true, Comparison: { } comparison }
        ? $"{comparison.ChangedFiles + comparison.UntrackedFiles:N0}"
        : _dashboardGitSelectedWatch?.Comparison?.Snapshot is { } snapshot ? $"{snapshot.Changes.Count:N0}" : "—";
    public string DashboardGitAddedLines => _dashboardGitSelectedWatch?.Comparison is { Available: true, Comparison: { } comparison }
        ? $"+{comparison.AddedLines:N0}" : "—";
    public string DashboardGitRemovedLines => _dashboardGitSelectedWatch?.Comparison is { Available: true, Comparison: { } comparison }
        ? $"−{comparison.DeletedLines:N0}" : "—";
    public string DashboardGitComparisonDetail
    {
        get
        {
            if (_dashboardGitSelectedWatch is not { } watch) return "Choose a repository and active connection in Git.";
            if (watch.Comparison is not { } read) return "Reading local Git comparison…";
            if (!read.Available) return read.UnavailableReason ?? read.Comparison?.UnavailableReason ?? "Remote comparison unavailable. Open Git to review setup.";
            var comparison = read.Comparison!;
            var detail = $"{read.UncommittedFiles:N0} uncommitted · {comparison.Ahead:N0} ahead / {comparison.Behind:N0} behind";
            if (comparison.UntrackedFiles > 0) detail += $" · {comparison.UntrackedFiles:N0} untracked (lines excluded)";
            if (comparison.BinaryFiles > 0) detail += $" · {comparison.BinaryFiles:N0} binary (lines excluded)";
            return detail;
        }
    }
    public bool CanRequestDashboardGitCheck => !_closeRequested && !_closing && !_closed && !IsEditing
        && _dashboardGitUnconfirmedCommand is null
        && _dashboardGitSelectedWatch is { Pending: false } watch && _dashboardGitRunningWatch != watch
        && watch.Comparison?.CanCheckConflicts == true;
    public bool HasDashboardGitConflictResult => _dashboardGitSelectedWatch?.Result is not null;
    public string DashboardGitConflictStatus => _dashboardGitUnconfirmedCommand is not null
        ? "Git process exit is unconfirmed. Checks paused until that process is confirmed stopped."
        : _dashboardGitSelectedWatch is { } watch
        ? watch.Pending || _dashboardGitRunningWatch == watch || watch.Result is not null ? watch.Status
            : watch.Comparison?.ConflictCheckUnavailableReason ?? watch.Status
        : "Conflict test unavailable until Git is configured.";
    public string DashboardGitConflictStatusColor => _dashboardGitSelectedWatch?.Result is { } result
        ? result.HasConflicts ? "#FFACA9" : "#77DFC3" : "#A9B9CF";
    public string DashboardGitConflictSchedule
    {
        get
        {
            if (_dashboardGitUnconfirmedCommand is not null) return "Checks paused until the prior Git process is confirmed stopped.";
            if (_dashboardGitSelectedWatch is not { } watch) return "Automatic check every 10 minutes when all local agents are idle.";
            var source = watch.Comparison?.ReleaseBranch is { Length: > 0 } branch ? $"{watch.Scope.RemoteName}/{branch}" : "saved release branch";
            if (_dashboardGitRunningWatch == watch) return $"Checking {source} · committed history only.";
            if (watch.Pending) return $"Queued for {source} · runs automatically when all local agents finish.";
            var remaining = watch.NextCheck - DateTimeOffset.UtcNow;
            var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            var checkedAt = watch.Result is { } result ? $"Checked {result.CheckedAt.ToLocalTime():HH:mm} · " : "";
            return $"{checkedAt}Next check in {seconds / 60}:{seconds % 60:00} · {source}.";
        }
    }

    private void InitializeDashboardGitComparison()
    {
        _dashboardGitInitialized = true;
        _dashboardGitTimer.Tick += async (_, _) => await PollDashboardGitAsync();
        Loaded += async (_, _) =>
        {
            _dashboardGitTimer.Start();
            await PollDashboardGitAsync();
        };
        Closed += (_, _) =>
        {
            _dashboardGitTimer.Stop();
            _dashboardGitRunCancellation?.Cancel();
            _dashboardGitPollCancellation?.Cancel();
            _dashboardGitLifetime.Cancel();
        };
    }

    private void SynchronizeDashboardGitScope()
    {
        if (!_dashboardGitInitialized) return;
        DashboardGitWatch? selected = null;
        if (SelectedProject is { IsArchived: false } project
            && SelectedNextCommitRepository is { Branch: { Length: > 0 } branch, RemoteName: { Length: > 0 } remote, ConnectionId: { Length: > 0 } connection } repository)
        {
            var scope = new DashboardGitComparisonScope(repository.RepositoryRoot, branch, remote, connection);
            try { selected = GetDashboardGitWatch(project, scope); }
            catch (Exception) { /* Invalid configured folders remain available for correction in Git. */ }
        }
        if (_dashboardGitSelectedWatch == selected) return;
        _dashboardGitSelectedWatch = selected;
        NotifyDashboardGitChanged();
        _ = PollDashboardGitAsync();
    }

    private DashboardGitWatch GetDashboardGitWatch(ProjectProfile project, DashboardGitComparisonScope scope)
    {
        var key = string.Join('\0', project.Id, scope.RepositoryRoot.ToUpperInvariant(), scope.Branch, scope.RemoteName, scope.ConnectionId);
        var configuredFolders = DashboardGitConfiguredFolders(project);
        if (_dashboardGitWatches.TryGetValue(key, out var watch) && ReferenceEquals(watch.Project, project)
            && watch.ConfiguredFolders == configuredFolders) return watch;
        return _dashboardGitWatches[key] = new(project, scope, configuredFolders, DateTimeOffset.UtcNow);
    }

    private string DashboardGitConfiguredFolders(ProjectProfile project) => string.Join('\0',
        BranchFolders(ProjectItems.Single(item => ReferenceEquals(item.Profile, project)))
            .Select(folder => Path.GetFullPath(folder.Directory).ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal));

    private bool IsCurrentDashboardGitWatch(DashboardGitWatch watch)
    {
        if (_closed || !_settings.Projects.Contains(watch.Project) || watch.Project.IsArchived) return false;
        try { return watch.ConfiguredFolders == DashboardGitConfiguredFolders(watch.Project); }
        catch (Exception) { return false; }
    }

    internal bool QueueDashboardGitConflictCheck(DashboardGitComparisonScope scope)
    {
        if (_closeRequested || _closing || _closed || IsEditing || SelectedProject is not { IsArchived: false } project) return false;
        DashboardGitWatch watch;
        try { watch = GetDashboardGitWatch(project, scope); }
        catch (Exception) { return false; }
        if (_dashboardGitRunningWatch == watch) return true;
        watch.Result = null;
        watch.Pending = true;
        watch.Status = "Conflict test queued; checking that all local agents are idle.";
        watch.NextCheck = DateTimeOffset.UtcNow;
        NotifyDashboardGitChanged();
        _ = PollDashboardGitAsync();
        return true;
    }

    private void DashboardGitConflictCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CanRequestDashboardGitCheck && _dashboardGitSelectedWatch is { } watch)
            QueueDashboardGitConflictCheck(watch.Scope);
    }

    private void DashboardGitConflictDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardGitSelectedWatch is { Result: { } result } watch)
            new GitMergeCheckWindow(watch.Scope.RepositoryRoot, result) { Owner = this }.ShowDialog();
    }

    private async Task PollDashboardGitAsync()
    {
        if (_dashboardGitPolling || _closeRequested || _closing || _closed || !IsLoaded) return;
        _dashboardGitPolling = true;
        using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(_dashboardGitLifetime.Token);
        _dashboardGitPollCancellation = pollCancellation;
        var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dashboardGitPollCompletion = pollCompletion;
        var pollToken = pollCancellation.Token;
        try
        {
            if (_dashboardGitUnconfirmedCommand is { } previousCommand)
            {
                if (!await Task.Run(previousCommand.IsExitConfirmed, pollToken)) return;
                _dashboardGitUnconfirmedCommand = null;
            }
            if (_closeRequested || _closing || _closed) return;
            foreach (var pair in _dashboardGitWatches.ToArray())
                if (!IsCurrentDashboardGitWatch(pair.Value))
                {
                    if (_dashboardGitRunningWatch == pair.Value) _dashboardGitRunCancellation?.Cancel();
                    _dashboardGitWatches.Remove(pair.Key);
                }
            if (_dashboardGitSelectedWatch is { } selected && IsCurrentDashboardGitWatch(selected)
                && _dashboardGitRunningWatch is null && DateTimeOffset.UtcNow >= selected.NextLocalRead)
                await ReadDashboardGitComparisonAsync(selected, pollToken);
            if (_closeRequested || _closing || _closed) return;

            var now = DateTimeOffset.UtcNow;
            var pending = _dashboardGitWatches.Values.Where(IsCurrentDashboardGitWatch)
                .Where(watch => watch.Pending || now >= watch.NextCheck).OrderBy(watch => watch.NextCheck).ToArray();
            foreach (var watch in pending)
            {
                if (!watch.Pending) watch.Result = null;
                watch.Pending = true;
            }
            if (pending.Length == 0 && _dashboardGitRunningWatch is null) return;
            _dashboardGitIdleGate ??= new(Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
            var idle = await _dashboardGitIdleGate.ReadAsync(pollToken);
            var queueBlocker = await Task.Run(ReadDashboardGitQueueBlocker, pollToken);
            if (_closeRequested || _closing || _closed) return;
            var blocked = IsEditing || _savingProjectEdits ? "Waiting for project editing to finish."
                : _settings.Projects.Any(project => _agentCoordination.HasActiveProjectWork(project.Id))
                    ? "Waiting for agents to release launcher service use and reservations."
                    : queueBlocker ?? (!idle.Ready ? idle.Status : null);
            if (_dashboardGitRunningWatch is not null)
            {
                if (blocked is not null || !IsCurrentDashboardGitWatch(_dashboardGitRunningWatch))
                {
                    _dashboardGitRunningWatch.Status = blocked ?? "Repository configuration changed; conflict test canceled.";
                    _dashboardGitRunCancellation?.Cancel();
                }
                return;
            }
            if (blocked is not null)
            {
                foreach (var watch in pending) watch.Status = blocked;
                return;
            }
            if (!_closeRequested && !_closing && !_closed && pending.FirstOrDefault() is { } next)
                _dashboardGitRunTask = RunDashboardGitConflictCheckAsync(next, idle);
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            HoldDashboardGitForUnconfirmedExit(exception);
        }
        catch (OperationCanceledException) when (pollToken.IsCancellationRequested) { }
        catch (Exception)
        {
            foreach (var watch in _dashboardGitWatches.Values.Where(watch => watch.Pending))
                watch.Status = "Reliable agent or Git status unavailable; conflict test remains queued.";
            _dashboardGitRunCancellation?.Cancel();
        }
        finally
        {
            _dashboardGitPolling = false;
            if (ReferenceEquals(_dashboardGitPollCancellation, pollCancellation)) _dashboardGitPollCancellation = null;
            pollCompletion.TrySetResult();
            NotifyDashboardGitChanged();
        }
    }

    private async Task ReadDashboardGitComparisonAsync(DashboardGitWatch watch, CancellationToken token)
    {
        watch.NextLocalRead = DateTimeOffset.UtcNow + DashboardGitLocalRefreshInterval;
        var read = await DashboardGitComparisonService.ReadAsync(watch.Scope, token);
        if (!IsCurrentDashboardGitWatch(watch)) return;
        watch.Comparison = read;
        if (watch.Result is { } result && (read.Snapshot?.HeadCommit != result.CurrentCommit
            || read.ReleaseBranch != result.SourceBranch))
        {
            watch.Result = null;
            watch.Status = "Compared branch or release source changed; previous result cleared.";
        }
        NotifyDashboardGitChanged();
    }

    private async Task RunDashboardGitConflictCheckAsync(DashboardGitWatch watch, CodexGitCheckIdleUpdate ready)
    {
        _dashboardGitRunningWatch = watch;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_dashboardGitLifetime.Token);
        _dashboardGitRunCancellation = operation;
        var interruptedByAgents = false;
        try
        {
            watch.Result = null;
            watch.Status = "Preparing Git-only conflict test…";
            NotifyDashboardGitChanged();
            if (!await IsConfiguredDashboardGitScopeAsync(watch, operation.Token))
            {
                CompleteDashboardGitAttempt(watch, "Repository, branch, or active connection changed. Open Git to review setup.");
                return;
            }
            await ReadDashboardGitComparisonAsync(watch, operation.Token);
            if (watch.Comparison is not { CanCheckConflicts: true } prepared)
            {
                CompleteDashboardGitAttempt(watch, watch.Comparison?.ConflictCheckUnavailableReason ?? "Git conflict test unavailable. Open Git to review setup.");
                return;
            }

            async Task EnsureIdleAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                bool DashboardAllowsCheck() => !_closeRequested && !_closing
                    && !IsEditing && !_savingProjectEdits && IsCurrentDashboardGitWatch(watch)
                    && !_settings.Projects.Any(project => _agentCoordination.HasActiveProjectWork(project.Id));
                var dashboardAllowsCheck = await Dispatcher.InvokeAsync(DashboardAllowsCheck, DispatcherPriority.Normal, token);
                var blocker = await Task.Run(ReadDashboardGitQueueBlocker, token);
                if (!dashboardAllowsCheck || blocker is not null || _dashboardGitIdleGate is null
                    || !await _dashboardGitIdleGate.ConfirmReadyAsync(ready, token))
                {
                    interruptedByAgents = true;
                    operation.Cancel();
                    throw new OperationCanceledException(token);
                }
                if (!await Dispatcher.InvokeAsync(DashboardAllowsCheck, DispatcherPriority.Normal, token))
                {
                    interruptedByAgents = true;
                    operation.Cancel();
                    throw new OperationCanceledException(token);
                }
            }

            var progress = new Progress<string>(message =>
            {
                if (_dashboardGitRunningWatch != watch || operation.IsCancellationRequested) return;
                watch.Status = message;
                NotifyDashboardGitChanged();
            });
            var checkedConflict = await DashboardGitComparisonService.CheckAsync(prepared, operation.Token, progress, EnsureIdleAsync);
            await EnsureIdleAsync(operation.Token);
            if (!IsCurrentDashboardGitWatch(watch)) return;
            if (checkedConflict.Result is { } result)
            {
                watch.Result = result;
                CompleteDashboardGitAttempt(watch, result.HasConflicts
                    ? $"Conflicts found against {result.Remote}/{result.SourceBranch}. Uncommitted edits excluded."
                    : $"No conflicts against {result.Remote}/{result.SourceBranch}. Uncommitted edits excluded.");
            }
            else CompleteDashboardGitAttempt(watch, checkedConflict.UnavailableReason ?? "No conflict result was confirmed.");
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            HoldDashboardGitForUnconfirmedExit(exception);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (IsCurrentDashboardGitWatch(watch))
            {
                watch.Pending = true;
                watch.Status = interruptedByAgents || !_closeRequested && !_closing && !_closed
                    ? "Conflict test deferred; waiting for all local agents to finish."
                    : "Conflict test paused while the launcher closes.";
            }
        }
        catch (Exception)
        {
            if (IsCurrentDashboardGitWatch(watch))
                CompleteDashboardGitAttempt(watch, "Conflict test unavailable. Review Git access before trying again.");
        }
        finally
        {
            if (ReferenceEquals(_dashboardGitRunCancellation, operation)) _dashboardGitRunCancellation = null;
            _dashboardGitRunningWatch = null;
            NotifyDashboardGitChanged();
        }
    }

    private async Task<bool> IsConfiguredDashboardGitScopeAsync(DashboardGitWatch watch, CancellationToken token)
    {
        if (!IsCurrentDashboardGitWatch(watch)) return false;
        var discovery = await DiscoverAgentGitAsync(watch.Project, null, token);
        if (!IsCurrentDashboardGitWatch(watch)) return false;
        EnsureAgentGitProjectCurrent(discovery);
        return discovery.Repositories.Any(repository => repository.CanRecordChanges
            && string.Equals(repository.RepositoryRoot, watch.Scope.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
            && repository.Branch == watch.Scope.Branch && repository.ActiveConnectionId == watch.Scope.ConnectionId
            && repository.Connections.Any(connection => connection.IsActive && connection.RemoteName == watch.Scope.RemoteName));
    }

    private static string? ReadDashboardGitQueueBlocker()
    {
        var store = new ProjectTaskStore();
        var data = store.Load();
        if (!store.CanSave) return "Queue activity unavailable; conflict test remains queued.";
        if (data.Receipts.Any(receipt => (receipt.State is ProjectTaskRunState.Starting or ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention)
                && receipt.FinishedAt is null && receipt.QueueReviewCompletedAt is null && receipt.ConnectionReviewCompletedAt is null
            || receipt.SubmissionStartedAt is not null && receipt.FinishedAt is null
                && receipt.QueueReviewCompletedAt is null && receipt.ConnectionReviewCompletedAt is null))
            return "Waiting for queued tasks or connection checks to finish.";
        if (!data.PauseAllQueues && data.Queues.Any(queue => queue.Enabled
            && (queue.ExternalPredecessor is not null && queue.ExternalPredecessorSatisfiedAt is null || data.QueueItems.Any(item => item.ProjectId == queue.ProjectId && item.Enabled
                && item.State is ProjectQueueItemState.Pending or ProjectQueueItemState.Starting or ProjectQueueItemState.Running))))
            return "Waiting for all enabled queue work to finish.";
        return null;
    }

    private static void CompleteDashboardGitAttempt(DashboardGitWatch watch, string status)
    {
        watch.Pending = false;
        watch.NextCheck = DateTimeOffset.UtcNow + DashboardGitCheckInterval;
        watch.Status = status;
    }

    private void HoldDashboardGitForUnconfirmedExit(GitCommandExitUnconfirmedException exception)
    {
        _dashboardGitUnconfirmedCommand = exception;
        foreach (var watch in _dashboardGitWatches.Values)
        {
            watch.Result = null;
            watch.Status = "Git process exit is unconfirmed. Checks paused until that process is confirmed stopped.";
        }
    }

    private void NotifyDashboardGitChanged()
    {
        foreach (var name in new[] { nameof(DashboardGitComparisonLabel), nameof(DashboardGitChangedFiles), nameof(DashboardGitAddedLines),
            nameof(DashboardGitRemovedLines), nameof(DashboardGitComparisonDetail), nameof(CanRequestDashboardGitCheck),
            nameof(HasDashboardGitConflictResult), nameof(DashboardGitConflictStatus), nameof(DashboardGitConflictStatusColor), nameof(DashboardGitConflictSchedule) })
            Changed(name);
    }

    private bool SuspendDashboardGitChecks()
    {
        var wasEnabled = _dashboardGitTimer.IsEnabled;
        _dashboardGitTimer.Stop();
        _dashboardGitRunCancellation?.Cancel();
        _dashboardGitPollCancellation?.Cancel();
        return wasEnabled;
    }

    private async Task StopDashboardGitComparisonAsync()
    {
        _dashboardGitRunCancellation?.Cancel();
        _dashboardGitPollCancellation?.Cancel();
        try { await Task.WhenAll(_dashboardGitRunTask ?? Task.CompletedTask, _dashboardGitPollCompletion?.Task ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        if (_dashboardGitUnconfirmedCommand is { } command && !await Task.Run(command.IsExitConfirmed))
            throw new InvalidOperationException("The started Git process exit was not confirmed; verify it has stopped before closing the launcher.");
    }
}
