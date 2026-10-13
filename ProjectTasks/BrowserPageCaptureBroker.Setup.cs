namespace FullStackLauncher.ProjectTasks;

/// <summary>A value-free snapshot of authenticated extension connections, not website access.</summary>
internal sealed record BrowserCaptureSetupStatus(
    bool IsConnected, int AuthenticatedProfiles, string? BrowserId, string Status);

internal static partial class BrowserPageCaptureBroker
{
    /// <summary>Reads existing authenticated connections without starting a listener or inspecting browser tabs.</summary>
    internal static BrowserCaptureSetupStatus GetBrowserSetupStatus()
    {
        lock (Gate)
        {
            var connected = Connections.Where(connection => connection.IsOpen).ToArray();
            if (connected.Length == 1)
            {
                var browserName = connected[0].Browser == "chrome" ? "Chrome" : "Edge";
                return new(true, 1, connected[0].Browser, $"{browserName} paired with Launcher.");
            }
            if (connected.Length > 0)
                return new(true, connected.Length, null, $"{connected.Length} browser profiles paired with Launcher.");
            return new(false, 0, null, _startupError == null
                ? "Waiting for pairing. Complete the two steps below in your chosen browser profile."
                : "Local browser connection could not start. Restart Launcher normally, then reopen this setup.");
        }
    }
}
