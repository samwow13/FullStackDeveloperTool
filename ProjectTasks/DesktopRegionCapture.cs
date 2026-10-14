using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace FullStackLauncher.ProjectTasks;

/// <summary>A rectangle in physical virtual-desktop pixels, including negative monitor origins.</summary>
internal readonly record struct DesktopCaptureRectangle(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
}

internal sealed record DesktopCaptureFrame(
    BitmapSource Image, DesktopCaptureRectangle Bounds, DesktopCaptureRectangle Selection);

internal sealed record DesktopCaptureResult(
    BitmapSource Image, CodexAgentCaptureContext Context, DesktopCaptureResumeTarget? ResumeTarget);

/// <summary>
/// One explicitly armed Ctrl+drag capture. Hooks run on a dedicated message thread;
/// screenshots and UI work never run inside a native input callback.
/// </summary>
internal sealed partial class DesktopRegionCapture : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<nint> _excludedWindowHandles;
    private readonly object _hookGate = new();
    private readonly HookProcedure _mouseProcedure;
    private readonly HookProcedure _keyboardProcedure;
    private readonly CancellationTokenSource _captureWork = new();
    private readonly CancellationToken _captureToken;
    private readonly TaskCompletionSource<DesktopCaptureResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DesktopCaptureRectangle> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _captureTask;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private uint _threadId;
    private int _started;
    private int _disposed;
    private int _cancelRequested;
    private int _moveQueued;
    private bool _mouseOwned;
    private bool _escapeOwned;
    private bool _selectionBegan;
    private bool _canceling;
    private bool _frameShown;
    private int _startX;
    private int _startY;
    private int _currentX;
    private int _currentY;
    private string? _failure;
    private DateTime _drainDeadline;
    private DesktopLayout? _layout;

    public DesktopRegionCapture(Dispatcher dispatcher, IEnumerable<nint>? excludedWindowHandles = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _excludedWindowHandles = excludedWindowHandles?.Where(handle => handle != 0).ToHashSet() ?? [];
        _mouseProcedure = MouseHook;
        _keyboardProcedure = KeyboardHook;
        _captureToken = _captureWork.Token;
        _dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
    }

    /// <summary>Runs on the WPF dispatcher; hide controller windows synchronously here.</summary>
    public event Action? SelectionStarting;
    /// <summary>Runs after the frozen screenshot exists; the overlay may now be displayed.</summary>
    public event Action<DesktopCaptureFrame>? SelectionStarted;
    public event Action<DesktopCaptureRectangle>? SelectionChanged;
    /// <summary>Hide an overlay promptly while the canceled gesture's input release is drained.</summary>
    public event Action? SelectionCanceled;

    public async Task<DesktopCaptureResult?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Create a new desktop capture for each region.");
        using var registration = cancellationToken.Register(Cancel);
        var thread = new Thread(HookThread) { IsBackground = true, Name = "Launcher desktop region capture" };
        try { thread.Start(); }
        catch
        {
            _threadExited.TrySetResult();
            Dispose();
            _captureWork.Dispose();
            throw;
        }
        try { return await _result.Task.ConfigureAwait(false); }
        finally
        {
            Dispose();
            await _threadExited.Task.ConfigureAwait(false);
            if (_captureTask != null) await _captureTask.ConfigureAwait(false);
            _captureWork.Dispose();
        }
    }

    /// <summary>Cancel capture without synthesizing mouse or keyboard input.</summary>
    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) PostThreadMessage(threadId, CancelMessage, UIntPtr.Zero, IntPtr.Zero);
    }

    private void Fail(string message)
    {
        Interlocked.CompareExchange(ref _failure, message, null);
        Cancel();
    }

    private void HookThread()
    {
        var previousDpi = IntPtr.Zero;
        UIntPtr timer = UIntPtr.Zero;
        try
        {
            // Create this thread's message queue before publishing its cancellation target.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            if (previousDpi == IntPtr.Zero)
                throw new InvalidOperationException("Physical screen coordinates are unavailable. Restart Launcher and retry capture.");
            _layout = ReadDesktopLayout();
            lock (_hookGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var module = GetModuleHandle(null);
                _mouseHook = SetWindowsHookEx(14, _mouseProcedure, module, 0); // WH_MOUSE_LL
                if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _keyboardHook = SetWindowsHookEx(13, _keyboardProcedure, module, 0); // WH_KEYBOARD_LL
                if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 250, IntPtr.Zero);
            if (timer == UIntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var expiry = DateTime.UtcNow.AddMinutes(30);
            while (Volatile.Read(ref _disposed) == 0)
            {
                var received = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (received == 0) break;
                if (received < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (message.Message == CancelMessage || message.Message == TimerMessage)
                {
                    if (DateTime.UtcNow >= expiry) Cancel();
                    if (!_canceling && ReadDesktopLayout().Signature != _layout.Signature)
                        Fail("Display layout changed. Choose another area to start a fresh capture.");
                    if (Volatile.Read(ref _cancelRequested) != 0) BeginCancellation();
                    if (_canceling)
                    {
                        // Only hook releases clear ownership: asynchronous key state can
                        // differ for an input event whose down transition was swallowed.
                        // A hard deadline prevents hooks surviving a lost input desktop.
                        if (!_mouseOwned && !_escapeOwned || DateTime.UtcNow >= _drainDeadline)
                        {
                            CompleteCancellation();
                            break;
                        }
                    }
                }
                else
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
        }
        catch (Exception)
        {
            _result.TrySetException(new InvalidOperationException(
                "Desktop capture could not start or lost screen access. Close the capture window and try again."));
        }
        finally
        {
            if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
            RemoveHooks();
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            Volatile.Write(ref _threadId, 0);
            _result.TrySetResult(null);
            _threadExited.TrySetResult();
        }
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || Volatile.Read(ref _disposed) != 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
        try
        {
            var input = Marshal.PtrToStructure<MouseInput>(data);
            if ((input.Flags & 1) != 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
            var kind = message.ToInt32();
            if (kind == LeftDown && !_selectionBegan && !_canceling && Volatile.Read(ref _cancelRequested) == 0 &&
                GetAsyncKeyState(0x11) < 0 && GetAsyncKeyState(0x12) >= 0 &&
                GetAsyncKeyState(0x5B) >= 0 && GetAsyncKeyState(0x5C) >= 0)
            {
                var window = WindowFromPoint(input.Point);
                // Leave controller controls and their owned popup windows usable.
                // Disabled Launcher targets are resolved off-hook in physical z-order.
                if (IsExcludedWindow(window))
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                _selectionBegan = true;
                _mouseOwned = true;
                _startX = _currentX = input.Point.X;
                _startY = _currentY = input.Point.Y;
                _captureTask = PrepareCaptureAsync(input.Point);
                return new IntPtr(1);
            }
            if (_mouseOwned && (kind == MouseMove || kind == LeftUp || kind == LeftDown))
            {
                Volatile.Write(ref _currentX, input.Point.X);
                Volatile.Write(ref _currentY, input.Point.Y);
                if (kind == LeftUp)
                {
                    _mouseOwned = false;
                    if (!_canceling && Volatile.Read(ref _cancelRequested) == 0) _released.TrySetResult(CurrentSelection());
                    else PostThreadMessage(_threadId, CancelMessage, UIntPtr.Zero, IntPtr.Zero);
                }
                else if (!_canceling) QueueSelectionChanged();
                // Allow Windows to move its real cursor. Blocking WM_MOUSEMOVE freezes
                // it and makes absolute hook coordinates unsuitable for region selection.
                // The nonactivating selection overlay receives those normal movements.
                return kind == MouseMove ? CallNextHookEx(IntPtr.Zero, code, message, data) : new IntPtr(1);
            }
        }
        catch (Exception)
        {
            Fail("Desktop selection could not read its pointer position. Choose another area and retry.");
            if (_mouseOwned) return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || Volatile.Read(ref _disposed) != 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
        try
        {
            var input = Marshal.PtrToStructure<KeyboardInput>(data);
            if (input.Key != 0x1B || (input.Flags & 0x10) != 0)
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            if (message.ToInt32() is KeyDown or SystemKeyDown)
            {
                _escapeOwned = true;
                Cancel();
                return new IntPtr(1);
            }
            if (_escapeOwned && message.ToInt32() is KeyUp or SystemKeyUp)
            {
                _escapeOwned = false;
                PostThreadMessage(_threadId, CancelMessage, UIntPtr.Zero, IntPtr.Zero);
                return new IntPtr(1);
            }
        }
        catch (Exception) { Cancel(); }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void BeginCancellation()
    {
        if (_canceling) return;
        _canceling = true;
        _drainDeadline = DateTime.UtcNow.AddSeconds(5);
        _captureWork.Cancel();
        if (!_dispatcher.HasShutdownStarted)
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                try { SelectionCanceled?.Invoke(); }
                catch (Exception) { /* The capture result still restores the caller's UI. */ }
            }), DispatcherPriority.Send);
    }

    private void CompleteCancellation()
    {
        var failure = Volatile.Read(ref _failure);
        if (failure != null) _result.TrySetException(new InvalidOperationException(failure));
        else _result.TrySetResult(null);
    }

    private bool IsExcludedWindow(nint window)
    {
        if (_excludedWindowHandles.Count == 0) return false;
        var root = GetAncestor(window, 2); // GA_ROOT
        if (root != 0) window = root;
        for (var depth = 0; window != 0 && depth < 16; depth++)
        {
            if (_excludedWindowHandles.Contains(window)) return true;
            var owner = GetWindow(window, 4); // GW_OWNER includes controller dropdowns and menus.
            if (owner == window) break;
            window = owner;
        }
        return false;
    }

    private DesktopCaptureTarget? ResolveTargetAtPoint(NativePoint point)
    {
        using var awareness = new PhysicalDpiScope();
        // Preserve Windows' ordinary hit testing for foreign apps, including
        // transparent overlays. Only supplement the disabled Launcher windows
        // that WindowFromPoint deliberately skips during a modal capture.
        var normalHit = WindowFromPoint(point);
        var root = GetAncestor(normalHit, 2); // GA_ROOT
        if (root != 0) normalHit = root;
        if (IsExcludedWindow(normalHit)) return null;
        var window = GetTopWindow(0);
        for (var count = 0; window != 0 && count < 512; count++, window = GetWindow(window, 2))
        {
            _captureToken.ThrowIfCancellationRequested();
            if (window == normalHit) break;
            var threadId = GetWindowThreadProcessId(window, out var processId);
            // Read the native disabled state: a ShowDialog owner can have
            // WPF IsEnabled=true while its HWND has WS_DISABLED set.
            if (processId != Environment.ProcessId || threadId == 0 || IsWindowEnabled(window) ||
                !IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var bounds) ||
                point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)
                continue;
            if (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
                continue;
            if (IsExcludedWindow(window)) return null;
            return new DesktopCaptureTarget(window, processId, threadId);
        }
        if (normalHit == 0) return null;
        var normalThread = GetWindowThreadProcessId(normalHit, out var normalProcess);
        return normalThread != 0 && normalProcess != 0
            ? new DesktopCaptureTarget(normalHit, normalProcess, normalThread) : null;
    }

    private async Task PrepareCaptureAsync(NativePoint point)
    {
        DesktopCaptureMetadataReader.ReadOperation? metadataRead = null;
        DesktopCaptureResumeTarget? resumeTarget = null;
        try
        {
            var target = await Task.Run(() => ResolveTargetAtPoint(point), _captureToken);
            if (target is null)
            {
                Cancel();
                return;
            }
            await _dispatcher.InvokeAsync(() =>
            {
                _captureToken.ThrowIfCancellationRequested();
                SelectionStarting?.Invoke();
            }, DispatcherPriority.Send, _captureToken);
            // Let hidden WPF windows leave the composition before reading desktop pixels.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, _captureToken);
            await Task.Delay(45, _captureToken);
            var layout = _layout ?? throw new InvalidOperationException();
            metadataRead = DesktopCaptureMetadataReader.BeginRead(target);
            await metadataRead.WaitForObservationAsync(_captureToken);
            var frame = await Task.Run(() =>
            {
                var resume = DesktopCaptureResumeTarget.TryObserve(target, _dispatcher);
                try
                {
                    var pixels = CaptureDesktop(layout);
                    return (Image: pixels, CapturedAtUtc: DateTimeOffset.UtcNow, ResumeTarget: resume);
                }
                catch { resume?.Dispose(); throw; }
            }, _captureToken);
            resumeTarget = frame.ResumeTarget;
            var image = frame.Image;
            metadataRead.NotifyFrameCaptured();
            var metadata = await metadataRead.FinishAsync(_captureToken);
            resumeTarget?.RememberBrowserTab(metadataRead.ObservedTab);
            _captureToken.ThrowIfCancellationRequested();
            await _dispatcher.InvokeAsync(() =>
            {
                _captureToken.ThrowIfCancellationRequested();
                SelectionStarted?.Invoke(new DesktopCaptureFrame(image, layout.Bounds, CurrentSelection()));
                _frameShown = true;
            }, DispatcherPriority.Send, _captureToken);
            var selected = await _released.Task.WaitAsync(_captureToken);
            if (selected.Width < 4 || selected.Height < 4)
                throw new InvalidOperationException("Drag an area at least 4 by 4 pixels, then release the mouse button.");
            var cropped = await Task.Run(() =>
            {
                using var awareness = new PhysicalDpiScope();
                if (ReadDesktopLayout().Signature != layout.Signature)
                    throw new InvalidOperationException("Display layout changed. Choose another area to start a fresh capture.");
                var result = new CroppedBitmap(image, new Int32Rect(selected.Left - layout.Bounds.Left,
                    selected.Top - layout.Bounds.Top, selected.Width, selected.Height));
                result.Freeze();
                return result;
            }, _captureToken);
            _captureToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _cancelRequested) != 0) throw new OperationCanceledException(_captureToken);
            var context = metadata with
            {
                CapturedAtUtc = frame.CapturedAtUtc,
                CropBounds = new(selected.Left, selected.Top, selected.Width, selected.Height),
                OriginalImageWidth = cropped.PixelWidth,
                OriginalImageHeight = cropped.PixelHeight,
                SentImageWidth = cropped.PixelWidth,
                SentImageHeight = cropped.PixelHeight
            };
            context.Validate();
            if (resumeTarget is not null && !resumeTarget.IsCurrent())
            {
                resumeTarget.Dispose();
                resumeTarget = null;
            }
            if (_result.TrySetResult(new(cropped, context, resumeTarget))) resumeTarget = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Fail(error is InvalidOperationException && error.Message.StartsWith("Drag an area", StringComparison.Ordinal)
                ? error.Message : "Desktop screenshot could not be captured. Keep the screen visible and choose another area.");
        }
        finally
        {
            resumeTarget?.Dispose();
            metadataRead?.Abandon();
        }
    }

    private DesktopCaptureRectangle CurrentSelection()
    {
        var bounds = _layout!.Bounds;
        var x = Math.Clamp(Volatile.Read(ref _currentX), bounds.Left, bounds.Right);
        var y = Math.Clamp(Volatile.Read(ref _currentY), bounds.Top, bounds.Bottom);
        var startX = Math.Clamp(_startX, bounds.Left, bounds.Right);
        var startY = Math.Clamp(_startY, bounds.Top, bounds.Bottom);
        return new(Math.Min(startX, x), Math.Min(startY, y), Math.Abs(x - startX), Math.Abs(y - startY));
    }

    private void QueueSelectionChanged()
    {
        if (Interlocked.Exchange(ref _moveQueued, 1) != 0 || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _moveQueued, 0);
            if (_frameShown && !_captureToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                try { SelectionChanged?.Invoke(CurrentSelection()); }
                catch (Exception) { Fail("Desktop selection could not update. Choose another area and retry."); }
            }
        }), DispatcherPriority.Render);
    }

    private static BitmapSource CaptureDesktop(DesktopLayout expected)
    {
        using var awareness = new PhysicalDpiScope();
        if (ReadDesktopLayout().Signature != expected.Signature)
            throw new InvalidOperationException("Display layout changed before capture.");
        DwmFlush();
        var bounds = expected.Bounds;
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Drawing.Size(bounds.Width, bounds.Height), Drawing.CopyPixelOperation.SourceCopy);
        var handle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { DeleteObject(handle); }
    }

    private static DesktopLayout ReadDesktopLayout()
    {
        var monitors = new List<NativeRectangle>();
        MonitorProcedure callback = (IntPtr monitor, IntPtr dc, ref NativeRectangle rectangle, IntPtr parameter) =>
        {
            if (monitors.Count >= 64) return false;
            monitors.Add(rectangle);
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
            throw new InvalidOperationException("Desktop monitors are unavailable.");
        var left = monitors.Min(item => item.Left);
        var top = monitors.Min(item => item.Top);
        var width = checked(monitors.Max(item => item.Right) - left);
        var height = checked(monitors.Max(item => item.Bottom) - top);
        if (width < 4 || height < 4 || width > 32768 || height > 32768 || (long)width * height > 100_000_000)
            throw new InvalidOperationException("The desktop is too large to capture. Reduce its resolution and retry.");
        var signature = string.Join(";", monitors.OrderBy(item => item.Left).ThenBy(item => item.Top)
            .Select(item => $"{item.Left},{item.Top},{item.Right},{item.Bottom}"));
        return new(new DesktopCaptureRectangle(left, top, width, height), signature);
    }

    private void RemoveHooks()
    {
        lock (_hookGate)
        {
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        }
    }

    private void Dispatcher_ShutdownStarted(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
        _captureWork.Cancel();
        RemoveHooks();
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); // WM_QUIT
        _result.TrySetResult(null);
        if (Volatile.Read(ref _started) == 0) _captureWork.Dispose();
    }

    private sealed record DesktopLayout(DesktopCaptureRectangle Bounds, string Signature);
    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public PhysicalDpiScope()
        {
            if (_previous == IntPtr.Zero) throw new InvalidOperationException("Physical desktop coordinates are unavailable.");
        }
        public void Dispose() => SetThreadDpiAwarenessContext(_previous);
    }
}
