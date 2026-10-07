using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>Separates dashboard startup from an inherited Codex process job.</summary>
internal static partial class DashboardProcessLifetime
{
    private const string RelayArgument = "--dashboard-lifetime-relay";
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;

    internal readonly record struct StartupResult(bool ExitStartup);
    private enum LaunchAncestry { Codex, Other, Incomplete }

    // Run before dashboard ownership, settings loading, or service recovery.
    // No external process or job is stopped, and no job limits are changed.
    internal static StartupResult Protect(string[] arguments)
    {
        if (arguments.Contains(RelayArgument, StringComparer.OrdinalIgnoreCase))
        {
            // A failed attempt must exit its own startup without recursively
            // launching, taking dashboard ownership, or touching existing apps.
            try { return new(IsInAnyJob(GetCurrentProcess())); }
            catch (Win32Exception) { return new(true); }
        }

        try
        {
            if (!IsInAnyJob(GetCurrentProcess()) || Debugger.IsAttached) return new(false);
            var ancestry = ReadLaunchAncestry();
            // An exec shell can exit before WPF startup, removing its parent
            // row. The inherited thread marker covers that Codex launch only
            // when ancestry is incomplete; verified Explorer ancestry wins.
            if (ancestry != LaunchAncestry.Codex && !(ancestry == LaunchAncestry.Incomplete &&
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_THREAD_ID")))) return new(false);
        }
        catch (Win32Exception) { return new(false); }

        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Launcher executable could not be identified.");
            // Preserve raw Windows quoting, explicit --settings, working folder,
            // and caller environment. No command shell is used for this relay.
            var command = Environment.CommandLine + " " + RelayArgument;
            var directory = Environment.CurrentDirectory;
            var startup = new StartupInformationEx { Startup = new() { Size = Marshal.SizeOf<StartupInformation>() } };
            if (StartIndependent(executable, command, directory, 0x01000000, ref startup)) return new(true);

            // Jobs may prohibit breakaway. A verified desktop shell provides an
            // independent parent; Windows inherits its job rather than Codex's.
            using var shell = OpenVerifiedShell();
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributes = Marshal.AllocHGlobal(size);
            var parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            var initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                Marshal.WriteIntPtr(parentValue, shell.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020000, parentValue,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                startup.Startup.Size = Marshal.SizeOf<StartupInformationEx>();
                startup.AttributeList = attributes;
                if (!StartIndependent(executable, command, directory, 0x00080000, ref startup))
                    throw new InvalidOperationException("Windows did not confirm an independent launcher process.");
                return new(true);
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(parentValue);
                Marshal.FreeHGlobal(attributes);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var detail = exception is Win32Exception native ? $" Windows error {native.NativeErrorCode}." : "";
            throw new InvalidOperationException("The launcher could not separate from Codex's process job. The existing launcher and apps were left running. Open the new launcher from File Explorer before closing Codex." + detail, exception);
        }
    }

    private static bool StartIndependent(string executable, string command, string directory,
        uint flags, ref StartupInformationEx startup)
    {
        if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero,
                false, flags, IntPtr.Zero, directory, ref startup, out var created)) return false;
        using var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var thread = new SafeFileHandle(created.Thread, ownsHandle: true);
        // The relay checks the same condition before normal dashboard startup.
        // Pin and inspect the created handle, never reopen a potentially reused PID.
        return !IsInAnyJob(process.DangerousGetHandle()) && WaitForSingleObject(process, 0) == WaitTimeout;
    }

    private static bool IsInAnyJob(IntPtr process)
    {
        if (!IsProcessInJob(process, IntPtr.Zero, out var inJob)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return inJob;
    }

    private static SafeProcessHandle OpenVerifiedShell()
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0)
            throw new InvalidOperationException("The Windows desktop shell is unavailable.");
        var shell = OpenProcess(QueryLimitedInformation | Synchronize | 0x0080, false, processId);
        try
        {
            if (shell.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ProcessIdToSessionId(processId, out var shellSession) ||
                !ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (shellSession != ownSession || WaitForSingleObject(shell, 0) != WaitTimeout ||
                !ReadExecutable(shell).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase) ||
                IsInAnyJob(shell.DangerousGetHandle()) || !HasSameIdentityAndElevation(shell))
                throw new InvalidOperationException("The Windows desktop shell could not provide an independent matching process identity.");
            return shell;
        }
        catch { shell.Dispose(); throw; }
    }

    private static bool HasSameIdentityAndElevation(SafeProcessHandle shell)
    {
        if (!OpenProcessToken(shell.DangerousGetHandle(), 0x0008, out var shellToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (shellToken)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var ownToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (ownToken)
            using (var shellIdentity = new WindowsIdentity(shellToken.DangerousGetHandle()))
            using (var ownIdentity = new WindowsIdentity(ownToken.DangerousGetHandle()))
                return shellIdentity.User == ownIdentity.User && ReadElevation(shellToken) == ReadElevation(ownToken);
        }
    }

    private static int ReadElevation(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out var elevation, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return elevation;
    }

    private static LaunchAncestry ReadLaunchAncestry()
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parents = new Dictionary<uint, uint>();
        do { parents[entry.ProcessId] = entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(GetCurrentProcess(), out var childCreated, out _, out _, out _) ||
            !ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var seen = new HashSet<uint>();
        var child = (uint)Environment.ProcessId;
        while (parents.TryGetValue(child, out var parent) && parent > 4 && seen.Add(parent) && seen.Count <= 64)
        {
            using var process = OpenProcess(QueryLimitedInformation | Synchronize, false, parent);
            if (process.IsInvalid || WaitForSingleObject(process, 0) != WaitTimeout ||
                !ProcessIdToSessionId(parent, out var session) || session != ownSession ||
                !GetProcessTimes(process.DangerousGetHandle(), out var created, out _, out _, out _) || created > childCreated)
                return LaunchAncestry.Incomplete;
            var executable = ReadExecutable(process);
            var name = Path.GetFileName(executable);
            if (executable.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                return LaunchAncestry.Other;
            if (name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                uint length = 0;
                var error = GetPackageFullName(process, ref length, null);
                if (error == 122 && length is > 0 and <= 32768)
                {
                    var package = new StringBuilder((int)length);
                    if (GetPackageFullName(process, ref length, package) == 0 &&
                        package.ToString().StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) return LaunchAncestry.Codex;
                }
                var installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex") + Path.DirectorySeparatorChar;
                if (name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) && executable.StartsWith(installedRoot, StringComparison.OrdinalIgnoreCase)) return LaunchAncestry.Codex;
            }
            childCreated = created;
            child = parent;
        }
        return parents.ContainsKey(child) ? LaunchAncestry.Other : LaunchAncestry.Incomplete;
    }

    private static string ReadExecutable(SafeProcessHandle process)
    {
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }
}
