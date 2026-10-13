using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FullStackLauncher.Services;

/// <summary>Applies the dashboard's native caption theme to every application window.</summary>
internal static class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int LegacyUseImmersiveDarkMode = 19;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int SystemColor = unchecked((int)0xFFFFFFFF);
    private const uint RefreshFrame = 0x0037; // FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE.
    private const uint RedrawFrame = 0x0541; // INVALIDATE | FRAME | UPDATENOW | NOCHILDREN; no background erase.
    private static readonly ConditionalWeakTable<Window, CaptionRegistration> Registrations = new();
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        // Constructors attach before HWND creation. Loaded remains a fallback for new
        // window types, independent of their local styles.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(WindowLoaded), handledEventsToo: true);
        // WPF creates private editor menu subclasses for spelling, IME and text commands.
        // Those subclasses do not automatically resolve our implicit base-type styles.
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(EditorMenuLoaded), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(EditorMenuLoaded), handledEventsToo: true);
    }

    internal static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        Registrations.GetValue(window, static current => new CaptionRegistration(current));
    }

    private static void EditorMenuLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement control || !ReferenceEquals(args.OriginalSource, control) ||
            control.Style is not null) return;
        var styleKey = control is ContextMenu ? typeof(ContextMenu) : typeof(MenuItem);
        if (control.TryFindResource(styleKey) is Style) control.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
    }

    private static void WindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window)) return;
        if (window.AllowsTransparency || window.WindowStyle == WindowStyle.None) return;
        Attach(window);
        Registrations.GetValue(window, static current => new CaptionRegistration(current)).Apply();
    }

    private sealed class CaptionRegistration
    {
        private readonly Window _window;
        private HwndSource? _source;
        private bool _closed;
        private bool _refreshPending;
        private bool _applying;

        internal CaptionRegistration(Window window)
        {
            _window = window;
            window.SourceInitialized += WindowSourceInitialized;
            window.Activated += WindowActivated;
            window.Closed += WindowClosed;
            Apply();
        }

        private void WindowSourceInitialized(object? sender, EventArgs args) => Apply();

        internal void Apply()
        {
            if (_closed || _applying || _window.AllowsTransparency ||
                _window.WindowStyle == WindowStyle.None) return;
            if (_source is null)
            {
                var existingHandle = new WindowInteropHelper(_window).Handle;
                if (existingHandle == IntPtr.Zero) return;
                _source = HwndSource.FromHwnd(existingHandle);
                if (_source is null) return;
                _source.AddHook(WindowMessage);
            }
            if (_source.IsDisposed) return;
            var handle = _source.Handle;
            _applying = true;
            try
            {
                var enabled = SystemParameters.HighContrast ? 0 : 1;
                if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, LegacyUseImmersiveDarkMode, ref enabled, sizeof(int));

                // Explicit Windows 11 colors keep active and inactive captions consistent.
                // Older Windows ignores unsupported attributes and retains dark-mode chrome.
                var caption = SystemParameters.HighContrast ? SystemColor : 0x00000000;
                var text = SystemParameters.HighContrast ? SystemColor : 0x00FFFFFF;
                DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));

                // Refresh the cached native frame before first display, without changing
                // focus, position, size or visibility. Attribute changes alone can leave
                // an already-painted light caption until the next interaction.
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, RefreshFrame);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RedrawFrame);
            }
            finally { _applying = false; }
        }

        private void WindowActivated(object? sender, EventArgs args) => Apply();

        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Reapply after OS theme changes and native caption/full-screen transitions.
            if ((message is 0x001A or 0x007D or 0x031A or 0x031E or 0x0320) && !_refreshPending && !_closed && !_applying)
            {
                _refreshPending = true;
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _refreshPending = false;
                    Apply();
                }));
            }
            return IntPtr.Zero;
        }

        private void WindowClosed(object? sender, EventArgs args)
        {
            _closed = true;
            _window.SourceInitialized -= WindowSourceInitialized;
            _window.Activated -= WindowActivated;
            _window.Closed -= WindowClosed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
            Registrations.Remove(_window);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);
}
