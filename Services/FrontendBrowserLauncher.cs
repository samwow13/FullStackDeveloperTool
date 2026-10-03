using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Services;

/// <summary>
/// Opens only after a complete tab check, or after verifying that no browser windows exist.
/// Address-bar inspection reads active tabs without selecting tabs or changing focus.
/// </summary>
internal static class FrontendBrowserLauncher
{
    internal static async Task<BrowserSiteOpenResult> EnsureOpenAsync(string url,
        Func<CancellationToken, Task<bool>> stillReady, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || !target.IsLoopback ||
            target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
            return Unavailable("The frontend address could not be verified. Automatic opening was skipped.");

        cancellationToken.ThrowIfCancellationRequested();
        if (!await stillReady(cancellationToken)) throw new OperationCanceledException(cancellationToken);
        var inventory = await Task.Run(ReadBrowserWindows, cancellationToken);
        // A positive active-tab match is sufficient even when background tabs are unavailable.
        try
        {
            var alreadyOpen = await Task.Run(() => inventory.Windows.Any(window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return IsSameSite(BrowserTabAccess.TryGetAddressUrl(window), target);
            }), cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            if (alreadyOpen)
                return new(BrowserSiteOpenState.AlreadyOpen, "The frontend is already open in the browser.");
        }
        catch (TimeoutException) { /* The paired extension can still check background tabs. */ }

        if (!inventory.Complete)
            return Unavailable("An existing browser could not be checked. Automatic opening was skipped.");

        if (inventory.Counts.Values.Sum() == 0)
        {
            // Refresh immediately before opening: another browser may have appeared during the check.
            var current = await Task.Run(ReadBrowserWindows, cancellationToken);
            if (!current.Complete || current.Counts.Values.Sum() != 0)
                return Unavailable("Browser windows changed during checking. Automatic opening was skipped.");
            if (!await stillReady(cancellationToken)) throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Shell URL activation can succeed without returning a process handle.
                using var process = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            }, cancellationToken);
            return new(BrowserSiteOpenState.Opened, "Opened the frontend in the default browser.");
        }

        return await BrowserPageCaptureBroker.EnsureSiteOpenAsync(target.AbsoluteUri, inventory.Counts,
            cancellationToken, async token =>
            {
                if (!await stillReady(token)) return null;
                var current = await Task.Run(ReadBrowserWindows, token);
                return current.Complete ? current.Counts : null;
            });
    }

    private static bool IsSameSite(string? address, Uri target) =>
        Uri.TryCreate(address, UriKind.Absolute, out var tab) && tab.IsLoopback &&
        tab.Scheme == target.Scheme && tab.Port == target.Port && string.IsNullOrEmpty(tab.UserInfo);

    private static BrowserSiteOpenResult Unavailable(string status) => new(BrowserSiteOpenState.Unavailable, status);

    private sealed record BrowserWindows(IReadOnlyDictionary<string, int> Counts, List<IntPtr> Windows, bool Complete);

    private static BrowserWindows ReadBrowserWindows()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["chrome"] = 0, ["msedge"] = 0 };
        var windows = new List<IntPtr>();
        var complete = true;
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        if (!EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var className = new StringBuilder(256);
            if (GetClassName(window, className, className.Capacity) == 0) return true;
            var name = className.ToString();
            if (name is not ("Chrome_WidgetWin_1" or "MozillaWindowClass" or "IEFrame")) return true;
            GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != session) return true;
                var browser = process.ProcessName.ToLowerInvariant();
                if (browser is "chrome" or "msedge")
                {
                    counts[browser]++;
                    windows.Add(window);
                }
                else if (name is "MozillaWindowClass" or "IEFrame" || !IsKnownDesktopApp(browser))
                    complete = false;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // An inaccessible browser-shaped window prevents claiming that all tabs are absent.
                complete = false;
            }
            return true;
        }, IntPtr.Zero)) complete = false;
        return new(counts, windows, complete);
    }

    // Electron desktop apps share Chromium's window class. Every other unknown
    // browser-shaped window is conservatively unavailable, including alternative browsers.
    private static bool IsKnownDesktopApp(string processName) => processName is
        "codex" or "chatgpt" or "code" or "code-insiders" or "slack" or "discord" or
        "teams" or "ms-teams" or "notion" or "obsidian" or "whatsapp" or "signal" or
        "spotify" or "postman" or "insomnia" or "githubdesktop" or "figma" or "msedgewebview2";

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximum);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
