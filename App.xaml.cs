using System.Windows;

namespace FullStackLauncher;

public partial class App : Application
{
    private bool _reportingUnexpectedError;
    private bool _startupErrorShown;

    protected override void OnStartup(StartupEventArgs e)
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
        if (e.Args.Contains("--codex-monitor", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CodexMonitor.CodexMonitorWindow.Open(e.Args);
        }
        else
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
