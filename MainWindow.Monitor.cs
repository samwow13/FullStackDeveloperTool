using System.Diagnostics;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _restartingMonitor;

    private async Task RestartExistingMonitorAsync()
    {
        if (!MonitorLifetime.IsRunning()) return;
        _restartingMonitor = true;
        Notice = "Restarting the Codex watcher with this launcher version…";
        try
        {
            // Finish staging the updated build before asking the old watcher to exit.
            // No project argument: restore its independent saved watches and overlay.
            var arguments = new List<string> { "--codex-monitor", "--background" };
            AddQueueStoreSettingsArgument(arguments);
            var start = await Task.Run(() => MonitorRuntime.CreateStart(arguments.ToArray()));
            await MonitorLifetime.StopAsync();
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The replacement watcher could not start.");
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (process.HasExited)
                    throw new InvalidOperationException("The replacement watcher exited while opening. Open Codex Watcher again.");
                if (MonitorLifetime.IsRunning() && CodexMonitorWindow.TrySignalCommand("resume"))
                {
                    Notice = "Codex watcher restarted with the updated app. Saved watches, sound preference and overlay settings were retained.";
                    return;
                }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The watcher has not confirmed startup yet. Check its tray icon or open Codex Watcher.");
        }
        catch (Exception ex)
        {
            // A watcher problem must not stop the dashboard from opening.
            Notice = $"The Codex watcher could not be restarted: {ex.Message}";
        }
        finally { _restartingMonitor = false; }
    }
}
