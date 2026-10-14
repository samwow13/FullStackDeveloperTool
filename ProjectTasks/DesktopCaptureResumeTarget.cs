using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Session-only capture destination. Never persisted with image metadata or waiting prompts.</summary>
internal sealed class DesktopCaptureResumeTarget : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SafeProcessHandle _process;
    private readonly nint _window;
    private readonly uint _processId;
    private readonly uint _threadId;
    private readonly long _processStarted;
    private BrowserTabCandidate? _browserTab;
    private int _disposed;

    internal nint Handle => _window;
    internal bool IsLauncherWindow => _processId == Environment.ProcessId;
    internal bool HasBrowserTab => _browserTab is not null;

    private DesktopCaptureResumeTarget(Dispatcher dispatcher, SafeProcessHandle process,
        DesktopCaptureTarget target, long processStarted)
    {
        _dispatcher = dispatcher;
        _process = process;
        _window = target.Handle;
        _processId = target.ProcessId;
        _threadId = target.ThreadId;
        _processStarted = processStarted;
    }

    internal static DesktopCaptureResumeTarget? TryObserve(DesktopCaptureTarget target, Dispatcher dispatcher)
    {
        SafeProcessHandle? process = null;
        try
        {
            if (!MatchesWindow(target.Handle, target.ProcessId, target.ThreadId)) return null;
            process = OpenProcess(0x00100000 | 0x1000, false, target.ProcessId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (process.IsInvalid || !TryReadCreationTime(process, out var started)) return null;
            var result = new DesktopCaptureResumeTarget(dispatcher, process, target, started);
            if (!result.IsCurrent()) return null;
            process = null;
            return result;
        }
        catch (Exception) { return null; }
        finally { process?.Dispose(); }
    }

    internal void RememberBrowserTab(BrowserTabCandidate? candidate)
    {
        if (candidate is null || candidate.WindowHandle != _window || candidate.ProcessId != _processId ||
            candidate.ThreadId != _threadId || candidate.RuntimeId.Length == 0 || !IsCurrent()) return;
        _browserTab = candidate with { RuntimeId = candidate.RuntimeId.ToArray() };
    }

    internal bool IsCurrent()
    {
        if (Volatile.Read(ref _disposed) != 0 || _process.IsClosed || _process.IsInvalid) return false;
        try
        {
            // The process handle pins its identity even if its numeric PID is later reused.
            return WaitForSingleObject(_process, 0) == 0x00000102 &&
                TryReadCreationTime(_process, out var started) && started == _processStarted &&
                MatchesWindow(_window, _processId, _threadId);
        }
        catch (Exception) { return false; }
    }

    internal async Task<bool> TryRestoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || !IsCurrent()) return false;
        nint initialForeground = 0;
        var ready = await _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent()) return false;
            initialForeground = GetForegroundWindow();
            // Restore only the existing minimized window. Never launch an app,
            // navigate a browser, or alter a normal/maximized window's geometry.
            return !IsIconic(_window) || ShowWindowAsync(_window, 9); // SW_RESTORE
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
        if (!ready) return false;

        bool ContextStillCurrent() => !cancellationToken.IsCancellationRequested && IsCurrent() &&
            GetForegroundWindow() is var foreground && (foreground == initialForeground || foreground == _window);

        var restoreWait = Stopwatch.StartNew();
        while (IsIconic(_window) && restoreWait.Elapsed < TimeSpan.FromMilliseconds(750))
        {
            if (!ContextStillCurrent()) return false;
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ContextStillCurrent() || IsIconic(_window)) return false;

        var tabRestored = _browserTab is not { } candidate ||
            await BrowserTabAccess.TryRestoreRememberedTabAsync(candidate, ContextStillCurrent, cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || !ContextStillCurrent()) return false;
        var windowRestored = await _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ContextStillCurrent()) return false;
            // A Launcher dashboard can remain natively disabled by ShowDialog.
            // Its caller restores the capture view without undoing modal protection.
            if (IsLauncherWindow && !IsWindowEnabled(_window))
                return SetWindowPos(_window, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
            return SetForegroundWindow(_window) && GetForegroundWindow() == _window && IsCurrent();
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
        return windowRestored && tabRestored;
    }

    private static bool MatchesWindow(nint window, uint processId, uint threadId) =>
        window != 0 && IsWindow(window) && IsWindowVisible(window) &&
        DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(uint)) == 0 && cloaked == 0 &&
        GetWindowThreadProcessId(window, out var currentProcess) == threadId && currentProcess == processId;

    private static bool TryReadCreationTime(SafeProcessHandle process, out long started)
    {
        started = 0;
        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return false;
        started = unchecked((long)(((ulong)creation.High << 32) | creation.Low));
        return started != 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime { public uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int left, int top, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);
}
