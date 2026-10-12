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
        var setupInstalledAgent = args.Length == 1 && args[0] == "--setup-installed-agent";
        if (!setupInstalledAgent && args.Any(a => a != "--background"))
        { MessageBox.Show("Unknown companion launch argument.", "Remote Companion", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        using var mutex = new Mutex(true, @"Global\FullStackRemoteCompanion." + sid, out var created);
        if (!created) { MessageBox.Show("Remote Companion is already running for this Windows user. Open its existing system tray icon.", "Remote Companion"); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        { MessageBox.Show(e.Exception.Message, "Remote Companion", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
        if (setupInstalledAgent)
            app.Startup += async (_, _) => await SetupInstalledAgentAsync(app);
        else
            OpenCompanion(app, args.Contains("--background", StringComparer.Ordinal));
        return app.Run();
    }

    private static void OpenCompanion(Application app, bool background)
    {
        var window = new CompanionWindow();
        app.MainWindow = window;
        window.Initialize(background);
    }

    private static async Task SetupInstalledAgentAsync(Application app)
    {
        try
        {
            if (ServiceInstallation.IsInstalled())
                throw new InvalidOperationException("A Remote Companion background service already exists. Review that installation before installing a tray agent.");
            // This process keeps the original signed-in user's identity. Only firewall setup requests UAC.
            await ServiceInstallation.ElevateAsync("--configure-firewall");
            if (ServiceInstallation.IsInstalled())
                throw new InvalidOperationException("A Remote Companion background service appeared during setup. Review it before continuing with a tray agent.");
            ServiceInstallation.SetStartsAtLogin(true);
            if (!ServiceInstallation.StartsAtLogin())
                throw new InvalidOperationException("Agent startup setting could not be verified. Review the agent's Windows sign-in startup setting.");
            OpenCompanion(app, background: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agent setup did not complete", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(1);
        }
    }
}
