using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _closeRequested;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closeRequested || _closing) return;
        if (_restartingMonitor)
        {
            Notice = "The Codex watcher is restarting. Close the launcher again when it finishes.";
            return;
        }
        if (_savingProjectEdits) return;

        // MessageBox runs a nested dispatcher loop. Pause passive work before any
        // confirmation, so polling and build output cannot compete with its first paint.
        // Keep this separate from _closing: the user's explicit Save choice must still work.
        _closeRequested = true;
        var resumeStatus = _timer.IsEnabled;
        var resumeConsole = _consoleTimer.IsEnabled;
        _timer.Stop();
        _consoleTimer.Stop();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!await ResolveProjectEditsForCloseAsync()) return;
            if (_projectTasksWindow?.PrepareToClose() == false) return;
            var runners = _runners.Values.SelectMany(x => x).Select(x => x.Runner).ToArray();
            if (runners.Any(x => x.HasManagedProcess) && MessageBox.Show(this,
                    "Close the launcher and stop the commands it started? Services started elsewhere will keep running.",
                    "Close launcher", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var closeWatcher = false;
            if (CodexMonitor.MonitorLifetime.IsRunning())
            {
                var choice = MessageBox.Show(this,
                    "Also close the Codex watcher and its chat progress overlay?\n\nYes: close the watcher too.\nNo: leave it running in the tray.\nCancel: keep the launcher open.\n\nYour saved watches and settings are kept. The Codex app and its tasks are unaffected.",
                    "Close Codex watcher too?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (choice == MessageBoxResult.Cancel) return;
                closeWatcher = choice == MessageBoxResult.Yes;
            }
            _closing = true;
            IsEnabled = false;
            if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = false;
            DatabasePanel.CancelPending();
            Notice = "Stopping launcher-owned commands…";
            // Paint the progress message before beginning native process cleanup.
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.WhenAll(runners.Select(x => x.StopManagedAsync()));
            if (closeWatcher)
            {
                Notice = "Closing the Codex watcher…";
                await CodexMonitor.MonitorLifetime.StopAsync();
            }
            await Task.Run(() => { foreach (var runner in runners) runner.Dispose(); });
            _closed = true;
            Close();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            if (_projectTasksWindow != null) _projectTasksWindow.IsEnabled = true;
            Notice = $"Closing could not finish: {ex.Message}";
            MessageBox.Show(this, Notice, "Close incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_closed)
            {
                _closing = false;
                _closeRequested = false;
                if (resumeStatus) _timer.Start();
                if (resumeConsole) _consoleTimer.Start();
                UpdateActions();
            }
        }
    }
}
