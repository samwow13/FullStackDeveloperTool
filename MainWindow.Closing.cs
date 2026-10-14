using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _closeRequested;
    private bool _resolvingCloseDrafts;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        await CloseDashboardAsync(automaticReplacement: false);
    }

    private async Task<bool> CloseDashboardAsync(bool automaticReplacement,
        CancellationToken cancellationToken = default,
        int? replacementProcessId = null, long? replacementProcessStartedAtUtcTicks = null)
    {
        if (_closed) return true;
        if (_closeRequested || _closing) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!automaticReplacement && _queueHandoffRetryBusy)
        {
            Notice = "Notes queue handoff recovery is in progress. Close the launcher again when it finishes.";
            return false;
        }
        if (!automaticReplacement && _resettingCodex)
        {
            Notice = "Codex reset is in progress. Close the launcher again when it finishes.";
            return false;
        }
        if (!automaticReplacement && _restartingMonitor)
        {
            Notice = "The Codex watcher is restarting. Close the launcher again when it finishes.";
            return false;
        }
        if (!automaticReplacement && (_savingProjectEdits || _addingProjectService)) return false;

        // Modal windows run a nested dispatcher loop. Pause passive work before any
        // confirmation, so polling and build output cannot compete with its first paint.
        // Keep this separate from _closing: the user's explicit Save choice must still work.
        _closeRequested = true;
        var resumeGitComparison = SuspendDashboardGitChecks();
        var resumeStatus = _timer.IsEnabled;
        var resumeConsole = _consoleTimer.IsEnabled;
        _timer.Stop();
        _consoleTimer.Stop();
        ServiceRunner[] runners = [];
        var closeFailed = false;
        var interactionSuspended = false;
        ProjectTasks.QueueOwnerClient.QueueOwnerDashboardHandoff? queueHandoff = null;
        var queueRollbackConfirmed = true;
        UpdateActions();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (automaticReplacement)
            {
                if (!await PrepareDashboardReplacementAsync(cancellationToken)) return false;
                // Do not let a new editor open while draft saves or service checks
                // yield. Its Closing guard would otherwise be skipped by owner exit.
                interactionSuspended = true;
                IsEnabled = false;
                if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = false;
            }
            _resolvingCloseDrafts = true;
            UpdateActions();
            try { if (!await ResolveProjectEditsForCloseAsync(automaticReplacement)) return false; }
            finally { _resolvingCloseDrafts = false; UpdateActions(); }
            if (_projectTasksWindow?.PrepareToClose(automaticReplacement) == false) return false;
            if (!CodexCrewPanel.PrepareReplyDraftsForClose(automaticReplacement)) return false;
            if (!PrepareStartAgentDraftsForClose(automaticReplacement)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            // Inspect idle services across every project. Busy services remain in
            // the choice without waiting for a potentially long maintenance command.
            var services = _runners.SelectMany(project => project.Value.Select(service =>
                (ProjectId: project.Key, Service: service))).ToArray();
            runners = services.Select(item => item.Service.Runner).ToArray();
            Notice = "Checking running apps before closing…";
            await Task.WhenAll(services.Where(item => !item.Service.IsBusy && !item.Service.IsStopping)
                .Select(item => item.Service.Runner.RefreshForProfileEditAsync()));
            foreach (var item in services) item.Service.Update();
            var activeServices = services.Where(item => item.Service.Runner.HasManagedProcess ||
                item.Service.Runner.Snapshot.ProcessIds.Count > 0 || item.Service.IsBusy || item.Service.IsStopping).ToArray();
            var exitChoice = ExitServicesChoice.LeaveRunning;
            if (!automaticReplacement && activeServices.Length > 0)
            {
                var names = activeServices.Select(item =>
                    $"{Projects.FirstOrDefault(project => project.Id == item.ProjectId)?.Name ?? "Project"} / {item.Service.Name}")
                    .ToArray();
                var dialog = new ExitServicesWindow(names) { Owner = this };
                dialog.ShowDialog();
                exitChoice = dialog.Choice;
                if (exitChoice == ExitServicesChoice.Cancel) return false;
            }
            var closeWatcher = false;
            if (!automaticReplacement && CodexMonitor.MonitorLifetime.IsRunning())
            {
                var choice = MessageBox.Show(this,
                    "Also close the Codex watcher and its chat progress overlay?\n\nYes: close the watcher too.\nNo: leave it running in the tray.\nCancel: keep the launcher open.\n\nYour saved watches and settings are kept. The Codex app and its tasks are unaffected.",
                    "Close Codex watcher too?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (choice == MessageBoxResult.Cancel) return false;
                closeWatcher = choice == MessageBoxResult.Yes;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!automaticReplacement) _dashboardResumeStore.Remove(DashboardResumeSection);
            _closing = true;
            IsEnabled = false;
            if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = false;
            Notice = exitChoice == ExitServicesChoice.ForceStop
                ? "Force stopping running apps…" : "Leaving apps running and closing launcher…";
            // Paint the progress message before beginning native process cleanup.
            await Dispatcher.Yield(DispatcherPriority.Background);
            // Prevent a late queue request from starting work during close cleanup.
            _queueDashboardPermit?.SetEnabled(false);
            if (automaticReplacement)
            {
                Notice = "Keeping Notes queue work for the replacement Launcher…";
                if (_projectTasksWindow != null)
                    await _projectTasksWindow.SuspendOwnerPollingForReplacementAsync(cancellationToken);
                queueHandoff = ProjectTasks.QueueOwnerClient.CreateDashboardHandoff(
                    replacementProcessId, replacementProcessStartedAtUtcTicks);
                await queueHandoff.PrepareAsync(cancellationToken);
            }
            else
            {
                Notice = "Stopping Notes queues…";
                await ProjectTasks.QueueOwnerClient.ShutdownForDashboardAsync(cancellationToken);
            }
            await StopGitConnectionWarmupAsync();
            await StopDashboardGitComparisonAsync();
            if (closeWatcher)
            {
                Notice = "Closing the Codex watcher…";
                await CodexMonitor.MonitorLifetime.StopAsync();
            }
            if (exitChoice == ExitServicesChoice.ForceStop)
            {
                // Recheck after modal choices and watcher cleanup, so processes that
                // came online during those waits remain part of force-stop coverage.
                await Task.WhenAll(services.Where(item => !item.Service.IsBusy && !item.Service.IsStopping)
                    .Select(item => item.Service.Runner.RefreshForProfileEditAsync()));
                var stopServices = services.Where(item => item.Service.Runner.HasManagedProcess ||
                    item.Service.Runner.Snapshot.ProcessIds.Count > 0 || item.Service.IsBusy || item.Service.IsStopping).ToArray();
                foreach (var projectId in stopServices.Select(item => item.ProjectId).Distinct())
                    RecordAgentBridgeProjectEvent(projectId, "force_stop_all", "requested", revokeReservations: true);
                await Task.WhenAll(stopServices.Select(item => item.Service.Runner.ForceStopAsync()));
                await Task.WhenAll(runners.Select(runner => runner.RefreshForProfileEditAsync()));
                var failures = services.Where(item => item.Service.Runner.HasManagedProcess ||
                    item.Service.Runner.Snapshot.ProcessIds.Count > 0 ||
                    (stopServices.Contains(item) && item.Service.Runner.Snapshot.State is ServiceState.Error or ServiceState.Conflict))
                    .ToArray();
                foreach (var item in stopServices)
                    RecordAgentBridgeEvent(item.Service, "stop", failures.Contains(item) ? "failed" : "completed");
                if (failures.Length > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine,
                        failures.Select(item => $"{item.Service.Name}: {item.Service.Runner.Snapshot.Detail}")));
                await StopCodexAccountUsageAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => { foreach (var runner in runners) runner.Dispose(); });
            }
            else
            {
                // Prepare every run before releasing any ownership handles. Failure
                // keeps the dashboard open with its service controls available.
                await Task.WhenAll(runners.Select(runner => runner.PrepareKeepRunningAsync()));
                // Finish fallible async cleanup while every service handle is still
                // retained. A failed transfer can then restore the original dashboard.
                await StopCodexAccountUsageAsync();
                // A timed-out replacement must retain dashboard ownership rather than
                // detaching services after the new process has already given up.
                cancellationToken.ThrowIfCancellationRequested();
                if (queueHandoff != null) await queueHandoff.TransferAsync(cancellationToken);
                await Task.Run(() =>
                {
                    // This is the commit boundary. Check cancellation and target
                    // identity before releasing the first native ownership handle.
                    cancellationToken.ThrowIfCancellationRequested();
                    using var replacementPeer = automaticReplacement
                        ? DashboardReplacementPeer.Pin(replacementProcessId!.Value, replacementProcessStartedAtUtcTicks!.Value) : null;
                    foreach (var runner in runners) runner.DisposeKeepingServicesRunning();
                });
            }
            // No awaited cleanup, cancellation or replacement validation may run
            // after native service ownership has been irreversibly released.
            _closed = true;
            CodexCrewPanel.DiscardReplyDraftsAfterClose();
            _startAgentDrafts.Clear();
            Close();
            return true;
        }
        catch (Exception ex)
        {
            closeFailed = true;
            IsEnabled = true;
            if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = true;
            Notice = ex is OperationCanceledException && automaticReplacement
                ? "Launcher replacement timed out. Existing apps remain running. Open the new launcher again."
                : $"Closing could not finish: {SensitiveDataProtection.Redact(ex.Message)}";
            if (!automaticReplacement)
                MessageBox.Show(this, Notice, "Close incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            if (!_closed)
            {
                if (queueHandoff != null)
                {
                    try { await queueHandoff.RollbackAsync(); }
                    catch (Exception ex)
                    {
                        queueRollbackConfirmed = false;
                        closeFailed = true;
                        Notice = $"Queue ownership rollback could not be confirmed. New queue dispatch remains paused; existing work was preserved. {SensitiveDataProtection.Redact(ex.Message)}";
                    }
                    if (!queueHandoff.SourceOwnershipConfirmed) queueRollbackConfirmed = false;
                    if (!queueRollbackConfirmed) SetQueueHandoffFailure(true);
                }
                if (interactionSuspended)
                {
                    IsEnabled = true;
                    if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = true;
                }
                // A failed preparation must not leave background helpers waiting
                // for an exit the user canceled, or change normal stop ownership.
                try { await Task.WhenAll(runners.Select(runner => runner.CancelKeepRunningPreparationAsync())); }
                catch (Exception ex) { closeFailed = true; Notice = $"Exit cleanup could not finish: {SensitiveDataProtection.Redact(ex.Message)}"; }
                foreach (var service in _runners.Values.SelectMany(project => project)) service.Update();
                _closing = false;
                _closeRequested = false;
                try { await RestartCodexAccountUsageAfterCanceledCloseAsync(); }
                catch (Exception ex)
                {
                    closeFailed = true;
                    Notice = $"Account usage refresh could not resume after canceled close: {SensitiveDataProtection.Redact(ex.Message)}";
                }
                _projectTasksWindow?.CancelReplacementPreparation();
                CodexCrewPanel.CancelReplyReplacementPreparation();
                PresentNextCommit();
                if (resumeStatus) _timer.Start();
                if (resumeConsole) _consoleTimer.Start();
                if (resumeGitComparison) _dashboardGitTimer.Start();
                _projectGitService.StartBackgroundRefresh();
                if (CanResumeQueueDispatch && queueRollbackConfirmed)
                {
                    try { _queueDashboardPermit?.SetEnabled(true); }
                    catch (InvalidOperationException ex) { closeFailed = true; Notice = ex.Message; }
                }
                if (!closeFailed) Notice = "Exit canceled. Apps keep running.";
                UpdateActions();
            }
        }
    }

}
