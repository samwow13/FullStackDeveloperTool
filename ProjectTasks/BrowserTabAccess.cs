using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace FullStackLauncher.ProjectTasks;

/// <summary>A browser tab discovered inside one Chrome or Edge top-level window.</summary>
internal sealed record BrowserTabCandidate(
    IntPtr WindowHandle,
    string Title,
    int[] RuntimeId,
    bool IsSelected,
    uint ProcessId,
    uint ThreadId);

/// <summary>
/// Best-effort access to browser tabs exposed through Windows UI Automation.
/// Call from a background thread: a browser's accessibility provider can block.
/// </summary>
internal static class BrowserTabAccess
{
    /// <summary>Reads the active browser address from UI Automation without changing focus or clipboard.</summary>
    public static string? TryGetAddressUrl(IntPtr browserWindowHandle)
    {
        if (!TryGetWindowIdentity(browserWindowHandle, out _, out _)) return null;
        try
        {
            var root = AutomationElement.FromHandle(browserWindowHandle);
            var edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            string? found = null;
            foreach (AutomationElement edit in edits)
            {
                try
                {
                    if (IsInsideWebDocument(edit)) continue;
                    var identity = (edit.Current.Name + " " + edit.Current.AutomationId).ToLowerInvariant();
                    if (!identity.Contains("address") && !identity.Contains("omnibox") &&
                        !identity.Contains("web address")) continue;
                    if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                        pattern is not ValuePattern value) continue;
                    var text = value.Current.Value?.Trim();
                    if (Uri.TryCreate(text, UriKind.Absolute, out var url) &&
                        url.Scheme is "http" or "https")
                    {
                        if (found != null && !string.Equals(found, url.AbsoluteUri, StringComparison.Ordinal))
                            return null;
                        found = url.AbsoluteUri;
                    }
                }
                catch (Exception error) when (IsAutomationFailure(error))
                {
                    // A stale address element must not block other candidates.
                }
            }
            return found;
        }
        catch (Exception error) when (IsAutomationFailure(error)) { }
        return null;
    }

    public static IReadOnlyList<BrowserTabCandidate> ListTabs(IntPtr browserWindowHandle)
    {
        if (!TryGetWindowIdentity(browserWindowHandle, out var processId, out var threadId))
            return [];

        try
        {
            var root = AutomationElement.FromHandle(browserWindowHandle);
            var tabs = FindBrowserTabs(root);
            var result = new List<BrowserTabCandidate>(tabs.Count);
            foreach (AutomationElement tab in tabs)
            {
                try
                {
                    if (IsInsideWebDocument(tab)) continue;
                    var runtimeId = tab.GetRuntimeId();
                    var title = tab.Current.Name?.Trim();
                    if (runtimeId is not { Length: > 0 } || string.IsNullOrWhiteSpace(title)) continue;
                    var selected = tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                        pattern is SelectionItemPattern selection && selection.Current.IsSelected;
                    result.Add(new BrowserTabCandidate(browserWindowHandle, title, runtimeId,
                        selected, processId, threadId));
                }
                catch (Exception error) when (IsAutomationFailure(error))
                {
                    // One closing or inaccessible tab must not hide other tabs.
                }
            }
            return result;
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            return [];
        }
    }

    public static bool TryActivate(BrowserTabCandidate candidate)
    {
        if (candidate.RuntimeId.Length == 0 ||
            !TryGetWindowIdentity(candidate.WindowHandle, out var processId, out var threadId) ||
            processId != candidate.ProcessId || threadId != candidate.ThreadId)
            return false;

        try
        {
            // Reacquire under the exact window. A retained AutomationElement may refer
            // to a closed tab or to an element moved to another browser window.
            var root = AutomationElement.FromHandle(candidate.WindowHandle);
            AutomationElement? match = null;
            foreach (AutomationElement tab in FindBrowserTabs(root))
            {
                try
                {
                    if (IsInsideWebDocument(tab) || !tab.GetRuntimeId().SequenceEqual(candidate.RuntimeId))
                        continue;
                    if (match != null) return false; // An ambiguous runtime ID is unsafe.
                    match = tab;
                }
                catch (Exception error) when (IsAutomationFailure(error))
                {
                    // A closing tab cannot be the selected target.
                }
            }
            if (match == null ||
                !match.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) ||
                pattern is not SelectionItemPattern selection)
                return false;

            selection.Select();
            return selection.Current.IsSelected;
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            return false;
        }
    }

    public static bool IsSelected(BrowserTabCandidate candidate)
    {
        if (candidate.RuntimeId.Length == 0 ||
            !TryGetWindowIdentity(candidate.WindowHandle, out var processId, out var threadId) ||
            processId != candidate.ProcessId || threadId != candidate.ThreadId)
            return false;

        try
        {
            var root = AutomationElement.FromHandle(candidate.WindowHandle);
            var matches = 0;
            var selected = false;
            foreach (AutomationElement tab in FindBrowserTabs(root))
            {
                if (IsInsideWebDocument(tab) || !tab.GetRuntimeId().SequenceEqual(candidate.RuntimeId))
                    continue;
                matches++;
                if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) ||
                    pattern is not SelectionItemPattern selection)
                    return false;
                selected = selection.Current.IsSelected;
            }
            return matches == 1 && selected;
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            return false;
        }
    }

    private static AutomationElementCollection FindBrowserTabs(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

    private static bool IsInsideWebDocument(AutomationElement tab)
    {
        // Web pages can contain ARIA tabs. Only browser chrome tabs belong here.
        var walker = TreeWalker.ControlViewWalker;
        var ancestor = walker.GetParent(tab);
        for (var depth = 0; ancestor != null && depth < 256; depth++)
        {
            if (ancestor.Current.ControlType == ControlType.Document) return true;
            ancestor = walker.GetParent(ancestor);
        }
        return false;
    }

    private static bool TryGetWindowIdentity(IntPtr handle, out uint processId, out uint threadId)
    {
        processId = 0;
        threadId = 0;
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        threadId = GetWindowThreadProcessId(handle, out processId);
        return threadId != 0 && processId != 0;
    }

    private static bool IsAutomationFailure(Exception error) => error is
        ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or
        ArgumentException or COMException or UnauthorizedAccessException or NotSupportedException;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
