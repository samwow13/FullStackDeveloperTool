using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace FullStackLauncher.Services;

public static class ChromeLinkLauncher
{
    public static void Open(string url)
    {
        var clean = PostPushLink.Validate(url);
        if (clean.Length == 0) return;
        var openWindow = FindOpenWindow();
        var executable = openWindow ?? FindInstalledChrome()
            ?? throw new InvalidOperationException("Google Chrome was not found. Install Chrome, then use Open in Chrome to open the saved link.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        // A URL without --new-window is routed to Chrome's existing profile session. Background-only
        // Chrome processes do not count as an open browser window.
        if (openWindow == null) start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(clean);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Chrome could not start. Use Open in Chrome to try opening the link again.");
    }

    private static string? FindOpenWindow()
    {
        string? executable = null;
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != session || !process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return true;
                var path = process.MainModule?.FileName;
                if (path != null && Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    executable = path;
                    return false;
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            return true;
        }, IntPtr.Zero);
        return executable;
    }

    private static string? FindInstalledChrome()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                var path = (key?.GetValue(null) as string)?.Trim('"');
                if (path != null && Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var path = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
