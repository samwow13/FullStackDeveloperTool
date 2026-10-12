using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace FullStackRemoteCompanion;

internal static class ServiceInstallation
{
    public const string ServiceName = "FullStackRemoteCompanion";
    public const string PipeName = "FullStackLauncher.RemoteCompanion.Control.v1";
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FullStackLauncher Remote Companion");
    public static string InstalledExecutable => Path.Combine(InstallDirectory, "FullStackRemoteCompanion.exe");
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FullStackLauncherRemoteCompanion");
    public static string OwnerFile => Path.Combine(DataDirectory, "owner.sid");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        if (key is null) return false;
        if (!string.Equals(key.GetValue("ImagePath") as string, "\"" + InstalledExecutable + "\" --service", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(key.GetValue("ObjectName") as string, @"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different Windows service already uses the companion service name. No changes were made.");
        SafePaths.RefuseReparse(InstalledExecutable);
        return true;
    }
    public static bool StartsAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ServiceName) as string, "\"" + CurrentExecutable() + "\" --background", StringComparison.OrdinalIgnoreCase);
    }
    public static void SetStartsAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ServiceName, "\"" + CurrentExecutable() + "\" --background", RegistryValueKind.String);
        else key.DeleteValue(ServiceName, throwOnMissingValue: false);
    }
    public static string CurrentExecutable() => Environment.ProcessPath is { } path && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        ? path : throw new InvalidOperationException("Open the published companion EXE to use this action.");
    public static async Task ElevateAsync(string action)
    {
        if (action is not ("--install-service" or "--configure-firewall" or "--start-service" or "--restart-service" or "--remove-service")) throw new InvalidOperationException("Unsupported setup action.");
        var info = new ProcessStartInfo(CurrentExecutable()) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(action);
        if (action == "--install-service")
        {
            info.ArgumentList.Add("--owner-sid");
            info.ArgumentList.Add(WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable."));
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Setup could not start.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Companion setup failed. The elevated setup window shows the specific error.");
    }
    public static async Task RunHelperAsync(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("This setup action requires administrator approval.");
        switch (args[0])
        {
            case "--install-service":
                if (args.Length != 3 || args[1] != "--owner-sid") throw new InvalidOperationException("Service installation requires the approving Windows user's SID.");
                var owner = new SecurityIdentifier(args[2]);
                if (!owner.IsAccountSid() || !owner.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal)) throw new InvalidOperationException("Service owner must be an interactive Windows account SID.");
                if (IsInstalled()) throw new InvalidOperationException("Companion service is already installed. Remove it explicitly before replacing its executable.");
                // Installation requires the distributable, bundled EXE rather than a development apphost.
#pragma warning disable IL3000
                if (!string.IsNullOrEmpty(typeof(Program).Assembly.Location)) throw new InvalidOperationException("Install the service from the published single-file companion EXE.");
#pragma warning restore IL3000
                SafePaths.CreateProtectedDirectory(InstallDirectory, false);
                SafePaths.CreateProtectedDirectory(DataDirectory, false);
                SafePaths.CreateProtectedDirectory(Path.Combine(DataDirectory, "identity"), true);
                SafePaths.RefuseReparse(OwnerFile);
                if (File.Exists(OwnerFile))
                {
                    SafePaths.RequireServiceFile(OwnerFile, false);
                    var previous = File.ReadAllText(OwnerFile).Trim();
                    if (previous != owner.Value) throw new InvalidOperationException("Existing service data belongs to another Windows user. Administrator review is required before transferring ownership.");
                }
                else File.WriteAllText(OwnerFile, owner.Value);
                SafePaths.RefuseReparse(InstalledExecutable);
                if (File.Exists(InstalledExecutable)) SafePaths.RequireServiceFile(InstalledExecutable, false);
                if (!string.Equals(CurrentExecutable(), InstalledExecutable, StringComparison.OrdinalIgnoreCase)) File.Copy(CurrentExecutable(), InstalledExecutable, overwrite: true);
                SafePaths.RequireServiceFile(InstalledExecutable, false);
                await CommandAsync("sc.exe", ["create", ServiceName, "binPath=", "\"" + InstalledExecutable + "\" --service", "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "Full Stack Remote Companion"]).ConfigureAwait(false);
                await CommandAsync("sc.exe", ["description", ServiceName, "Paired private-LAN status and process reads for Full Stack Launcher."]).ConfigureAwait(false);
                await ConfigureFirewallAsync(InstalledExecutable).ConfigureAwait(false);
                await StartServiceAsync().ConfigureAwait(false);
                break;
            case "--configure-firewall":
                if (args.Length != 1) throw new InvalidOperationException("Unexpected setup arguments.");
                await ConfigureFirewallAsync(IsInstalled() ? InstalledExecutable : CurrentExecutable()).ConfigureAwait(false);
                break;
            case "--start-service":
                if (args.Length != 1 || !IsInstalled()) throw new InvalidOperationException("Companion service is not installed.");
                await StartServiceAsync().ConfigureAwait(false);
                break;
            case "--restart-service":
                if (args.Length != 1 || !IsInstalled()) throw new InvalidOperationException("Companion service is not installed.");
                using (var service = new ServiceController(ServiceName))
                {
                    if (service.Status != ServiceControllerStatus.Stopped)
                    { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                }
                await StartServiceAsync().ConfigureAwait(false);
                break;
            case "--remove-service":
                if (args.Length != 1 || !IsInstalled()) throw new InvalidOperationException("Companion service is not installed.");
                using (var service = new ServiceController(ServiceName))
                {
                    if (service.Status != ServiceControllerStatus.Stopped)
                    { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                }
                await CommandAsync("sc.exe", ["delete", ServiceName]).ConfigureAwait(false);
                // Keep protected identity data for explicit reinstall; never delete pairing identities implicitly.
                break;
            default: throw new InvalidOperationException("Unsupported setup action.");
        }
    }
    private static async Task StartServiceAsync()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20))).ConfigureAwait(false);
    }
    private static async Task ConfigureFirewallAsync(string executable)
    {
        foreach (var entry in new[] { ("Discovery", "UDP", "45873"), ("Management", "TCP", "45874") })
        {
            var name = "Full Stack Remote Companion " + entry.Item1;
            await CommandAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=" + name], allowFailure: true).ConfigureAwait(false);
            await CommandAsync("netsh.exe", ["advfirewall", "firewall", "add", "rule", "name=" + name, "dir=in", "action=allow",
                "protocol=" + entry.Item2, "localport=" + entry.Item3, "remoteip=LocalSubnet", "profile=private,domain", "program=" + executable, "enable=yes"]).ConfigureAwait(false);
        }
    }
    private static async Task CommandAsync(string executable, string[] arguments, bool allowFailure = false)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows setup command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException(executable + " failed (" + process.ExitCode + "): " + (stdout + " " + stderr).Trim());
    }
    public static string ReadOwnerSid()
    {
        SafePaths.RequireServiceDirectory(DataDirectory, false);
        SafePaths.RefuseReparse(OwnerFile);
        SafePaths.RequireServiceFile(OwnerFile, false);
        if (new FileInfo(OwnerFile).Length is < 1 or > 256) throw new InvalidDataException("Service owner identity is invalid.");
        var sid = new SecurityIdentifier(File.ReadAllText(OwnerFile).Trim());
        if (!sid.IsAccountSid() || !sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal)) throw new InvalidDataException("Service owner identity is invalid.");
        return sid.Value;
    }
}
