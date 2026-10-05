using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private static readonly TimeSpan DashboardGitLocalRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _dashboardGitTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _dashboardGitLifetime = new();
    private readonly Dictionary<string, DashboardGitWatch> _dashboardGitWatches = [];
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
        internal DateTimeOffset RequestedAt { get; set; }
        internal DateTimeOffset NextLocalRead { get; set; } = now;
        internal bool Pending { get; set; }
        internal string? VerifiedReleaseBranch { get; set; }
        internal string? RunningReleaseBranch { get; set; }
        internal string Status { get; set; } = "No conflict test yet.";
    }

    public string DashboardGitComparisonLabel => _dashboardGitSelectedWatch?.Comparison?.Comparison is { IsLocalComparison: true }
        ? "Local changes · since last local commit"
        : _dashboardGitSelectedWatch?.Comparison is { Available: true } read
        ? $"{read.Scope.RemoteName}/{read.Scope.Branch} · last fetched"
        : "Local changes · remote comparison unavailable";
    public string DashboardGitChangedFiles => _dashboardGitSelectedWatch?.Comparison?.Comparison is { LineTotalsAvailable: true } comparison
        ? $"{comparison.ChangedFiles + comparison.UntrackedFiles:N0}"
        : _dashboardGitSelectedWatch?.Comparison?.Snapshot is { } snapshot ? $"{snapshot.Changes.Count:N0}" : "—";
    public string DashboardGitAddedLines => _dashboardGitSelectedWatch?.Comparison?.Comparison is { LineTotalsAvailable: true } comparison
        ? $"+{comparison.AddedLines:N0}" : "—";
    public string DashboardGitRemovedLines => _dashboardGitSelectedWatch?.Comparison?.Comparison is { LineTotalsAvailable: true } comparison
        ? $"−{comparison.DeletedLines:N0}" : "—";
    public string DashboardGitLineTotalsTooltip => _dashboardGitSelectedWatch?.Comparison?.Comparison is { IsLocalComparison: true }
        ? "Tracked text compared with the last local commit (HEAD). Includes staged and unstaged edits; untracked and binary contents excluded."
        : "Tracked text compared with the last fetched remote branch. Includes local commits and edits; untracked and binary contents excluded.";
    public string DashboardGitComparisonDetail
    {
        get
        {
            if (_dashboardGitSelectedWatch is not { } watch) return "Choose a repository and active connection in Git.";
            if (watch.Comparison is not { } read) return "Reading local Git comparison…";
            if (read.Comparison is not { LineTotalsAvailable: true } comparison)
                return read.UnavailableReason ?? read.Comparison?.UnavailableReason ?? "Remote comparison unavailable. Open Git to review setup.";
            var detail = $"{read.UncommittedFiles:N0} uncommitted";
            if (!comparison.Available) detail += " · remote branch unavailable";
            else if (comparison.Ahead > 0 || comparison.Behind > 0)
                detail += $" · {comparison.Ahead:N0} ahead / {comparison.Behind:N0} behind";
            if (comparison.UntrackedFiles > 0) detail += $" · {comparison.UntrackedFiles:N0} untracked";
            if (comparison.BinaryFiles > 0) detail += $" · {comparison.BinaryFiles:N0} binary";
            return detail;
        }
    }
    public bool CanRequestDashboardGitCheck => !_closeRequested && !_closing && !_closed && !IsEditing
        && _dashboardGitUnconfirmedCommand is null
        && _dashboardGitSelectedWatch is { Pending: false } watch && _dashboardGitRunningWatch != watch
        && watch.Comparison?.CanCheckConflicts == true;
    public bool HasDashboardGitConflictResult => _dashboardGitSelectedWatch?.Result is not null;
    public bool HasDashboardGitConflictStatus => _dashboardGitUnconfirmedCommand is not null
        || _dashboardGitSelectedWatch is { RequestedAt: var requested } && requested != default;
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
            if (_dashboardGitSelectedWatch is not { } watch) return "Manual conflict tests only. Configure Git to run a test.";
            var source = watch.Comparison?.ReleaseBranch is { Length: > 0 } branch ? $"{watch.Scope.RemoteName}/{branch}" : "saved release branch";
            if (_dashboardGitRunningWatch == watch) return $"Checking {source} · committed history only.";
            if (watch.Pending) return $"Queued for {source} · committed history only.";
            var checkedAt = watch.Result is { } result ? $"Checked {result.CheckedAt.ToLocalTime():HH:mm} · " : "";
            return $"{checkedAt}{source} · manual only · uncommitted edits excluded.";
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

    internal bool QueueDashboardGitConflictCheck(DashboardGitComparisonScope scope, string? verifiedReleaseBranch = null)
    {
        if (_closeRequested || _closing || _closed || IsEditing || SelectedProject is not { IsArchived: false } project) return false;
        DashboardGitWatch watch;
        try { watch = GetDashboardGitWatch(project, scope); }
        catch (Exception) { return false; }
        if (_dashboardGitRunningWatch == watch)
            return verifiedReleaseBranch is null || watch.RunningReleaseBranch == verifiedReleaseBranch;
        watch.Result = null;
        watch.VerifiedReleaseBranch = verifiedReleaseBranch ?? (watch.Pending ? watch.VerifiedReleaseBranch : null);
        watch.Pending = true;
        watch.Status = "Conflict test requested…";
        watch.RequestedAt = DateTimeOffset.UtcNow;
        NotifyDashboardGitChanged();
        _ = PollDashboardGitAsync();
        return true;
    }

    internal void RecordWorkspaceGitConflictCheck(DashboardGitComparisonScope scope, GitMergeCheckResult result)
    {
        if (_closeRequested || _closing || _closed || IsEditing || SelectedProject is not { IsArchived: false } project
            || result.CurrentBranch != scope.Branch || result.Remote != scope.RemoteName) return;
        DashboardGitWatch watch;
        try { watch = GetDashboardGitWatch(project, scope); }
        catch (Exception) { return; }
        // A separately requested dashboard check keeps its own outcome and lifetime.
        if (_dashboardGitRunningWatch == watch || watch.Pending || !IsCurrentDashboardGitWatch(watch)) return;
        watch.Result = result;
        watch.RequestedAt = result.CheckedAt;
        CompleteDashboardGitAttempt(watch, result.HasConflicts
            ? $"Conflicts found · {result.Remote}/{result.SourceBranch}"
            : $"No conflicts · {result.Remote}/{result.SourceBranch}");
        watch.NextLocalRead = DateTimeOffset.MinValue;
        NotifyDashboardGitChanged();
    }

    private void DashboardGitConflictCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CanRequestDashboardGitCheck && _dashboardGitSelectedWatch is { } watch)
            QueueDashboardGitConflictCheck(watch.Scope);
    }

    private void DashboardGitOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.DataContext = this;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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

            var pending = _dashboardGitWatches.Values.Where(IsCurrentDashboardGitWatch)
                .Where(watch => watch.Pending).OrderBy(watch => watch.RequestedAt).ToArray();
            if (pending.Length == 0 && _dashboardGitRunningWatch is null) return;
            if (_closeRequested || _closing || _closed) return;
            var blocked = IsEditing || _savingProjectEdits ? "Waiting for project editing to finish." : null;
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
                _dashboardGitRunTask = RunDashboardGitConflictCheckAsync(next);
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            HoldDashboardGitForUnconfirmedExit(exception);
        }
        catch (OperationCanceledException) when (pollToken.IsCancellationRequested) { }
        catch (Exception)
        {
            foreach (var watch in _dashboardGitWatches.Values.Where(watch => watch.Pending))
                watch.Status = "Git status unavailable; conflict test remains queued.";
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
        var resultBeforeRead = watch.Result;
        var read = await DashboardGitComparisonService.ReadAsync(watch.Scope, token);
        if (!IsCurrentDashboardGitWatch(watch)) return;
        if (!ReferenceEquals(watch.Result, resultBeforeRead))
        {
            // A workspace comparison can finish while this older local read is waiting.
            // Read again before attaching status or invalidating the newer result.
            watch.NextLocalRead = DateTimeOffset.MinValue;
            return;
        }
        watch.Comparison = read;
        if (watch.Result is { } result && (read.Snapshot?.HeadCommit != result.CurrentCommit
            || read.ReleaseBranch != result.SourceBranch))
        {
            watch.Result = null;
            watch.Status = "Compared branch or release source changed; previous result cleared.";
        }
        NotifyDashboardGitChanged();
    }

    private async Task RunDashboardGitConflictCheckAsync(DashboardGitWatch watch)
    {
        _dashboardGitRunningWatch = watch;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_dashboardGitLifetime.Token);
        _dashboardGitRunCancellation = operation;
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
            if (watch.VerifiedReleaseBranch is { } verifiedRelease && prepared.ReleaseBranch != verifiedRelease)
            {
                CompleteDashboardGitAttempt(watch, "The verified release branch changed while the conflict test was queued. Open Git and verify the release branch again.");
                return;
            }
            watch.RunningReleaseBranch = prepared.ReleaseBranch;

            async Task EnsureCurrentContextAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                bool DashboardAllowsCheck() => !_closeRequested && !_closing
                    && !IsEditing && !_savingProjectEdits && IsCurrentDashboardGitWatch(watch);
                var dashboardAllowsCheck = await Dispatcher.InvokeAsync(DashboardAllowsCheck, DispatcherPriority.Normal, token);
                if (!dashboardAllowsCheck)
                {
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
            var checkedConflict = await DashboardGitComparisonService.CheckAsync(prepared, operation.Token, progress, EnsureCurrentContextAsync);
            await EnsureCurrentContextAsync(operation.Token);
            if (!IsCurrentDashboardGitWatch(watch)) return;
            if (checkedConflict.Result is { } result)
            {
                watch.Result = result;
                CompleteDashboardGitAttempt(watch, result.HasConflicts
                    ? $"Conflicts found · {result.Remote}/{result.SourceBranch}"
                    : $"No conflicts · {result.Remote}/{result.SourceBranch}");
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
                watch.Pending = _closeRequested || _closing;
                if (!watch.Pending) watch.VerifiedReleaseBranch = null;
                watch.Status = watch.Pending
                    ? "Conflict test paused while the launcher closes."
                    : "Conflict test canceled; run it again when ready.";
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
            watch.RunningReleaseBranch = null;
            _dashboardGitRunningWatch = null;
            NotifyDashboardGitChanged();
        }
    }

    private async Task<bool> IsConfiguredDashboardGitScopeAsync(DashboardGitWatch watch, CancellationToken token)
    {
        if (!IsCurrentDashboardGitWatch(watch)) return false;
        var discovery = await DiscoverAgentGitAsync(watch.Project, null, token, includeImmediateRepositories: true);
        if (!IsCurrentDashboardGitWatch(watch)) return false;
        EnsureAgentGitProjectCurrent(discovery);
        return discovery.Repositories.Any(repository => repository.CanRecordChanges
            && string.Equals(repository.RepositoryRoot, watch.Scope.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
            && repository.Branch == watch.Scope.Branch && repository.ActiveConnectionId == watch.Scope.ConnectionId
            && repository.Connections.Any(connection => connection.IsActive && connection.RemoteName == watch.Scope.RemoteName));
    }

    private static void CompleteDashboardGitAttempt(DashboardGitWatch watch, string status)
    {
        watch.Pending = false;
        watch.VerifiedReleaseBranch = null;
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
            nameof(DashboardGitRemovedLines), nameof(DashboardGitLineTotalsTooltip), nameof(DashboardGitComparisonDetail), nameof(CanRequestDashboardGitCheck),
            nameof(HasDashboardGitConflictResult), nameof(HasDashboardGitConflictStatus), nameof(DashboardGitConflictStatus), nameof(DashboardGitConflictStatusColor), nameof(DashboardGitConflictSchedule) })
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
