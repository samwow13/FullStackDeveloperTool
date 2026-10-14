using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FullStackLauncher.ProjectTasks;

internal sealed record DesktopCaptureTarget(nint Handle, uint ProcessId, uint ThreadId);

/// <summary>One bounded best-effort metadata read for the window under the initial drag point.</summary>
internal static class DesktopCaptureMetadataReader
{
    // An abandoned UIA call may remain blocked inside another process. Never accumulate workers.
    private static int _workerRunning;

    public static ReadOperation BeginRead(DesktopCaptureTarget target)
    {
        var operation = new ReadOperation();
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0)
        {
            operation.Worker = Task.FromResult(new CodexAgentCaptureContext
                { MetadataStatus = "metadata_reader_busy", AddressStatus = "not_read" });
            operation.Ready.TrySetResult();
            return operation;
        }
        try
        {
            operation.Worker = Task.Run(() =>
            {
                try { return Read(target, operation); }
                catch (Exception) { return new() { MetadataStatus = "metadata_unavailable", AddressStatus = "not_read" }; }
                finally
                {
                    operation.Ready.TrySetResult();
                    Volatile.Write(ref _workerRunning, 0);
                }
            });
        }
        catch
        {
            Volatile.Write(ref _workerRunning, 0);
            throw;
        }
        return operation;
    }

    private static CodexAgentCaptureContext Read(DesktopCaptureTarget target, ReadOperation operation)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (previousDpi == 0 || !Matches(target)) return Unavailable("window_unavailable");
            using var process = Process.GetProcessById(checked((int)target.ProcessId));
            var started = process.StartTime.ToUniversalTime().Ticks;
            var app = Clean(process.ProcessName, 128);
            var title = ReadTitle(target.Handle);
            var bounds = ReadBounds(target.Handle);
            if (!Matches(target) || bounds is null || title is null) return Unavailable("window_unavailable");
            var native = new CodexAgentCaptureContext
            {
                MetadataReadAtUtc = DateTimeOffset.UtcNow,
                Application = app,
                WindowTitle = title,
                SourceWindowBounds = bounds,
                MetadataStatus = "observed",
                AddressStatus = "not_browser"
            };
            var browser = app.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                app.Equals("msedge", StringComparison.OrdinalIgnoreCase);
            if (operation.IsAbandoned) return Unavailable("metadata_timeout");
            var before = browser ? BrowserTabAccess.TryGetSelectedTab(target.Handle) : null;
            if (operation.IsAbandoned) return Unavailable("metadata_timeout");
            var firstAddress = before is null ? null : BrowserTabAccess.ReadObservedAddress(target.Handle);
            // Only accept browser evidence when its two observations bracket the actual desktop pixels.
            operation.Ready.TrySetResult();
            if (!operation.FrameCaptured.Task.Wait(TimeSpan.FromSeconds(1)) || operation.IsAbandoned)
                return Unavailable("metadata_timeout");
            var secondAddress = before is null ? null : BrowserTabAccess.ReadObservedAddress(target.Handle);
            var after = browser ? BrowserTabAccess.TryGetSelectedTab(target.Handle) : null;
            if (!Matches(target) || process.HasExited || process.StartTime.ToUniversalTime().Ticks != started ||
                !string.Equals(ReadTitle(target.Handle), title, StringComparison.Ordinal) || ReadBounds(target.Handle) != bounds)
                return Unavailable("window_changed_during_metadata_read");
            if (!browser) return native with { MetadataReadAtUtc = DateTimeOffset.UtcNow };
            if (before is null) return native with
            {
                MetadataReadAtUtc = DateTimeOffset.UtcNow, AddressStatus = "selected_tab_unavailable"
            };
            if (after is null || before.ProcessId != target.ProcessId || after.ProcessId != target.ProcessId ||
                before.ThreadId != target.ThreadId || after.ThreadId != target.ThreadId ||
                !before.RuntimeId.SequenceEqual(after.RuntimeId) || !string.Equals(before.Title, after.Title, StringComparison.Ordinal) ||
                firstAddress != secondAddress)
                return native with
                {
                    MetadataReadAtUtc = DateTimeOffset.UtcNow,
                    MetadataStatus = "browser_changed_during_metadata_read",
                    AddressStatus = "not_read"
                };
            if (operation.IsAbandoned) return Unavailable("metadata_timeout");
            var observed = native with
            {
                MetadataReadAtUtc = DateTimeOffset.UtcNow,
                BrowserTabTitle = Clean(after.Title, 1024),
                ObservedAddress = secondAddress!.Address,
                AddressStatus = secondAddress.Status
            };
            operation.StableTab = after with { RuntimeId = after.RuntimeId.ToArray() };
            return observed;
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static CodexAgentCaptureContext Unavailable(string status) => new()
    {
        MetadataReadAtUtc = DateTimeOffset.UtcNow, MetadataStatus = status, AddressStatus = "not_read"
    };

    private static bool Matches(DesktopCaptureTarget target)
    {
        if (target.Handle == 0 || !IsWindow(target.Handle) || !IsWindowVisible(target.Handle)) return false;
        var threadId = GetWindowThreadProcessId(target.Handle, out var processId);
        return threadId == target.ThreadId && processId == target.ProcessId;
    }

    private static string? ReadTitle(nint handle) =>
        DesktopCaptureWindowText.TryRead(handle, 1024, out var title) ? Clean(title, 1024) : null;

    private static CodexCaptureRectangle? ReadBounds(nint handle)
    {
        if (DwmGetWindowAttribute(handle, 9, out NativeRectangle bounds, Marshal.SizeOf<NativeRectangle>()) != 0 &&
            !GetWindowRect(handle, out bounds)) return null;
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        if (width is < 1 or > 100_000 || height is < 1 or > 100_000 ||
            bounds.Left is < -1_000_000 or > 1_000_000 || bounds.Top is < -1_000_000 or > 1_000_000) return null;
        return new(bounds.Left, bounds.Top, (int)width, (int)height);
    }

    private static string Clean(string value, int maximum) => new(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray());

    internal sealed class ReadOperation
    {
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FrameCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<CodexAgentCaptureContext> Worker = null!;
        internal BrowserTabCandidate? StableTab;
        private BrowserTabCandidate? _observedTab;
        private int _abandoned;
        internal bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;
        // Session-only identity; never included in screenshot metadata or saved prompts.
        public BrowserTabCandidate? ObservedTab => IsAbandoned ? null : Volatile.Read(ref _observedTab);

        public async Task WaitForObservationAsync(CancellationToken cancellationToken)
        {
            try { await Ready.Task.WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { Abandon(); }
            catch (OperationCanceledException) { Abandon(); throw; }
        }

        public void NotifyFrameCaptured() => FrameCaptured.TrySetResult();

        public async Task<CodexAgentCaptureContext> FinishAsync(CancellationToken cancellationToken)
        {
            if (IsAbandoned) return new() { MetadataStatus = "metadata_timeout", AddressStatus = "not_read" };
            try
            {
                var context = await Worker.WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsAbandoned) return new() { MetadataStatus = "metadata_timeout", AddressStatus = "not_read" };
                Volatile.Write(ref _observedTab, context.MetadataStatus == "observed" ? StableTab : null);
                return context;
            }
            catch (TimeoutException)
            {
                Abandon();
                return new() { MetadataStatus = "metadata_timeout", AddressStatus = "not_read" };
            }
            catch (OperationCanceledException) { Abandon(); throw; }
        }

        public void Abandon()
        {
            Interlocked.Exchange(ref _abandoned, 1);
            FrameCaptured.TrySetResult();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRectangle bounds);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRectangle value, int size);
}
