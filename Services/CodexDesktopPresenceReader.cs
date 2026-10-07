using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FullStackLauncher.Models;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>
/// Read-only desktop availability. Native CLI/app-server children do not establish
/// that the Codex desktop is open. No process or window is changed by this reader.
/// </summary>
internal static class CodexDesktopPresenceReader
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitSignaled = 0;
    private const uint WaitTimeout = 258;
    private const int InvalidParameter = 87;
    private const int InsufficientBuffer = 122;
    private const int NoPackage = 15700;

    public static CodexDesktopPresence Read()
    {
        var processes = new List<Process>();
        try
        {
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            var openWindows = ReadDesktopWindowProcesses();
            // Current Codex releases use ChatGPT.exe for their packaged desktop.
            // Package identity distinguishes it from the separate ChatGPT app.
            processes.AddRange(Process.GetProcessesByName("ChatGPT"));
            processes.AddRange(Process.GetProcessesByName("Codex"));
            var uncertain = false;
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, (uint)process.Id);
                    if (handle.IsInvalid)
                    {
                        if (Marshal.GetLastWin32Error() != InvalidParameter) uncertain = true;
                        continue;
                    }
                    var status = WaitForSingleObject(handle, 0);
                    if (status == WaitSignaled) continue;
                    if (status != WaitTimeout)
                    {
                        uncertain = true;
                        continue;
                    }
                    var executable = ReadExecutable(handle);
                    var fileName = Path.GetFileName(executable);
                    if (!fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        uncertain = true;
                        continue;
                    }
                    var package = ReadPackageName(handle);
                    var codexPackage = package?.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true;
                    if (fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) && !codexPackage &&
                        !executable.Split(Path.DirectorySeparatorChar).Any(part =>
                            part.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) &&
                        !IsUnpackagedCodexDesktop(package, executable))
                        continue;
                    if (!IsGuiExecutable(executable)) continue;
                    // Minimized windows remain visible to Windows. A lingering
                    // Electron helper without a desktop window is not an open app.
                    if (openWindows.Contains((uint)process.Id) && WaitForSingleObject(handle, 0) == WaitTimeout)
                        return CodexDesktopPresence.Running;
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and inspection.
                }
                catch (Exception exception) when (IsInspectionFailure(exception))
                {
                    uncertain = true;
                }
            }
            return uncertain ? CodexDesktopPresence.Unknown : CodexDesktopPresence.NotRunning;
        }
        catch (Exception exception) when (IsInspectionFailure(exception) || exception is InvalidOperationException)
        {
            return CodexDesktopPresence.Unknown;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static HashSet<uint> ReadDesktopWindowProcesses()
    {
        var processes = new HashSet<uint>();
        if (!EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero &&
                    GetWindowThreadProcessId(window, out var processId) != 0)
                    processes.Add(processId);
                return true;
            }, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return processes;
    }

    private static string ReadExecutable(SafeProcessHandle handle)
    {
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    private static string? ReadPackageName(SafeProcessHandle handle)
    {
        uint length = 0;
        var error = GetPackageFullName(handle, ref length, null);
        if (error == NoPackage) return null;
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var package = new StringBuilder((int)length);
        error = GetPackageFullName(handle, ref length, package);
        if (error != 0) throw new Win32Exception(error);
        return package.ToString();
    }

    private static bool IsGuiExecutable(string executable)
    {
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
        stream.Position = 0x3C;
        var headerOffset = reader.ReadInt32();
        if (headerOffset < 64 || (long)headerOffset + 94 > stream.Length) return false;
        stream.Position = headerOffset;
        if (reader.ReadUInt32() != 0x00004550) return false;
        stream.Position = headerOffset + 24;
        if (reader.ReadUInt16() is not (0x10B or 0x20B)) return false;
        stream.Position = headerOffset + 24 + 68;
        // IMAGE_SUBSYSTEM_WINDOWS_GUI; native Codex CLI uses WINDOWS_CUI.
        return reader.ReadUInt16() == 2;
    }

    private static bool IsUnpackagedCodexDesktop(string? package, string executable)
    {
        if (package is not null) return false;
        var version = FileVersionInfo.GetVersionInfo(executable);
        return version.ProductName?.Equals("Codex", StringComparison.OrdinalIgnoreCase) == true &&
            version.CompanyName?.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsInspectionFailure(Exception exception) => exception is
        Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or
        System.Security.SecurityException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException;

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags,
        StringBuilder executable, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(SafeProcessHandle handle, ref uint length, StringBuilder? package);
}
