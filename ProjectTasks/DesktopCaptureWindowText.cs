using System.Runtime.InteropServices;
using System.Text;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Reads a bounded caption without waiting indefinitely on another Launcher UI thread.</summary>
internal static class DesktopCaptureWindowText
{
    internal static bool TryRead(nint window, int maximumLength, out string title)
    {
        title = "";
        if (window == 0 || maximumLength < 1 || GetWindowThreadProcessId(window, out var processId) == 0)
            return false;
        if (processId == Environment.ProcessId)
        {
            // GetWindowText sends a synchronous message for same-process windows.
            // Capture workers must not wait on a blocked or shutting-down dispatcher.
            const uint flags = 0x0001 | 0x0002 | 0x0020; // BLOCK | ABORTIFHUNG | ERRORONEXIT
            if (SendMessageTimeout(window, 0x000E, 0, 0, flags, 100, out var lengthResult) == 0 ||
                lengthResult.ToUInt64() > (ulong)maximumLength) return false; // WM_GETTEXTLENGTH
            var buffer = new StringBuilder(maximumLength + 1);
            if (SendMessageTimeoutText(window, 0x000D, (nuint)buffer.Capacity, buffer, flags, 100,
                    out var copiedResult) == 0 || copiedResult != lengthResult) return false; // WM_GETTEXT
            title = buffer.ToString();
            return title.Length == (int)copiedResult;
        }

        // For another process Windows reads its stored caption without sending WM_GETTEXT.
        var length = GetWindowTextLength(window);
        if (length > maximumLength) return false;
        var text = new StringBuilder(maximumLength + 1);
        if (GetWindowText(window, text, text.Capacity) != length) return false;
        title = text.ToString();
        return true;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nuint parameter, nint data, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutText(nint window, uint message, nuint parameter, StringBuilder data, uint flags, uint timeout, out nuint result);
}
