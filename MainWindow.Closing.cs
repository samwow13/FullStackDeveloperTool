using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        CancellationToken cancellationToken = default)
    {
        if (_closed) return true;
        if (_closeRequested || _closing) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (automaticReplacement && (!IsEnabled || _checkingStartupServices || _savingProjectEdits ||
            _addingProjectService || _batchBusy || _forceStopBatchBusy ||
            HasOpenReplacementDialog() ||
            _runners.Values.SelectMany(project => project).Any(service => service.IsBusy || service.IsStopping)))
        {
            Notice = "Finish the open dialog or service operation, then open the new launcher again. Apps remain running.";
            return false;
        }
        if (_resettingCodex)
        {
            Notice = "Codex reset is in progress. Close the launcher again when it finishes.";
            return false;
        }
        if (_restartingMonitor)
        {
            Notice = "The Codex watcher is restarting. Close the launcher again when it finishes.";
            return false;
        }
        if (_savingProjectEdits || _addingProjectService) return false;

        // Modal windows run a nested dispatcher loop. Pause passive work before any
        // confirmation, so polling and build output cannot compete with its first paint.
        // Keep this separate from _closing: the user's explicit Save choice must still work.
        _closeRequested = true;
        SuspendNextCommitReminder();
        var resumeGitComparison = SuspendDashboardGitChecks();
        var resumeStatus = _timer.IsEnabled;
        var resumeConsole = _consoleTimer.IsEnabled;
        _timer.Stop();
        _consoleTimer.Stop();
        ServiceRunner[] runners = [];
        var closeFailed = false;
        UpdateActions();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            _resolvingCloseDrafts = true;
            UpdateActions();
            try { if (!await ResolveProjectEditsForCloseAsync()) return false; }
            finally { _resolvingCloseDrafts = false; UpdateActions(); }
            if (_projectTasksWindow?.PrepareToClose() == false) return false;
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
            _closing = true;
            IsEnabled = false;
            if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = false;
            Notice = exitChoice == ExitServicesChoice.ForceStop
                ? "Force stopping running apps…" : "Leaving apps running and closing launcher…";
            // Paint the progress message before beginning native process cleanup.
            await Dispatcher.Yield(DispatcherPriority.Background);
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
                await Task.Run(() => { foreach (var runner in runners) runner.Dispose(); });
            }
            else
            {
                // Prepare every run before releasing any ownership handles. Failure
                // keeps the dashboard open with its service controls available.
                await Task.WhenAll(runners.Select(runner => runner.PrepareKeepRunningAsync()));
                // A timed-out replacement must retain dashboard ownership rather than
                // detaching services after the new process has already given up.
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => { foreach (var runner in runners) runner.DisposeKeepingServicesRunning(); });
            }
            _closed = true;
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
                // A failed preparation must not leave background helpers waiting
                // for an exit the user canceled, or change normal stop ownership.
                try { await Task.WhenAll(runners.Select(runner => runner.CancelKeepRunningPreparationAsync())); }
                catch (Exception ex) { closeFailed = true; Notice = $"Exit cleanup could not finish: {SensitiveDataProtection.Redact(ex.Message)}"; }
                foreach (var service in _runners.Values.SelectMany(project => project)) service.Update();
                _closing = false;
                _closeRequested = false;
                PresentNextCommit();
                if (resumeStatus) _timer.Start();
                if (resumeConsole) _consoleTimer.Start();
                if (resumeGitComparison) _dashboardGitTimer.Start();
                if (!closeFailed) Notice = "Exit canceled. Apps keep running.";
                UpdateActions();
            }
        }
    }

    private bool HasOpenReplacementDialog()
    {
        var windows = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible).ToArray();
        // Note editors can belong to the notes host rather than this window.
        // Include all visible windows, plus native modal dialogs that disable an owner.
        return windows.Any(window => window != this && window != _projectTasksWindow &&
                window is not ServiceConsoleWindow) || windows.Any(window =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                return handle != IntPtr.Zero && !IsReplacementWindowEnabled(handle);
            });
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsReplacementWindowEnabled(IntPtr window);
}
