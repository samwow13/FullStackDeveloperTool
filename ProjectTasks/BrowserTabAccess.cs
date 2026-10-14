using System.Diagnostics;
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

internal sealed record BrowserAddressObservation(string Address, string Status);

/// <summary>
/// Best-effort access to browser tabs exposed through Windows UI Automation.
/// Call from a background thread: a browser's accessibility provider can block.
/// </summary>
internal static class BrowserTabAccess
{
    // A timed-out accessibility lookup may still be inside the browser provider.
    // Keep at most one resume worker until that provider call returns.
    private static int _resumeWorkerRunning;

    /// <summary>Reads only the selected tab's title; other tab titles are not collected.</summary>
    public static BrowserTabCandidate? TryGetSelectedTab(IntPtr browserWindowHandle)
    {
        if (!TryGetWindowIdentity(browserWindowHandle, out var processId, out var threadId)) return null;
        try
        {
            BrowserTabCandidate? selected = null;
            foreach (var tab in FindBrowserChromeControls(AutomationElement.FromHandle(browserWindowHandle), ControlType.TabItem))
            {
                if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) ||
                    pattern is not SelectionItemPattern selection || !selection.Current.IsSelected) continue;
                if (selected is not null) return null;
                var title = tab.Current.Name ?? "";
                var runtimeId = tab.GetRuntimeId();
                if (runtimeId is not { Length: > 0 }) return null;
                selected = new(browserWindowHandle, title, runtimeId, true, processId, threadId);
            }
            return selected;
        }
        catch (Exception error) when (IsAutomationFailure(error)) { return null; }
    }

    /// <summary>
    /// Returns the observed address without inventing a scheme or normalizing the text.
    /// Focused address fields may contain an uncommitted edit, so their value is withheld.
    /// Call on a bounded background worker, never on a hook or dispatcher thread.
    /// </summary>
    public static BrowserAddressObservation ReadObservedAddress(IntPtr browserWindowHandle)
    {
        if (!TryGetWindowIdentity(browserWindowHandle, out _, out _)) return new("", "window_unavailable");
        try
        {
            var root = AutomationElement.FromHandle(browserWindowHandle);
            string? found = null;
            foreach (var edit in FindBrowserChromeControls(root, ControlType.Edit))
            {
                var identity = (edit.Current.Name + " " + edit.Current.AutomationId).ToLowerInvariant();
                if (!identity.Contains("address") && !identity.Contains("omnibox") &&
                    !identity.Contains("web address")) continue;
                if (edit.Current.HasKeyboardFocus) return new("", "address_field_focused");
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                    pattern is not ValuePattern value) continue;
                var text = value.Current.Value ?? "";
                if (!IsSupportedObservedAddress(text)) continue;
                if (found is not null && !string.Equals(found, text, StringComparison.Ordinal))
                    return new("", "ambiguous_address");
                found = text;
            }
            if (found is null) return new("", "address_unavailable");
            var absolute = Uri.TryCreate(found, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";
            return new(found, absolute ? "observed" : "observed_without_scheme");
        }
        catch (Exception error) when (IsAutomationFailure(error)) { return new("", "address_unavailable"); }
    }

    internal static bool IsSupportedObservedAddress(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl))
            return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return !string.IsNullOrWhiteSpace(absolute.Host) && string.IsNullOrEmpty(absolute.UserInfo);
        // The temporary scheme is only for parsing loopback syntax. It is never returned or saved.
        if (!text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("127.", StringComparison.Ordinal) && !text.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
            return false;
        return Uri.TryCreate("http://" + text, UriKind.Absolute, out var loopback) && loopback.IsLoopback &&
            string.IsNullOrEmpty(loopback.UserInfo) && !string.IsNullOrWhiteSpace(loopback.Host);
    }

    private static IEnumerable<AutomationElement> FindBrowserChromeControls(AutomationElement root, ControlType desired,
        CancellationToken cancellationToken = default)
    {
        // Do not traverse web documents or their input fields/ARIA tabs. Keep discovery bounded
        // even when a browser exposes an unexpectedly large native accessibility tree.
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        var queued = 1;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = pending.Pop();
            var type = element.Current.ControlType;
            if (type == ControlType.Document) continue;
            if (type == desired) yield return element;
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++queued > 1024) throw new InvalidOperationException("Browser chrome accessibility tree is too large.");
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Reacquires one remembered native browser tab without searching web documents.
    /// The identity callback must be safe on a background worker. Lookup is bounded;
    /// once Select begins, its provider call is drained before this method returns.
    /// </summary>
    public static async Task<bool> TryRestoreRememberedTabAsync(BrowserTabCandidate candidate,
        Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (cancellationToken.IsCancellationRequested || candidate.RuntimeId.Length == 0 ||
            !MatchesCandidate(candidate)) return false;
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lookupToken = lookupCancellation.Token;
        if (Interlocked.CompareExchange(ref _resumeWorkerRunning, 1, 0) != 0) return false;
        var deadline = TimeSpan.FromMilliseconds(500);
        var elapsed = Stopwatch.StartNew();
        var gate = new object();
        var selectionStarted = false;
        var selectionBlocked = false;
        var remembered = candidate with { RuntimeId = candidate.RuntimeId.ToArray() };
        bool CanRestore() => !lookupToken.IsCancellationRequested && elapsed.Elapsed < deadline &&
            isCurrent() && MatchesCandidate(remembered);
        Task<bool> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try
                {
                    if (!CanRestore()) return false;
                    var root = AutomationElement.FromHandle(remembered.WindowHandle);
                    AutomationElement? match = null;
                    foreach (var tab in FindBrowserChromeControls(root, ControlType.TabItem, lookupToken))
                    {
                        if (!CanRestore()) return false;
                        if (!tab.GetRuntimeId().SequenceEqual(remembered.RuntimeId)) continue;
                        if (match is not null) return false;
                        match = tab;
                    }
                    if (match is null || !CanRestore() ||
                        !match.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) ||
                        pattern is not SelectionItemPattern selection) return false;
                    if (selection.Current.IsSelected) return CanRestore();
                    if (!CanRestore()) return false;
                    lock (gate)
                    {
                        // Closing this gate and claiming mutation are atomic. A late
                        // read-only worker can never select after its caller returns.
                        if (selectionBlocked || lookupToken.IsCancellationRequested || elapsed.Elapsed >= deadline)
                            return false;
                        selectionStarted = true;
                    }
                    selection.Select();
                    return !lookupToken.IsCancellationRequested && selection.Current.IsSelected &&
                        isCurrent() && MatchesCandidate(remembered);
                }
                catch (Exception error) when (IsAutomationFailure(error) || error is OperationCanceledException)
                {
                    return false;
                }
                finally { Volatile.Write(ref _resumeWorkerRunning, 0); }
            });
        }
        catch
        {
            Volatile.Write(ref _resumeWorkerRunning, 0);
            throw;
        }
        try { return await worker.WaitAsync(deadline, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            bool drainSelection;
            lock (gate)
            {
                selectionBlocked = true;
                drainSelection = selectionStarted;
            }
            lookupCancellation.Cancel();
            // UIA cannot revoke a Select already inside the browser. Keep the caller
            // busy until it finishes rather than allowing a late tab change afterward.
            if (drainSelection) await worker.ConfigureAwait(false);
            return false;
        }
    }

    private static bool MatchesCandidate(BrowserTabCandidate candidate) =>
        TryGetWindowIdentity(candidate.WindowHandle, out var processId, out var threadId) &&
        processId == candidate.ProcessId && threadId == candidate.ThreadId;

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
