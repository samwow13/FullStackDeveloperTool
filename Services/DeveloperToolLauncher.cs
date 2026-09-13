using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public sealed record DeveloperToolTarget(string FileName, bool IsWebsite, string Description);

/// <summary>
/// Opens user-configured desktop tools and websites. Developer tools are never adopted by the
/// service process supervisor: opening, focusing, or closing the launcher does not stop them.
/// pgAdmin discovery reads executable paths only; it never reads its configuration or session URL.
/// </summary>
public static class DeveloperToolLauncher
{
    private static readonly HashSet<string> CommandShells = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "wsl.exe" };

    public static DeveloperToolTarget Resolve(DeveloperTool tool, string settingsDirectory)
    {
        Validate(tool, settingsDirectory);
        if (tool.Kind.Equals("Website", StringComparison.OrdinalIgnoreCase))
        {
            var uri = ValidateWebsite(tool.Target);
            return new(uri.AbsoluteUri, true, $"Website · {uri.Host}");
        }

        var automaticPgAdmin = tool.Kind.Equals("PgAdmin", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(tool.Target);
        var executable = automaticPgAdmin
            ? FindPgAdmin() ?? throw MissingPgAdmin()
            : ResolveApplicationPath(tool.Target, settingsDirectory);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"The application for '{tool.Name}' was not found at '{executable}'. Edit this tool and choose its installed .exe file.", executable);
        return new(executable, false, automaticPgAdmin ? "pgAdmin 4 desktop application" : tool.Name.Trim());
    }

    public static void Validate(DeveloperTool tool, string settingsDirectory, bool requireExistingFile = false)
    {
        if (tool is null) throw new ArgumentException("A developer tool is required.", nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name) || ContainsControlCharacters(tool.Name))
            throw new ArgumentException("Give this tool a name without line breaks or control characters.");
        if (string.IsNullOrWhiteSpace(tool.Kind))
            throw new ArgumentException($"Choose a kind for '{tool.Name}': PgAdmin, Application, or Website.");

        if (tool.Kind.Equals("Website", StringComparison.OrdinalIgnoreCase))
        {
            ValidateWebsite(tool.Target);
            return;
        }
        var pgAdmin = tool.Kind.Equals("PgAdmin", StringComparison.OrdinalIgnoreCase);
        if (!pgAdmin && !tool.Kind.Equals("Application", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown tool kind '{tool.Kind}'. Choose PgAdmin, Application, or Website.");

        if (pgAdmin && string.IsNullOrWhiteSpace(tool.Target))
        {
            // Keep the portable built-in saveable on a machine where pgAdmin is not installed.
            // Resolve performs discovery when the user actually opens it.
            return;
        }

        var executable = ResolveApplicationPath(tool.Target, settingsDirectory);
        if (requireExistingFile && !File.Exists(executable))
            throw new ArgumentException($"Application file not found: '{executable}'. Choose an installed .exe file or correct the saved path.");
    }

    public static void Open(DeveloperToolTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsWebsite)
        {
            // Validate again because callers can construct DeveloperToolTarget directly.
            var uri = ValidateWebsite(target.FileName);
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        var executable = ResolveApplicationPath(target.FileName, AppContext.BaseDirectory);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Application file not found: '{executable}'. Edit this tool and choose its installed .exe file.", executable);
        if (TryActivateExistingWindow(executable)) return;
        using var application = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
    }

    private static Uri ValidateWebsite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsControlCharacters(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Website targets must be complete http:// or https:// URLs.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Website URLs cannot contain a username or password. Save the normal login page instead.");
        return uri;
    }

    private static string ResolveApplicationPath(string? target, string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(target) || ContainsControlCharacters(target))
            throw new ArgumentException("Choose an application's .exe file. Command lines and script files are not supported here.");
        var expanded = Environment.ExpandEnvironmentVariables(target.Trim());
        if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new ArgumentException("Application tools need a local or network .exe file path. Use Website for browser links.");
        if (Regex.IsMatch(expanded, "%[^%]+%"))
            throw new ArgumentException($"The application path contains an environment variable that is not defined on this computer: '{target}'.");
        if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || expanded.Contains('"'))
            throw new ArgumentException("The application path contains invalid characters. Choose the .exe file without quotes or command arguments.");
        if (!Path.GetExtension(expanded).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Application tools must point to an .exe file, without command arguments. Use Website for browser links.");
        if (CommandShells.Contains(Path.GetFileName(expanded)))
            throw new ArgumentException("Choose the application's executable directly. Command shells belong in a service command, not a developer-tool shortcut.");
        try
        {
            return Path.GetFullPath(expanded, Path.GetFullPath(settingsDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"The application path is invalid: {ex.Message}", ex);
        }
    }

    private static bool ContainsControlCharacters(string value) => value.Any(char.IsControl);

    private static FileNotFoundException MissingPgAdmin() => new(
        "pgAdmin 4 was not found in the running applications or its usual installation folders. Install pgAdmin 4, or edit this tool and browse to runtime\\pgAdmin4.exe in its installation folder.");

    private static string? FindPgAdmin()
    {
        foreach (var process in Process.GetProcessesByName("pgAdmin4"))
        {
            using (process)
            {
                try
                {
                    var image = process.MainModule?.FileName;
                    if (!process.HasExited && !string.IsNullOrWhiteSpace(image)
                        && Path.GetFileName(image).Equals("pgAdmin4.exe", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(image)) return image;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }

        foreach (var candidate in PgAdminCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> PgAdminCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            foreach (var candidate in StandaloneCandidates(Path.Combine(local, "Programs", "pgAdmin 4")))
                yield return candidate;
        }
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var candidate in StandaloneCandidates(Path.Combine(root!, "pgAdmin 4")))
                yield return candidate;
            foreach (var version in VersionDirectories(Path.Combine(root!, "PostgreSQL")))
                yield return Path.Combine(version, "pgAdmin 4", "runtime", "pgAdmin4.exe");
        }
    }

    private static IEnumerable<string> StandaloneCandidates(string root)
    {
        yield return Path.Combine(root, "runtime", "pgAdmin4.exe");
        foreach (var version in VersionDirectories(root))
            yield return Path.Combine(version, "runtime", "pgAdmin4.exe");
    }

    private static IReadOnlyList<string> VersionDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            // One level in known installation folders only; never scan user files or whole drives.
            return Directory.EnumerateDirectories(root)
                .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
                .Where(entry => entry.Version is not null)
                .OrderByDescending(entry => entry.Version)
                .Select(entry => entry.Path).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return []; }
    }

    private static Version? ParseVersion(string name)
    {
        name = name.TrimStart('v', 'V');
        if (!name.Contains('.')) name += ".0";
        return Version.TryParse(name, out var version) ? version : null;
    }

    private static bool TryActivateExistingWindow(string executable)
    {
        var identities = new Dictionary<int, long>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        identities[process.Id] = process.StartTime.ToUniversalTime().Ticks;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        if (identities.Count == 0) return false;

        var found = false;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (!identities.TryGetValue((int)pid, out var started)) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != started
                    || !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true;
                if (IsIconic(window)) ShowWindowAsync(window, 9);
                SetForegroundWindow(window);
                // Focus restrictions must not cause duplicate instances of an already open tool.
                found = true;
                return false;
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
            { return true; }
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
