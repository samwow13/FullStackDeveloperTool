using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace FullStackLauncher.Services;

public sealed record InstalledWebsiteBrowser(string Id, string Name, string FileName);

/// <summary>
/// Discovers Chrome and Edge from fixed executable registrations and normal install locations.
/// Discovery reads executable metadata only; browser profiles and credentials are never read.
/// </summary>
public static class WebsiteBrowserLauncher
{
    private static readonly (string Id, string Name, string Executable, string InstallFolder)[] Browsers =
    [
        ("chrome", "Google Chrome", "chrome.exe", @"Google\Chrome\Application"),
        ("msedge", "Microsoft Edge", "msedge.exe", @"Microsoft\Edge\Application")
    ];

    public static IReadOnlyList<InstalledWebsiteBrowser> GetInstalledBrowsers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var installed = new List<InstalledWebsiteBrowser>();
        foreach (var browser in Browsers)
        {
            var executable = FindRegisteredExecutable(browser.Id, browser.Executable)
                ?? FindNormalExecutable(browser.Id, browser.Executable, browser.InstallFolder);
            if (executable is not null) installed.Add(new(browser.Id, browser.Name, executable));
        }
        return installed.OrderBy(browser => browser.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void ValidateBrowserId(string? id)
    {
        if (id is not (null or "chrome" or "msedge"))
            throw new ArgumentException("Choose Automatic, Google Chrome, or Microsoft Edge.", nameof(id));
    }

    public static InstalledWebsiteBrowser Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ValidateBrowserId(id);
        var browser = GetInstalledBrowsers().FirstOrDefault(browser => browser.Id == id);
        if (browser is not null) return browser;
        var name = id == "chrome" ? "Google Chrome" : "Microsoft Edge";
        throw new FileNotFoundException($"{name} was not found. Install it or choose another browser.");
    }

    public static void Open(string url, string? browserId)
    {
        var uri = ValidateWebsite(url);
        ValidateBrowserId(browserId);
        if (browserId is null)
        {
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        var selected = Resolve(browserId);
        var start = new ProcessStartInfo(selected.FileName) { UseShellExecute = false };
        start.ArgumentList.Add(uri.AbsoluteUri);
        using var application = Process.Start(start)
            ?? throw new InvalidOperationException($"{selected.Name} could not open.");
    }

    /// <summary>Starts the selected browser without a URL so its paired extension can open the login page once.</summary>
    public static void Start(string browserId)
    {
        var selected = Resolve(browserId);
        using var browser = Process.Start(new ProcessStartInfo(selected.FileName) { UseShellExecute = false })
            ?? throw new InvalidOperationException($"{selected.Name} could not open.");
    }

    private static Uri ValidateWebsite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Website targets must be complete http:// or https:// URLs.", nameof(url));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Website URLs cannot contain a username or password. Save the normal login page instead.", nameof(url));
        return uri;
    }

    private static string? FindRegisteredExecutable(string id, string executableName)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var registration = root.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executableName, writable: false);
                    if (registration?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string path) continue;
                    var executable = ExistingBrowserExecutable(path, id, executableName);
                    if (executable is not null) return executable;
                }
                catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { }
            }
        return null;
    }

    private static string? FindNormalExecutable(string id, string executableName, string installFolder)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432")
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                var executable = ExistingBrowserExecutable(Path.Combine(root!, installFolder, executableName), id, executableName);
                if (executable is not null) return executable;
            }
            catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { }
        }
        return null;
    }

    private static string? ExistingBrowserExecutable(string value, string id, string executableName)
    {
        try
        {
            var candidate = value.Trim();
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"') candidate = candidate[1..^1];
            candidate = Environment.ExpandEnvironmentVariables(candidate);
            if (candidate.Any(char.IsControl) || candidate.Contains('"') || !Path.IsPathFullyQualified(candidate)
                || !Path.GetFileName(candidate).Equals(executableName, StringComparison.OrdinalIgnoreCase)) return null;
            var path = Path.GetFullPath(candidate);
            var channelFolders = id == "chrome"
                ? new[] { "Chrome Beta", "Chrome Dev", "Chrome SxS" }
                : new[] { "Edge Beta", "Edge Dev", "Edge SxS" };
            if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(part => channelFolders.Contains(part, StringComparer.OrdinalIgnoreCase))) return null;
            if (!InstalledDesktopAppDiscovery.IsLocalPathWithoutReparsePoints(path, CancellationToken.None)
                || !File.Exists(path)) return null;
            return path;
        }
        catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { return null; }
    }

    private static bool IsExpectedMetadataFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
