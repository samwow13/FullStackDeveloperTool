using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async Task<bool> PrepareDashboardReplacementAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible).ToArray();
            // Close the innermost dialog first. Closing an owner directly bypasses
            // its owned windows' Closing events and their draft/operation guards.
            var dialog = windows.LastOrDefault(window => window != this && window != _projectTasksWindow &&
                window is not ServiceConsoleWindow && !windows.Any(child => child.Owner == window));
            if (dialog is not null)
            {
                var participant = dialog as IDashboardReplacementWindow;
                if (!IsReplacementWindowReady(dialog) || participant?.IsDashboardReplacementReady == false)
                {
                    Notice = "Waiting for the open dialog to finish before restarting Launcher…";
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                Notice = "Closing open dialogs before restarting Launcher…";
                dialog.Close();
                // Modal ShowDialog callers must unwind first so retained message
                // and note drafts reach their normal dashboard close checks.
                await Dispatcher.Yield(DispatcherPriority.Background);
                while (dialog.IsVisible && participant?.IsDashboardReplacementClosePending == true)
                    await Task.Delay(200, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // A rejected draft prompt is a real cancellation. Never repeat it
                // or close the owner to bypass it.
                if (dialog.IsVisible) return false;
                continue;
            }

            if (windows.All(IsReplacementWindowReady) && !_checkingStartupServices && !_savingProjectEdits &&
                !_addingProjectService && !_batchBusy && !_forceStopBatchBusy && !_resettingCodex && !_restartingMonitor &&
                !_runners.Values.SelectMany(project => project).Any(service => service.IsBusy || service.IsStopping))
                return true;

            Notice = "Waiting for the current operation to finish before restarting Launcher…";
            await Task.Delay(200, cancellationToken);
        }
    }

    private static bool IsReplacementWindowReady(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return window.IsEnabled && (handle == IntPtr.Zero || IsReplacementWindowEnabled(handle));
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsReplacementWindowEnabled(IntPtr window);
}
