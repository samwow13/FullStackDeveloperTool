using System.ServiceProcess;
using System.Windows;

namespace FullStackRemoteCompanion;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--service") { ServiceBase.Run(new CompanionService()); return 0; }
        if (args.Length != 0 && args[0] is "--install-service" or "--configure-firewall" or "--start-service" or "--restart-service" or "--remove-service")
        {
            try { ServiceInstallation.RunHelperAsync(args).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Companion setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        if (args.Any(a => a != "--background"))
        { MessageBox.Show("Unknown companion launch argument.", "Remote Companion", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        using var mutex = new Mutex(true, @"Global\FullStackRemoteCompanion." + sid, out var created);
        if (!created) { MessageBox.Show("Remote Companion is already running for this Windows user. Open its existing system tray icon.", "Remote Companion"); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        { MessageBox.Show(e.Exception.Message, "Remote Companion", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        var window = new CompanionWindow();
        app.MainWindow = window;
        window.Initialize(args.Contains("--background", StringComparer.Ordinal));
        return app.Run();
    }
}
