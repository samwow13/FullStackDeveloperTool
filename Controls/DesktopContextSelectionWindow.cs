using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

/// <summary>Displays frozen pixels only; the native capture backend owns input.</summary>
internal sealed class DesktopContextSelectionWindow : Window
{
    private readonly DesktopCaptureRectangle _bounds;
    private readonly Canvas _canvas;
    private readonly Border[] _shades;
    private readonly Border _selection;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private DesktopCaptureRectangle _rectangle;
    private bool _finished;
    private bool _closed;
    private HwndSource? _source;
    private bool _positioned;
    private uint _displayDpi;
    internal string? FailureMessage { get; private set; }

    internal event Action? SelectionAbandoned;

    internal DesktopContextSelectionWindow(DesktopCaptureFrame frame)
    {
        _bounds = frame.Bounds;
        _rectangle = frame.Selection;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = true;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        Title = "Select screenshot area";
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(new Image { Source = frame.Image, Stretch = Stretch.Fill, SnapsToDevicePixels = true });
        _canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        root.Children.Add(_canvas);
        _shades = Enumerable.Range(0, 4).Select(_ => new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(145, 5, 11, 19))
        }).ToArray();
        foreach (var shade in _shades) _canvas.Children.Add(shade);
        _selection = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(105, 226, 192)),
            BorderThickness = new Thickness(2), Background = Brushes.Transparent
        };
        _canvas.Children.Add(_selection);
        _labelText = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 12 };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 31, 43)), Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(5), Child = _labelText
        };
        _canvas.Children.Add(_label);
        Content = root;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            PositionOnPhysicalDesktop();
            _displayDpi = GetDpiForWindow(new WindowInteropHelper(this).Handle);
            _positioned = true;
            Redraw();
        };
        SizeChanged += (_, _) => Redraw();
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _source?.RemoveHook(WindowMessage); _source = null; };
    }

    internal void UpdateSelection(DesktopCaptureRectangle rectangle)
    {
        _rectangle = rectangle;
        Redraw();
    }

    internal void CloseAfterCapture()
    {
        _finished = true;
        if (!_closed) Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!_finished) SelectionAbandoned?.Invoke();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Native hooks own down/up. Route ordinary mouse movement here so the
        // pointer moves normally without sending drag movement to the app.
        SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x00000080);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
        PositionOnPhysicalDesktop();
    }

    private nint WindowMessage(nint window, int message, nint parameter, nint data, ref bool handled)
    {
        if (message == 0x0084) // WM_NCHITTEST
        {
            handled = true;
            return 1; // HTCLIENT
        }
        if (message == 0x02E0) // WM_DPICHANGED
        {
            var changedDpi = unchecked((uint)parameter.ToInt64()) & 0xffff;
            if (_positioned && changedDpi != _displayDpi)
            {
                FailSelection("Display scale changed. Capture the area again with the current screen settings.");
                return 0;
            }
            // Keep the window on the captured physical virtual desktop even
            // when Windows proposes a single-monitor suggested rectangle.
            _ = Dispatcher.BeginInvoke(new Action(() => { PositionOnPhysicalDesktop(); Redraw(); }));
        }
        if (message == 0x007E && _positioned)
            FailSelection("Display layout changed. Capture the area again with the current screen settings."); // WM_DISPLAYCHANGE
        return 0;
    }

    private void PositionOnPhysicalDesktop()
    {
        if (_closed || _finished) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || FailureMessage is not null) return;
        var previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2 for physical coordinates.
        if (previous == 0)
        {
            FailSelection("Physical screen placement is unavailable. Capture the area again after restarting Launcher.");
            return;
        }
        try
        {
            if (!SetWindowPos(handle, -1, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0x0010 | 0x0040) ||
                !GetWindowRect(handle, out var actual) || actual.Left != _bounds.Left || actual.Top != _bounds.Top ||
                actual.Right != _bounds.Right || actual.Bottom != _bounds.Bottom)
                FailSelection("The selection overlay could not cover the physical desktop. Capture the area again with stable display settings.");
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private void FailSelection(string message)
    {
        if (FailureMessage is not null) return;
        FailureMessage = message;
        SelectionAbandoned?.Invoke();
    }

    private void Redraw()
    {
        if (_bounds.Width < 1 || _bounds.Height < 1 || ActualWidth <= 0 || ActualHeight <= 0) return;
        // Derive independent scale factors from the actual window client area;
        // never assume 96 DPI, a primary monitor origin, or one monitor scale.
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var scaleX = width / _bounds.Width;
        var scaleY = height / _bounds.Height;
        var left = Math.Clamp((_rectangle.Left - _bounds.Left) * scaleX, 0, width);
        var top = Math.Clamp((_rectangle.Top - _bounds.Top) * scaleY, 0, height);
        var right = Math.Clamp((_rectangle.Right - _bounds.Left) * scaleX, left, width);
        var bottom = Math.Clamp((_rectangle.Bottom - _bounds.Top) * scaleY, top, height);
        Place(_selection, left, top, right - left, bottom - top);
        Place(_shades[0], 0, 0, width, top);
        Place(_shades[1], 0, bottom, width, height - bottom);
        Place(_shades[2], 0, top, left, bottom - top);
        Place(_shades[3], right, top, width - right, bottom - top);
        _labelText.Text = $"{_rectangle.Width:N0} × {_rectangle.Height:N0} px · Release to review · Esc to cancel";
        _label.Measure(new Size(width, height));
        Canvas.SetLeft(_label, Math.Clamp(left, 0, Math.Max(0, width - _label.DesiredSize.Width)));
        Canvas.SetTop(_label, Math.Clamp(top > 40 ? top - _label.DesiredSize.Height - 8 : bottom + 8,
            0, Math.Max(0, height - _label.DesiredSize.Height)));
    }

    private static void Place(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int left, int top, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);
}
