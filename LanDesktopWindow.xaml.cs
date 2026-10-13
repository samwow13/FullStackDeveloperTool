using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.CompanionProtocol;
using FullStackLauncher.Services;

namespace FullStackLauncher;

/// <summary>A bounded, latest-frame viewer for an explicitly opened paired companion.</summary>
public partial class LanDesktopWindow : Window
{
    private readonly LanCompanionClient _client;
    private readonly LanCompanionSession _companion;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly object _frameGate = new();
    private readonly HashSet<(int VirtualKey, bool Extended)> _pressedKeys = [];
    private readonly HashSet<MouseButton> _pressedButtons = [];
    private LanDesktopSession? _session;
    private LanDesktopFrame? _pendingFrame;
    private RemoteDesktopFrame? _displayedFrame;
    private RemoteDesktopMonitor[] _monitors = [];
    private RemoteDesktopOptions _options = new();
    private long _pendingReceivedAt;
    private long _displayedReceivedAt;
    private long _lastReceivedBytes;
    private long _lastMetricsAt = Stopwatch.GetTimestamp();
    private int _displayedCount;
    private bool _ready;
    private bool _closed;
    private bool _faulted;
    private bool _releasing;
    private bool _inputEnabled;
    private bool _refreshingMonitors;
    private bool _actualSize;
    private bool _fullScreen;
    private WindowState _previousWindowState;
    private int _lastNativeKey;
    private bool _lastNativeExtended;
    private HwndSource? _source;

    public LanDesktopWindow(LanCompanionClient client, LanCompanionSession companion)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _client = client;
        _companion = companion;
        InitializeComponent();
        Title = $"{companion.ComputerName} · Companion desktop";
        _ready = true;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
        Deactivated += (_, _) => ReleaseControl();
        PreviewKeyDown += Window_KeyDown;
        PreviewKeyUp += Window_KeyUp;
        SourceInitialized += Window_SourceInitialized;
        _statusTimer.Tick += StatusTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += RenderLatestFrame;
        _statusTimer.Start();
        try
        {
            var session = await _client.OpenDesktopAsync(_companion, _options, _lifetime.Token);
            if (_closed) { await session.DisposeAsync(); return; }
            _session = session;
            session.FrameReceived += Session_FrameReceived;
            session.Faulted += Session_Faulted;
            if (session.IsClosed)
            {
                ShowFailure(session.FailureMessage ?? "Desktop connection ended before the first frame. Close this window and reopen View & control.");
                return;
            }
            ProfileBox.IsEnabled = true;
            StatusText.Text = "Waiting for first frame…";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowFailure(ex.Message); }
    }

    private void Session_FrameReceived(object? sender, LanDesktopFrame frame)
    {
        lock (_frameGate)
        {
            if (_closed) return;
            _pendingFrame = frame;
            _pendingReceivedAt = Stopwatch.GetTimestamp();
        }
    }

    private void Session_Faulted(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => ShowFailure(message)));
    }

    private void RenderLatestFrame(object? sender, EventArgs e)
    {
        if (_closed || _faulted) return;
        LanDesktopFrame? frame;
        long receivedAt;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            receivedAt = _pendingReceivedAt;
            _pendingFrame = null;
        }
        if (frame == null || frame.Metadata.OptionsRevision != _options.Revision) return;
        var metadata = frame.Metadata;
        if (_displayedFrame != null && (!HasFreshFrame() || _displayedFrame.MonitorId != metadata.MonitorId ||
            _displayedFrame.SourceWidth != metadata.SourceWidth || _displayedFrame.SourceHeight != metadata.SourceHeight)) ReleaseControl();
        _displayedFrame = metadata;
        _displayedReceivedAt = receivedAt;
        DesktopImage.Source = frame.Bitmap;
        _session?.NotifyFramePresented(metadata.Sequence);
        _displayedCount++;
        ConnectingText.Visibility = Visibility.Collapsed;
        RefreshMonitors(metadata);
        if (_actualSize) SetImageSize();
        ControlSwitch.IsEnabled = SendEscapeButton.IsEnabled = HasFreshFrame();
        if (ControlSwitch.IsChecked != true) StatusText.Text = "Viewing · Enable Control for mouse and keyboard · Esc releases input";
    }

    private void RefreshMonitors(RemoteDesktopFrame frame)
    {
        _refreshingMonitors = true;
        try
        {
            if (!_monitors.SequenceEqual(frame.Monitors))
            {
                _monitors = frame.Monitors;
                MonitorBox.ItemsSource = frame.Monitors.Select((monitor, index) => new MonitorChoice(monitor.Id,
                    $"{index + 1}. {monitor.Name} · {monitor.Width}×{monitor.Height}{(monitor.Primary ? " · Main" : "")}")).ToArray();
            }
            MonitorBox.SelectedValue = frame.MonitorId;
            MonitorBox.IsEnabled = frame.Monitors.Length > 1;
            NextMonitorButton.IsEnabled = frame.Monitors.Length > 1;
        }
        finally { _refreshingMonitors = false; }
    }

    private bool HasFreshFrame() => !_closed && !_faulted && _session != null && _displayedFrame != null &&
        _displayedFrame.OptionsRevision == _options.Revision &&
        Stopwatch.GetElapsedTime(_displayedReceivedAt) < TimeSpan.FromMilliseconds(1500);

    private bool CanSendInput() => ControlSwitch.IsChecked == true && IsActive && Viewport.IsKeyboardFocusWithin && HasFreshFrame();

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_closed || _faulted) return;
        if (!HasFreshFrame())
        {
            ReleaseControl();
            ControlSwitch.IsEnabled = SendEscapeButton.IsEnabled = false;
            if (_displayedFrame != null) StatusText.Text = "Waiting for current frame · Control released";
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastMetricsAt, now).TotalSeconds;
        if (elapsed < 1) return;
        var totalBytes = _session?.ReceivedBytes ?? 0;
        var bytes = Math.Max(0, totalBytes - _lastReceivedBytes);
        _lastReceivedBytes = totalBytes;
        var size = _displayedFrame == null ? "" : $" · {_displayedFrame.Width}×{_displayedFrame.Height}";
        MetricsText.Text = $"{_displayedCount / elapsed:0.0} displayed FPS · {bytes * 8d / elapsed / 1_000_000:0.0} Mbps{size}";
        _displayedCount = 0;
        _lastMetricsAt = now;
    }

    private void ShowFailure(string message)
    {
        if (_closed || _faulted) return;
        _faulted = true;
        ReleaseControl();
        ControlSwitch.IsEnabled = SendEscapeButton.IsEnabled = ProfileBox.IsEnabled = MonitorBox.IsEnabled = NextMonitorButton.IsEnabled = false;
        DesktopImage.Source = null;
        ConnectingText.Text = "Desktop connection ended";
        ConnectingText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 172, 169));
        MetricsText.Text = "";
        lock (_frameGate) _pendingFrame = null;
        if (_session is { } session)
        {
            session.FrameReceived -= Session_FrameReceived;
            session.Faulted -= Session_Faulted;
            _ = DisposeFailedSessionAsync(session);
        }
    }

    private static async Task DisposeFailedSessionAsync(LanDesktopSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Companion desktop failure cleanup: {ex.GetType().Name}"); }
    }

    private bool Send(RemoteDesktopCommand command)
    {
        if (_session != null && _session.Send(command)) return true;
        ShowFailure("Remote input could not be delivered. Close this window and reopen View & control.");
        return false;
    }

    private void Control_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (!HasFreshFrame() || Keyboard.Modifiers != ModifierKeys.None)
        {
            ReleaseControl();
            StatusText.Text = "Release modifier keys and wait for a current frame, then enable Control.";
            return;
        }
        Viewport.Focus();
        _inputEnabled = true;
        Viewport.BorderBrush = (Brush)FindResource("AccentBrush");
        StatusText.Text = "Control on · Esc releases all remote input";
    }

    private void Control_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_ready) ReleaseControl();
    }

    private void ReleaseControl()
    {
        if (_releasing) return;
        _releasing = true;
        try
        {
            var heldInput = _pressedKeys.Count != 0 || _pressedButtons.Count != 0 || _inputEnabled || ControlSwitch.IsChecked == true;
            _inputEnabled = false;
            _pressedKeys.Clear();
            _pressedButtons.Clear();
            ControlSwitch.IsChecked = false;
            if (heldInput) _session?.ReleaseInput();
            if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
            Viewport.BorderBrush = (Brush)FindResource("BorderBrush");
            if (!_faulted && !_closed && _displayedFrame != null) StatusText.Text = "Viewing · Enable Control for mouse and keyboard · Esc releases input";
        }
        finally { _releasing = false; }
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedButtons.Count != 0) ReleaseControl();
    }

    private void Viewport_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!Viewport.IsKeyboardFocusWithin) ReleaseControl();
    }

    private bool TryGetPointer(MouseEventArgs e, bool allowOutside, out Point normalized)
    {
        normalized = default;
        if (_displayedFrame == null || DesktopImage.Source == null) return false;
        var width = DesktopImage.ActualWidth;
        var height = DesktopImage.ActualHeight;
        var scale = Math.Min(width / _displayedFrame.Width, height / _displayedFrame.Height);
        if (!double.IsFinite(scale) || scale <= 0) return false;
        var renderedWidth = _displayedFrame.Width * scale;
        var renderedHeight = _displayedFrame.Height * scale;
        var point = e.GetPosition(DesktopImage);
        var x = (point.X - (width - renderedWidth) / 2) / renderedWidth;
        var y = (point.Y - (height - renderedHeight) / 2) / renderedHeight;
        if (!allowOutside && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
        normalized = new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        return true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ControlSwitch.IsChecked != true || !HasFreshFrame() || !TryGetPointer(e, false, out var point)) return;
        // Scrollbars remain local in actual-size mode.
        if (e.OriginalSource is DependencyObject source && IsScrollbar(source)) return;
        Viewport.Focus();
        if (!CanSendInput()) return;
        e.Handled = true;
        if (!_pressedButtons.Add(e.ChangedButton)) return;
        if (!Viewport.CaptureMouse()) { ReleaseControl(); return; }
        Send(new RemoteDesktopCommand { Kind = "button", Button = ButtonName(e.ChangedButton), Down = true, X = point.X, Y = point.Y, Sequence = _displayedFrame!.Sequence });
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressedButtons.Remove(e.ChangedButton)) return;
        e.Handled = true;
        if (CanSendInput() && TryGetPointer(e, true, out var point))
            Send(new RemoteDesktopCommand { Kind = "button", Button = ButtonName(e.ChangedButton), Down = false, X = point.X, Y = point.Y, Sequence = _displayedFrame!.Sequence });
        else ReleaseControl();
        if (_pressedButtons.Count == 0 && Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CanSendInput() || !TryGetPointer(e, _pressedButtons.Count != 0, out var point)) return;
        Send(new RemoteDesktopCommand { Kind = "move", X = point.X, Y = point.Y, Sequence = _displayedFrame!.Sequence });
        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanSendInput() || !TryGetPointer(e, false, out var point)) return;
        Send(new RemoteDesktopCommand { Kind = "wheel", WheelDelta = Math.Clamp(e.Delta, -1200, 1200), X = point.X, Y = point.Y, Sequence = _displayedFrame!.Sequence });
        e.Handled = true;
    }

    private static bool IsScrollbar(DependencyObject source)
    {
        for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "left", MouseButton.Right => "right", MouseButton.Middle => "middle", MouseButton.XButton1 => "x1", MouseButton.XButton2 => "x2", _ => ""
    };

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var key = EffectiveKey(e);
        if (key == Key.Escape)
        {
            var wasControlling = ControlSwitch.IsChecked == true;
            ReleaseControl();
            if (!wasControlling && _fullScreen) ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (!CanSendInput()) return;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is < 8 or > 254) return;
        var extended = IsExtended(key, virtualKey);
        e.Handled = true;
        if (e.IsRepeat && !_pressedKeys.Contains((virtualKey, extended))) return;
        _pressedKeys.Add((virtualKey, extended));
        Send(new RemoteDesktopCommand { Kind = "key", VirtualKey = virtualKey, Extended = extended, Down = true, Sequence = _displayedFrame!.Sequence });
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        var key = EffectiveKey(e);
        if (key == Key.Escape) { e.Handled = true; return; }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var extended = IsExtended(key, virtualKey);
        if (!_pressedKeys.Remove((virtualKey, extended))) return;
        e.Handled = true;
        if (!CanSendInput()) { ReleaseControl(); return; }
        Send(new RemoteDesktopCommand { Kind = "key", VirtualKey = virtualKey, Extended = extended, Down = false, Sequence = _displayedFrame!.Sequence });
    }

    private static Key EffectiveKey(KeyEventArgs e) => e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };

    private bool IsExtended(Key key, int virtualKey) => _lastNativeKey == virtualKey ? _lastNativeExtended : key is
        Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown or
        Key.Left or Key.Right or Key.Up or Key.Down or Key.Divide or Key.NumLock or Key.PrintScreen or Key.LWin or Key.RWin or Key.Apps;

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingMonitors || MonitorBox.SelectedItem is not MonitorChoice monitor) return;
        ChangeOptions(_options with { MonitorId = monitor.Id });
    }

    private void NextMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorBox.Items.Count > 1) MonitorBox.SelectedIndex = (MonitorBox.SelectedIndex + 1) % MonitorBox.Items.Count;
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var smooth = ProfileBox.SelectedIndex == 1;
        var sharp = ProfileBox.SelectedIndex == 2;
        ChangeOptions(_options with { MaxFramesPerSecond = smooth ? 60 : 30, MaxWidth = sharp ? 3840 : 1920, JpegQuality = sharp ? 92 : smooth ? 80 : 85 });
    }

    private void ChangeOptions(RemoteDesktopOptions options)
    {
        if (_closed || _faulted || _session == null) return;
        ReleaseControl();
        _options = options with { Revision = _options.Revision + 1 };
        ControlSwitch.IsEnabled = SendEscapeButton.IsEnabled = false;
        _displayedFrame = null;
        DesktopImage.Source = null;
        ConnectingText.Text = "Updating stream…";
        ConnectingText.Visibility = Visibility.Visible;
        StatusText.Text = "Waiting for current stream settings…";
        if (!_session.UpdateOptions(_options)) ShowFailure("Stream settings could not be delivered. Close this window and reopen View & control.");
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        ReleaseControl();
        _actualSize = !_actualSize;
        SizeButton.Content = _actualSize ? "Fit" : "100%";
        ImageScroll.HorizontalScrollBarVisibility = ImageScroll.VerticalScrollBarVisibility = _actualSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ImageScroll.HorizontalContentAlignment = _actualSize ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ImageScroll.VerticalContentAlignment = _actualSize ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        SetImageSize();
    }

    private void SendEscape_Click(object sender, RoutedEventArgs e)
    {
        ReleaseControl();
        if (!IsActive || !HasFreshFrame()) return;
        var sequence = _displayedFrame!.Sequence;
        if (Send(new RemoteDesktopCommand { Kind = "key", VirtualKey = 27, Down = true, Sequence = sequence }))
            Send(new RemoteDesktopCommand { Kind = "key", VirtualKey = 27, Down = false, Sequence = sequence });
    }

    private void SetImageSize()
    {
        var dpi = VisualTreeHelper.GetDpi(DesktopImage);
        DesktopImage.Width = _actualSize && _displayedFrame != null ? _displayedFrame.Width / dpi.DpiScaleX : double.NaN;
        DesktopImage.Height = _actualSize && _displayedFrame != null ? _displayedFrame.Height / dpi.DpiScaleY : double.NaN;
    }

    private void ImageScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0 || e.VerticalChange != 0) ReleaseControl();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        ReleaseControl();
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousWindowState;
        }
        FullscreenButton.Content = _fullScreen ? "Window" : "Full screen";
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            _lastNativeKey = unchecked((int)wParam.ToInt64());
            _lastNativeExtended = (lParam.ToInt64() & (1L << 24)) != 0;
        }
        return IntPtr.Zero;
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        ReleaseControl();
        _closed = true;
        _statusTimer.Stop();
        CompositionTarget.Rendering -= RenderLatestFrame;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        if (_session == null) _lifetime.Cancel();
        if (_session != null)
        {
            _session.FrameReceived -= Session_FrameReceived;
            _session.Faulted -= Session_Faulted;
            try { await _session.DisposeAsync(); }
            catch (Exception ex) { Debug.WriteLine($"Companion desktop close: {ex.GetType().Name}"); }
        }
        lock (_frameGate) _pendingFrame = null;
        DesktopImage.Source = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record MonitorChoice(string Id, string Label);
}
