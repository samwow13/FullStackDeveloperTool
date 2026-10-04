using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Reads Flutter app evidence separately from the shell, SDK and build processes.</summary>
internal static class FlutterRuntimeStatus
{
    private static readonly Regex BinaryName = new(@"^\s*set\s*\(\s*BINARY_NAME\s+(?:""(?<name>[^""\r\n]+)""|(?<name>[^\s()]+))\s*\)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    internal static bool IsNative(ServiceProfile profile) => string.Equals(profile.Kind, "Flutter", StringComparison.OrdinalIgnoreCase);
    internal static bool IsWeb(ServiceProfile profile) => string.Equals(profile.Kind, "Flutter Web", StringComparison.OrdinalIgnoreCase);

    internal sealed record NativeEvidence(IReadOnlyList<InspectedProcess> Apps, IReadOnlyList<ProcessIdentity> ReadyApps)
    {
        internal static readonly NativeEvidence Empty = new([], []);
    }

    // The caller already established service membership. Paths identify the application role,
    // never ownership. Only standard Windows runner output is recognized without app instrumentation.
    internal static NativeEvidence InspectNativeApps(string directory, IReadOnlyList<InspectedProcess> processes)
    {
        var buildDirectory = Path.Combine(directory, "build", "windows");
        var binaryName = ReadBinaryName(directory);
        var apps = new List<InspectedProcess>();
        var ready = new List<ProcessIdentity>();
        foreach (var process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.Executable) ||
                !Path.GetExtension(process.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(buildDirectory, process.Executable);
            if (Path.IsPathRooted(relative)) continue;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length is not (3 or 4) || parts.Any(part => part == "..") ||
                (parts.Length == 4 && !parts[0].Equals("x64", StringComparison.OrdinalIgnoreCase) &&
                !parts[0].Equals("arm64", StringComparison.OrdinalIgnoreCase)) ||
                !parts[^3].Equals("runner", StringComparison.OrdinalIgnoreCase) ||
                !new[] { "Debug", "Profile", "Release" }.Contains(parts[^2], StringComparer.OrdinalIgnoreCase)) continue;
            if (binaryName is not null && !parts[^1].Equals(binaryName + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(process.Executable)!, "flutter_windows.dll")) ||
                !ProcessInspector.IsSameProcess(process.Identity)) continue;
            apps.Add(process);
            if (HasVisibleAppWindow(process.Identity)) ready.Add(process.Identity);
        }
        return new(apps.ToArray(), ready.ToArray());
    }

    private static string? ReadBinaryName(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "windows", "CMakeLists.txt");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024) return null;
            using var reader = new StreamReader(stream);
            var buffer = new char[64 * 1024 + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count > 64 * 1024) return null;
            var text = new string(buffer, 0, count);
            var names = BinaryName.Matches(text).Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length == 1 && names[0].IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !names[0].Contains('$') ? names[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static bool HasVisibleAppWindow(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks) return false;
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
            GetWindowThreadProcessId(window, out var owner);
            // The generated Flutter runner shows this window after its first rendered frame.
            // Recheck identity after window discovery to reject exits and PID reuse.
            return owner == identity.Id && !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
