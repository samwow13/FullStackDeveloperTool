using System.Windows;

namespace FullStackLauncher;

public partial class App : Application
{
    private bool _reportingUnexpectedError;
    private bool _startupErrorShown;
    private Services.DashboardInstanceLease? _dashboardInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // WPF can report one deferred rendering error per item. A modal dialog here
            // pumps the dispatcher again, opening more dialogs before the first one closes.
            args.Handled = true;
            if (_reportingUnexpectedError) return;
            _reportingUnexpectedError = true;
            try
            {
                if (MainWindow is FullStackLauncher.MainWindow launcher && launcher.IsLoaded)
                    launcher.ReportUnexpectedError();
                else if (!_startupErrorShown)
                {
                    _startupErrorShown = true;
                    MessageBox.Show("The launcher could not finish opening. Close it and try again.",
                        "Full Stack Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally { _reportingUnexpectedError = false; }
        };
        base.OnStartup(e);
        if (e.Args.Contains("--service-output-drain", StringComparer.OrdinalIgnoreCase))
        {
            // A staged background helper keeps inherited service output pipes open
            // after dashboard exit. It never opens windows or reads settings.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = 1;
            try { exitCode = Task.Run(() => Services.ServiceOutputDrain.RunAsync(e.Args)).GetAwaiter().GetResult(); }
            catch (Exception) { /* Background pipe cleanup must never open an error dialog. */ }
            finally { Shutdown(exitCode); }
        }
        else if (e.Args.Contains("--agent-mcp", StringComparer.OrdinalIgnoreCase))
        {
            // This is a separate stdio bridge process. It connects to an already-running
            // dashboard and never creates a service runner or reads the settings library.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Task.Run(AgentBridge.AgentMcpServer.RunAsync).GetAwaiter().GetResult(); }
            catch (Exception)
            {
                // MCP stdio must not open a WPF error dialog or print service data.
                try { Console.Error.WriteLine("Launcher MCP adapter could not start."); }
                catch { }
            }
            finally { Shutdown(); }
        }
        else if (e.Args.Contains("--queue-owner", StringComparer.OrdinalIgnoreCase))
        {
            // A separate staged tray process owns queue recovery, dispatch, and
            // exact Stop requests. Dashboard startup never runs a prepared note.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ProjectTasks.QueueOwnerHost.Open(e.Args);
        }
        else if (e.Args.Contains("--codex-monitor", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CodexMonitor.CodexMonitorWindow.Open(e.Args);
        }
        else
        {
            // Do not create runners or load recovery data until the previous
            // dashboard has retained its service handles and actually exited.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                _dashboardInstance = await Services.DashboardInstanceLease.AcquireAsync(
                    new Services.SettingsStore().SettingsPath);
                MainWindow = new MainWindow();
                ((MainWindow)MainWindow).StartAgentBridge();
                MainWindow.Show();
                _dashboardInstance.CompleteStartup();
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                RestoreQueueOwnerAfterStartup();
            }
            catch (Exception ex)
            {
                _dashboardInstance?.Dispose();
                _dashboardInstance = null;
                MessageBox.Show("The new launcher could not take over. Running apps were not stopped.\n\n" +
                    Services.SensitiveDataProtection.Redact(ex.Message),
                    "Launcher replacement incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dashboardInstance?.Dispose();
        _dashboardInstance = null;
        base.OnExit(e);
    }

    private async void RestoreQueueOwnerAfterStartup()
    {
        try { await ProjectTasks.QueueOwnerClient.StartIfSavedWorkAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var guidance = ex is System.IO.IOException &&
                (ex.Message.StartsWith("An older queue owner", StringComparison.Ordinal) ||
                 ex.Message.StartsWith("The queue owner is running", StringComparison.Ordinal))
                ? ex.Message : "The queue owner could not start or finish recovery. Open Notes & queue to inspect its status.";
            MessageBox.Show("Queue owner compatibility check failed. Saved task data was preserved. " +
                "No new queue item will be submitted. " + guidance,
                "Queue owner unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
