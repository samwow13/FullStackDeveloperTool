using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FullStackLauncher.CompanionProtocol;

namespace FullStackRemoteCompanion;

// One capture/write at a time provides backpressure without accumulating stale frames.
// Input has its own reader and watchdog so a blocked frame write cannot hold keys down.
internal sealed class DesktopSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop;
    private readonly Func<bool> _authorized;
    private readonly DesktopInput _input = new();
    private RemoteDesktopOptions _options;
    private RemoteDesktopMonitor[] _monitors;
    private RemoteDesktopMonitor _monitor;
    private long _generation;
    private long _sequence;
    private long _firstInputSequence = long.MaxValue;
    private long _lastInputSequence;
    private long _lastCommandAt = Environment.TickCount64;
    private long _lastDisplayAt = Environment.TickCount64;
    private readonly Dictionary<long, long> _sentFrames = [];
    private bool _stopped;
    public string ControllerName { get; }
    public RemoteDesktopMonitor[] Monitors { get { lock (_gate) return _monitors; } }

    public DesktopSession(RemoteDesktopOptions options, string controllerName, Func<bool> authorized, CancellationToken token)
    {
        ValidateOptions(options);
        DesktopNative.RequireInputDesktop();
        _monitors = DesktopNative.Monitors();
        _monitor = SelectMonitor(_monitors, options.MonitorId);
        _options = options with { MonitorId = _monitor.Id };
        ControllerName = controllerName;
        _authorized = authorized;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public static void ValidateOptions(RemoteDesktopOptions options)
    {
        if (options.MonitorId is null || options.MonitorId.Length > 128 || options.Revision < 0 ||
            options.MaxFramesPerSecond is < 1 or > 60 || options.MaxWidth is < 320 or > 3840 || options.JpegQuality is < 50 or > 95)
            throw new InvalidDataException("Desktop settings are outside supported limits.");
    }

    private static RemoteDesktopMonitor SelectMonitor(RemoteDesktopMonitor[] monitors, string id) =>
        (id.Length == 0 ? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault() : monitors.FirstOrDefault(m => m.Id == id))
        ?? throw new InvalidOperationException("Selected monitor is unavailable. Reopen the desktop to select a connected monitor.");

    public async Task RunAsync(Stream stream)
    {
        lock (_gate) { _lastCommandAt = Environment.TickCount64; _lastDisplayAt = _lastCommandAt; }
        var capture = Task.Run(() => CaptureAsync(stream, _stop.Token));
        var commands = Task.Run(() => ReadCommandsAsync(stream, _stop.Token));
        var watchdog = Task.Run(() => WatchdogAsync(_stop.Token));
        try
        {
            await await Task.WhenAny(capture, commands, watchdog).ConfigureAwait(false);
        }
        finally
        {
            Stop();
            // All network waits are cancelable; capture owns its GDI resources until it finishes.
            try { await Task.WhenAll(capture, commands, watchdog).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or Win32Exception or System.Text.Json.JsonException or ExternalException or ArgumentException) { }
        }
    }

    private async Task CaptureAsync(Stream stream, CancellationToken token)
    {
        using var capture = new DesktopCapture();
        var topologyCheckedAt = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            if (!_authorized()) throw new IOException("Desktop approval was revoked or desktop access was stopped.");
            DesktopNative.RequireInputDesktop();
            RemoteDesktopOptions options;
            RemoteDesktopMonitor monitor;
            RemoteDesktopMonitor[] monitors;
            long generation;
            var currentMonitors = Environment.TickCount64 - topologyCheckedAt >= 1000 ? DesktopNative.Monitors() : null;
            if (currentMonitors is not null) topologyCheckedAt = Environment.TickCount64;
            lock (_gate)
            {
                if (currentMonitors is not null && !_monitors.SequenceEqual(currentMonitors))
                {
                    _input.ReleaseAll(); _firstInputSequence = long.MaxValue; _sentFrames.Clear(); ++_generation;
                    _monitors = currentMonitors;
                    _monitor = SelectMonitor(_monitors, _options.MonitorId);
                }
                options = _options; monitor = _monitor; monitors = _monitors; generation = _generation;
            }
            var jpeg = capture.Capture(monitor, options, out var width, out var height);
            if (jpeg.Length is < 1 or > RemoteProtocol.MaximumDesktopJpegBytes) throw new IOException("Desktop JPEG exceeds the supported frame size. Reduce resolution or quality.");
            var sequence = Interlocked.Increment(ref _sequence);
            var metadata = new RemoteDesktopFrame
            {
                MonitorId = monitor.Id, Width = width, Height = height, SourceWidth = monitor.Width, SourceHeight = monitor.Height,
                Sequence = sequence, JpegLength = jpeg.Length, Monitors = monitors, OptionsRevision = options.Revision,
                CaptureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await RemoteProtocol.WriteAsync(stream, metadata, deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync(jpeg, deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            lock (_gate)
            {
                if (generation == _generation && !_stopped)
                {
                    if (_firstInputSequence == long.MaxValue) _firstInputSequence = sequence;
                    _lastInputSequence = sequence;
                    var now = Environment.TickCount64;
                    foreach (var stale in _sentFrames.Where(f => now - f.Value > 2500).Select(f => f.Key).ToArray()) _sentFrames.Remove(stale);
                    _sentFrames[sequence] = now - (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }
            var remaining = TimeSpan.FromSeconds(1d / options.MaxFramesPerSecond) - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }

    private async Task ReadCommandsAsync(Stream stream, CancellationToken token)
    {
        var rateStart = Environment.TickCount64;
        var commandCount = 0;
        while (!token.IsCancellationRequested)
        {
            var command = await RemoteProtocol.ReadAsync<RemoteDesktopCommand>(stream, token).ConfigureAwait(false);
            var now = Environment.TickCount64;
            if (now - rateStart >= 1000) { rateStart = now; commandCount = 0; }
            if (++commandCount > 1000) throw new InvalidDataException("Desktop input rate exceeds the supported limit.");
            if (!_authorized()) throw new IOException("Desktop approval was revoked or desktop access was stopped.");
            DesktopNative.RequireInputDesktop();
            lock (_gate)
            {
                if (_stopped) return;
                _lastCommandAt = now;
                if (command.Kind == "heartbeat")
                {
                    if (RecentSequence(command.Sequence, now)) _lastDisplayAt = Math.Max(_lastDisplayAt, _sentFrames[command.Sequence]);
                    else _input.ReleaseAll();
                    continue;
                }
                if (command.Kind == "release") { _input.ReleaseAll(); continue; }
                if (command.Kind == "settings")
                {
                    if (command.Options is not { } options) throw new InvalidDataException("Desktop settings are missing.");
                    ValidateOptions(options);
                    var monitor = SelectMonitor(_monitors, options.MonitorId);
                    if (options.Revision < _options.Revision) throw new InvalidDataException("Desktop settings revision is stale.");
                    _input.ReleaseAll(); _firstInputSequence = long.MaxValue; _sentFrames.Clear(); ++_generation;
                    _lastDisplayAt = now;
                    _options = options with { MonitorId = monitor.Id }; _monitor = monitor;
                    continue;
                }
                if (command.Kind is not ("move" or "button" or "key" or "wheel")) throw new InvalidDataException("Desktop command is unsupported.");
                // Late commands from a previous monitor/settings generation must never reach Windows.
                if (!RecentSequence(command.Sequence, now)) { _input.ReleaseAll(); continue; }
                _input.Apply(command, _monitor);
            }
        }
    }

    private async Task WatchdogAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            if (!_authorized() || !DesktopNative.IsInputDesktop()) throw new IOException("Desktop is locked, unavailable, or access was stopped.");
            lock (_gate)
            {
                if (Environment.TickCount64 - _lastCommandAt > 3000) throw new IOException("Desktop input heartbeat timed out.");
                if (Environment.TickCount64 - _lastDisplayAt > 3000) throw new IOException("Desktop display heartbeat timed out.");
            }
        }
    }
    private bool RecentSequence(long sequence, long now) => sequence >= _firstInputSequence && sequence <= _lastInputSequence &&
        _sentFrames.TryGetValue(sequence, out var capturedAt) && now - capturedAt <= 2000;

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true; _firstInputSequence = long.MaxValue;
            _input.ReleaseAll(); _stop.Cancel();
        }
    }
    public void Dispose() { Stop(); _stop.Dispose(); }
}

internal sealed class DesktopCapture : IDisposable
{
    private readonly ImageCodecInfo _jpegCodec = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly MemoryStream _encoded = new();
    private Bitmap? _source;
    private Bitmap? _scaled;
    private Graphics? _sourceGraphics;
    private Graphics? _scaledGraphics;
    public ReadOnlyMemory<byte> Capture(RemoteDesktopMonitor monitor, RemoteDesktopOptions options, out int width, out int height)
    {
        if (monitor.Width is < 1 or > 16384 || monitor.Height is < 1 or > 16384 || (long)monitor.Width * monitor.Height > 40_000_000)
            throw new InvalidOperationException("Monitor resolution exceeds the supported capture size.");
        using var dpi = new DesktopNative.PhysicalPixels();
        var scale = Math.Min(1d, Math.Min(options.MaxWidth / (double)monitor.Width, 7680d / monitor.Height));
        scale = Math.Min(scale, Math.Sqrt(16_777_216d / ((long)monitor.Width * monitor.Height)));
        // Round down so the 16-megapixel product cannot exceed the decoder's bound.
        width = Math.Max(1, (int)Math.Floor(monitor.Width * scale));
        height = Math.Max(1, (int)Math.Floor(monitor.Height * scale));
        if (_source?.Width != monitor.Width || _source.Height != monitor.Height)
        {
            _sourceGraphics?.Dispose(); _source?.Dispose();
            _source = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppRgb);
            _sourceGraphics = Graphics.FromImage(_source);
        }
        DesktopNative.RequireInputDesktop();
        var sourceGraphics = _sourceGraphics ?? throw new InvalidOperationException("Desktop capture surface is unavailable.");
        var source = _source ?? throw new InvalidOperationException("Desktop capture bitmap is unavailable.");
        DesktopNative.CopyScreen(sourceGraphics, monitor);
        DesktopNative.DrawCursor(sourceGraphics, monitor);
        Bitmap target = source;
        if (width != monitor.Width || height != monitor.Height)
        {
            if (_scaled?.Width != width || _scaled.Height != height)
            {
                _scaledGraphics?.Dispose(); _scaled?.Dispose();
                _scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                _scaledGraphics = Graphics.FromImage(_scaled);
                _scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            _scaledGraphics!.DrawImage(source, new Rectangle(0, 0, width, height));
            target = _scaled!;
        }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)options.JpegQuality);
        _encoded.SetLength(0);
        target.Save(_encoded, _jpegCodec, parameters);
        return _encoded.GetBuffer().AsMemory(0, checked((int)_encoded.Length));
    }
    public void Dispose()
    {
        _sourceGraphics?.Dispose(); _scaledGraphics?.Dispose(); _source?.Dispose(); _scaled?.Dispose(); _encoded.Dispose();
    }
}

internal sealed class DesktopInput
{
    private readonly HashSet<(int Key, bool Extended)> _keys = [];
    private readonly HashSet<string> _buttons = new(StringComparer.Ordinal);
    public void Apply(RemoteDesktopCommand command, RemoteDesktopMonitor monitor)
    {
        DesktopNative.RequireInputDesktop();
        if (command.Kind == "key")
        {
            if (command.VirtualKey is < 8 or > 254) throw new InvalidDataException("Desktop virtual key is invalid.");
            var key = (command.VirtualKey, command.Extended);
            if (!command.Down && !_keys.Contains(key)) return;
            if (command.Down) _keys.Add(key);
            DesktopNative.Send(DesktopNative.KeyInput(command.VirtualKey, command.Extended, command.Down));
            if (!command.Down) _keys.Remove(key);
            return;
        }
        if (!double.IsFinite(command.X) || !double.IsFinite(command.Y) || command.X is < 0 or > 1 || command.Y is < 0 or > 1)
            throw new InvalidDataException("Desktop pointer position is invalid.");
        if (command.Kind == "button") _ = DesktopNative.ButtonInput(command.Button, command.Down);
        if (command.Kind == "wheel" && command.WheelDelta is < -1200 or > 1200) throw new InvalidDataException("Desktop wheel delta is invalid.");
        using var dpi = new DesktopNative.PhysicalPixels();
        var virtualLeft = DesktopNative.GetSystemMetrics(76);
        var virtualTop = DesktopNative.GetSystemMetrics(77);
        var virtualWidth = DesktopNative.GetSystemMetrics(78);
        var virtualHeight = DesktopNative.GetSystemMetrics(79);
        if (virtualWidth < 2 || virtualHeight < 2) throw new InvalidOperationException("Desktop geometry is unavailable.");
        var x = monitor.Left + (int)Math.Round(command.X * (monitor.Width - 1));
        var y = monitor.Top + (int)Math.Round(command.Y * (monitor.Height - 1));
        DesktopNative.Send(DesktopNative.MouseInput(0x0001 | 0x8000 | 0x4000, 0,
            (int)Math.Round((x - virtualLeft) * 65535d / (virtualWidth - 1)),
            (int)Math.Round((y - virtualTop) * 65535d / (virtualHeight - 1))));
        if (command.Kind == "button")
        {
            var input = DesktopNative.ButtonInput(command.Button, command.Down);
            if (!command.Down && !_buttons.Contains(command.Button)) return;
            if (command.Down) _buttons.Add(command.Button);
            DesktopNative.Send(input);
            if (!command.Down) _buttons.Remove(command.Button);
        }
        else if (command.Kind == "wheel")
        {
            if (command.WheelDelta is < -1200 or > 1200) throw new InvalidDataException("Desktop wheel delta is invalid.");
            DesktopNative.Send(DesktopNative.MouseInput(0x0800, unchecked((uint)command.WheelDelta)));
        }
    }
    public void ReleaseAll()
    {
        foreach (var key in _keys) DesktopNative.TrySend(DesktopNative.KeyInput(key.Key, key.Extended, false));
        foreach (var button in _buttons) DesktopNative.TrySend(DesktopNative.ButtonInput(button, false));
        _keys.Clear(); _buttons.Clear();
    }
}

internal static class DesktopNative
{
    private static readonly int ProcessSessionId = ReadSessionId();
    private static readonly object SessionGate = new();
    private static long _sessionCheckedAt;
    private static bool _sessionActive;
    public static bool InteractiveProcess { get; } = Environment.UserInteractive && ProcessSessionId != 0;
    private static int ReadSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }
    public static void RequireInputDesktop()
    {
        if (!IsInputDesktop()) throw new InvalidOperationException("Desktop control requires an unlocked Windows session and the companion tray app. Windows service mode cannot control a desktop.");
    }
    public static bool IsInputDesktop()
    {
        if (!InteractiveProcess || !IsActiveSession()) return false;
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    private static bool IsActiveSession()
    {
        // OpenInputDesktop also succeeds in disconnected RDP sessions; require an active session.
        lock (SessionGate)
        {
            var now = Environment.TickCount64;
            if (_sessionCheckedAt != 0 && now - _sessionCheckedAt < 200) return _sessionActive;
            var buffer = IntPtr.Zero;
            try
            {
                _sessionActive = WTSQuerySessionInformation(IntPtr.Zero, -1, 8, out buffer, out var bytes)
                    ? bytes >= 4 && Marshal.ReadInt32(buffer) == 0
                    : WTSGetActiveConsoleSessionId() == (uint)ProcessSessionId;
                _sessionCheckedAt = now;
                return _sessionActive;
            }
            finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
        }
    }
    public static RemoteDesktopMonitor[] Monitors()
    {
        using var dpi = new PhysicalPixels();
        var monitors = new List<RemoteDesktopMonitor>();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info)) monitors.Add(new(info.Device, $"Display {monitors.Count + 1}" + ((info.Flags & 1) != 0 ? " · Primary" : ""),
                info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
            return monitors.Count < 32;
        }, IntPtr.Zero)) throw new Win32Exception("Windows monitors could not be read.");
        if (monitors.Count == 0) throw new InvalidOperationException("No Windows monitors are available.");
        return monitors.ToArray();
    }
    public static void DrawCursor(Graphics graphics, RemoteDesktopMonitor monitor)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 || cursor.Handle == IntPtr.Zero) return;
        var copied = CopyIcon(cursor.Handle);
        if (copied == IntPtr.Zero) return;
        try
        {
            if (!GetIconInfo(copied, out var info)) return;
            try
            {
                var dc = graphics.GetHdc();
                try { DrawIconEx(dc, cursor.Position.X - monitor.Left - (int)info.HotspotX, cursor.Position.Y - monitor.Top - (int)info.HotspotY, copied, 0, 0, 0, IntPtr.Zero, 3); }
                finally { graphics.ReleaseHdc(dc); }
            }
            finally { if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); if (info.Color != IntPtr.Zero) DeleteObject(info.Color); }
        }
        finally { DestroyIcon(copied); }
    }
    public static void CopyScreen(Graphics graphics, RemoteDesktopMonitor monitor)
    {
        var source = GetDC(IntPtr.Zero);
        if (source == IntPtr.Zero) throw new Win32Exception("Windows desktop capture is unavailable.");
        try
        {
            var target = graphics.GetHdc();
            try
            {
                if (!BitBlt(target, 0, 0, monitor.Width, monitor.Height, source, monitor.Left, monitor.Top, 0x40CC0020))
                    throw new Win32Exception("Windows desktop capture failed.");
            }
            finally { graphics.ReleaseHdc(target); }
        }
        finally { ReleaseDC(IntPtr.Zero, source); }
    }
    public readonly struct PhysicalPixels : IDisposable
    {
        private readonly IntPtr _previous;
        public PhysicalPixels()
        {
            _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (_previous == IntPtr.Zero) throw new Win32Exception("Windows physical-pixel desktop coordinates are unavailable.");
        }
        public void Dispose() { if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous); }
    }
    public static Input KeyInput(int key, bool extended, bool down) => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = (extended ? 1u : 0) | (down ? 0u : 2u) } } };
    public static Input MouseInput(uint flags, uint data = 0, int x = 0, int y = 0) => new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } };
    public static Input ButtonInput(string button, bool down) => button switch
    {
        "left" => MouseInput(down ? 0x0002u : 0x0004u), "right" => MouseInput(down ? 0x0008u : 0x0010u),
        "middle" => MouseInput(down ? 0x0020u : 0x0040u), "x1" => MouseInput(down ? 0x0080u : 0x0100u, 1),
        "x2" => MouseInput(down ? 0x0080u : 0x0100u, 2), _ => throw new InvalidDataException("Desktop mouse button is invalid.")
    };
    public static void Send(Input input)
    {
        if (!TrySend(input)) throw new InvalidOperationException("Windows rejected remote input. Elevated apps and secure desktops require local interaction.");
    }
    public static bool TrySend(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    // Sequential outer structure supplies the native union's pointer-sized alignment (40 bytes on x64).
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] public MouseData Mouse; [FieldOffset(0)] public KeyboardData Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public NativePoint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int information, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
}
