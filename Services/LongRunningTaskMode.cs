using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FullStackLauncher.Services;

/// <summary>Dashboard-owned power request and idle pointer activity. All calls stay on its dispatcher.</summary>
internal sealed class LongRunningTaskMode : IDisposable
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private const uint DisplayRequired = 0x00000002;
    private const uint IdleMilliseconds = 60_000;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private long _lastNudge;
    private bool _disposed;

    public bool IsEnabled { get; private set; }
    public string Status { get; private set; } = "Off. Normal Windows sleep settings apply.";
    public event Action? StatusChanged;

    public LongRunningTaskMode(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _timer.Tick += Tick;
    }

    public void SetEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == IsEnabled) return;
        // Execution state belongs to the calling OS thread, so never use Task.Run here.
        if (SetThreadExecutionState(Continuous | (enabled ? SystemRequired | DisplayRequired : 0)) == 0)
            throw new InvalidOperationException("Windows could not change the keep-awake request. Try again or close the launcher.");
        IsEnabled = enabled;
        if (enabled)
        {
            _lastNudge = Environment.TickCount64;
            _timer.Start();
            SetStatus("On. Keeping the computer and display awake; pointer nudges begin after one idle minute.");
        }
        else
        {
            _timer.Stop();
            SetStatus("Off. Normal Windows sleep settings apply.");
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !IsEnabled) return;
        if (Environment.TickCount64 - _lastNudge < IdleMilliseconds) return;

        // Only the dispatcher's own interactive desktop may receive a nudge. Locked,
        // secure or inaccessible desktops are skipped; never switch desktops or focus.
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == IntPtr.Zero ||
            !GetUserObjectInformation(desktop, 6 /* UOI_IO */, out var receivingInput, sizeof(int), out _) ||
            receivingInput == 0)
        {
            SetStatus("On. Keeping awake; pointer nudges are paused while the desktop is unavailable.");
            return;
        }

        var lastInput = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref lastInput))
        {
            SetStatus("On. Keeping awake; Windows could not read idle time, so pointer nudges are paused.");
            return;
        }
        var idle = unchecked((uint)Environment.TickCount - lastInput.Time);
        // Unsigned subtraction handles tick-count rollover. A future/invalid timestamp
        // must not be treated as an idle session.
        if (idle < IdleMilliseconds || idle > int.MaxValue)
        {
            SetStatus("On. Keeping the computer and display awake; waiting for one idle minute.");
            return;
        }
        // Do not move during a held key/button or drag, even if the last event is old.
        for (var key = 1; key < 256; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return;

        // Send both relative movements together so real input cannot be interleaved
        // with a delayed cursor restore. No clicks, keystrokes or window activation.
        Input[] inputs = [MouseMove(1), MouseMove(-1)];
        _lastNudge = Environment.TickCount64;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        SetStatus(sent == inputs.Length
            ? "On. Keeping the computer and display awake; idle pointer nudges are active."
            : "On. Keeping awake; Windows blocked pointer activity. Teams presence is not guaranteed.");
    }

    private static Input MouseMove(int dx) => new()
    {
        Type = 0 /* INPUT_MOUSE */,
        Mouse = new MouseInput { Dx = dx, Dy = 0, MouseData = 0, Flags = 0x0001 | 0x2000, Time = 0, ExtraInfo = UIntPtr.Zero }
    };

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        if (IsEnabled) SetThreadExecutionState(Continuous);
        IsEnabled = false;
        // Windows also removes this thread's request when the process exits.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    // MOUSEINPUT is the largest INPUT union member. Sequential native alignment
    // supplies the required padding on both 32-bit and 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, out int information, uint length, out uint needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
