using System.Diagnostics;

namespace FullStackLauncher.CodexMonitor;

/// <summary>Controls only this launcher's watcher through its existing session-local IPC.</summary>
internal static class MonitorLifetime
{
    internal static bool IsRunning()
    {
        try
        {
            using var instance = Mutex.OpenExisting(CodexMonitorWindow.InstanceName);
            return !TryObserveStopped(instance, 0);
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        // Unknown ownership should still offer the user the exit choice.
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static Task StopAsync() => Task.Run(() =>
    {
        Mutex instance;
        try { instance = Mutex.OpenExisting(CodexMonitorWindow.InstanceName); }
        catch (WaitHandleCannotBeOpenedException) { return; }
        using (instance)
        {
            var elapsed = Stopwatch.StartNew();
            var signaled = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (TryObserveStopped(instance, 0)) return;
                // A watcher that is still opening may not have created its event yet.
                if (!signaled) signaled = CodexMonitorWindow.TrySignalCommand("exit-monitor");
                if (TryObserveStopped(instance, 100)) return;
            }
            throw new InvalidOperationException("The Codex watcher did not finish closing. Exit it from its tray menu, then retry. The Codex app and your tasks are still running.");
        }
    });

    private static bool TryObserveStopped(Mutex instance, int milliseconds)
    {
        bool acquired;
        try { acquired = instance.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) instance.ReleaseMutex();
        return acquired;
    }
}
