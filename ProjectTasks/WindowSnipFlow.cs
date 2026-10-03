using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FullStackLauncher.ProjectTasks;

internal sealed record SnipCaptureResult(BitmapSource Image, BrowserPageContext? PageContext);

/// <summary>Captures a user-approved rectangle from a selected, visible desktop window.</summary>
internal static class WindowSnipFlow
{
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(20, 29, 41));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(237, 243, 249));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(178, 194, 207));

    public static async Task<SnipCaptureResult?> CaptureAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        while (true)
        {
            var picker = new WindowPicker(owner);
            if (picker.ShowDialog() != true || picker.SelectedTarget is not { } selection) return null;
            var target = selection.Window;

            // Keep the editor's ShowDialog lifetime intact while suppressing every
            // launcher window from the screen capture.
            var suppressed = System.Windows.Application.Current.Windows.Cast<Window>()
                .Where(window => window.IsVisible && !ReferenceEquals(window, picker))
                .Select(window => (Window: window, Opacity: window.Opacity)).ToArray();
            try
            {
                foreach (var (window, _) in suppressed) window.Opacity = 0;
                EnsureTarget(target);
                if (IsIconic(target.Handle)) ShowWindowAsync(target.Handle, 9); // SW_RESTORE
                await ActivateTabAsync(selection);
                SetForegroundWindow(target.Handle);

                var decision = await ConfirmTargetAsync(selection, owner);
                if (decision == TargetDecision.Cancel) return null;
                if (decision == TargetDecision.Back) continue;

                EnsureTarget(target);
                if (IsIconic(target.Handle)) ShowWindowAsync(target.Handle, 9);
                await ActivateTabAsync(selection);
                SetForegroundWindow(target.Handle);
                await Task.Delay(200); // Let the confirmation panel disappear from the screen.
                if (!owner.IsVisible) return null;
                EnsureTarget(target);
                if (GetForegroundWindow() != target.Handle)
                    throw new InvalidOperationException("The selected window could not be brought to the foreground. Select it and retry the snip.");
                if (selection.Tab is { } selectedTab &&
                    !await Task.Run(() => BrowserTabAccess.IsSelected(selectedTab)))
                    throw new InvalidOperationException("The selected browser tab changed before capture. Choose it again.");

                var bounds = GetVisibleBounds(target.Handle);
                var screenshot = CaptureScreen(bounds);
                BrowserPageContext? pageContext = null;
                if (target.IsBrowser)
                {
                    // The browser stays foreground here. Capture live DOM/CSS before
                    // the region selector and review windows take focus away.
                    var activeTab = selection.Tab;
                    if (activeTab == null)
                    {
                        var selectedTabs = await Task.Run(() => BrowserTabAccess.ListTabs(target.Handle)
                            .Where(tab => tab.IsSelected).Take(2).ToArray());
                        if (selectedTabs.Length == 1) activeTab = selectedTabs[0];
                    }
                    if (activeTab == null)
                    {
                        pageContext = new BrowserPageContext("", "", "",
                            "Browser source unavailable. The active tab could not be identified.");
                    }
                    else
                    {
                        var address = await Task.Run(() => BrowserTabAccess.TryGetAddressUrl(target.Handle));
                        GetWindowRect(target.Handle, out var windowRectangle);
                        var windowBounds = new BrowserWindowBounds(windowRectangle.Left, windowRectangle.Top,
                            windowRectangle.Width, windowRectangle.Height, (int)GetDpiForWindow(target.Handle));
                        pageContext = await BrowserPageCaptureBroker.CaptureAsync(
                            target.ProcessName, activeTab.Title, address, windowBounds);
                        if (!string.IsNullOrEmpty(pageContext.Html) &&
                            (GetForegroundWindow() != target.Handle ||
                             !await Task.Run(() => BrowserTabAccess.IsSelected(activeTab))))
                            pageContext = new BrowserPageContext("", "", "",
                                "Browser tab changed during page source capture. Snip the tab again.");
                    }
                }
                if (!owner.IsVisible) return null;
                while (true)
                {
                    var snip = new RegionSelector(screenshot, bounds).Select();
                    if (snip == null || !owner.IsVisible) return null;
                    var review = new SnipReview(snip).Review();
                    if (review == ReviewDecision.Use) return new SnipCaptureResult(snip, pageContext);
                    if (review == ReviewDecision.Cancel) return null;
                }
            }
            finally
            {
                foreach (var (window, opacity) in suppressed) window.Opacity = opacity;
                if (owner.IsVisible) owner.Activate();
            }
        }
    }

    private static async Task ActivateTabAsync(SnipTargetSelection selection)
    {
        if (selection.Tab is not { } tab) return;
        if (!await Task.Run(() => BrowserTabAccess.TryActivate(tab)))
            throw new InvalidOperationException("The selected browser tab is no longer available. Choose it again.");
    }

    private static List<WindowCandidate> ListWindows(string? lastSelectedApp = null)
    {
        var windows = new List<WindowCandidate>();
        var ownProcessId = Environment.ProcessId;
        using var ownProcess = Process.GetCurrentProcess();
        var ownProcessName = ownProcess.ProcessName;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var threadId = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || threadId == 0 || processId == ownProcessId) return true;
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = ReadWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title)) return true;
            var className = ReadWindowClass(handle);
            if (className.Length == 0) return true;
            if (!IsIconic(handle) &&
                (!GetWindowRect(handle, out var rectangle) || rectangle.Width < 120 || rectangle.Height < 80)) return true;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (process.HasExited) return true;
                var name = process.ProcessName;
                if (name.Equals(ownProcessName, StringComparison.OrdinalIgnoreCase)) return true;
                windows.Add(new WindowCandidate(handle, processId, threadId,
                    process.StartTime.ToUniversalTime().Ticks, className, name, title));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
            {
                // Inaccessible or exiting windows cannot be revalidated safely.
            }
            return true;
        }, IntPtr.Zero);
        return windows.OrderBy(window => string.Equals(window.ProcessName, lastSelectedApp, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void EnsureTarget(WindowCandidate target)
    {
        if (!IsWindow(target.Handle) || !IsWindowVisible(target.Handle))
            throw new InvalidOperationException("The selected window is no longer open. Choose a window again.");
        var threadId = GetWindowThreadProcessId(target.Handle, out var processId);
        if (processId != target.ProcessId || threadId != target.ThreadId ||
            ReadWindowClass(target.Handle) != target.ClassName)
            throw new InvalidOperationException("The selected window changed. Choose a window again.");
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartTicks)
                throw new InvalidOperationException("The selected window changed. Choose a window again.");
        }
        catch (Exception error) when (error is ArgumentException or Win32Exception)
        {
            throw new InvalidOperationException("The selected window is no longer available. Choose a window again.", error);
        }
    }

    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return "";
        var title = new StringBuilder(Math.Min(length + 1, 1024));
        GetWindowText(handle, title, title.Capacity);
        return title.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string ReadWindowClass(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static NativeRect GetVisibleBounds(IntPtr handle)
    {
        // DWM frame bounds use physical pixels and exclude invisible resize borders.
        if (DwmGetWindowAttribute(handle, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0 ||
            bounds.Width <= 0 || bounds.Height <= 0)
        {
            if (!GetWindowRect(handle, out bounds))
                throw new InvalidOperationException("The selected window has no readable screen position.");
        }
        var desktop = Forms.SystemInformation.VirtualScreen;
        bounds.Left = Math.Max(bounds.Left, desktop.Left);
        bounds.Top = Math.Max(bounds.Top, desktop.Top);
        bounds.Right = Math.Min(bounds.Right, desktop.Right);
        bounds.Bottom = Math.Min(bounds.Bottom, desktop.Bottom);
        if (bounds.Width < 4 || bounds.Height < 4 || (long)bounds.Width * bounds.Height > 100_000_000)
            throw new InvalidOperationException("The selected window has no usable visible area or is too large to capture.");
        return bounds;
    }

    private static BitmapSource CaptureScreen(NativeRect bounds)
    {
        try
        {
            using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Drawing.Size(bounds.Width, bounds.Height));
            var bitmapHandle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(bitmapHandle); }
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or Win32Exception or ExternalException)
        {
            throw new InvalidOperationException("The selected window could not be captured. Check that it is visible and try again.", error);
        }
    }

    private static async Task<TargetDecision> ConfirmTargetAsync(SnipTargetSelection selection, Window owner)
    {
        var target = selection.Window;
        var completion = new TaskCompletionSource<TargetDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new Window
        {
            Title = "Confirm snip target", Width = 420, Height = 220,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Background = PanelBrush, Foreground = TextBrush,
            FontFamily = new FontFamily("Segoe UI"), WindowStartupLocation = WindowStartupLocation.Manual
        };
        var layout = new DockPanel { Margin = new Thickness(15) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var start = ActionButton("Start snip", true);
        var back = ActionButton("Choose another window");
        var cancel = ActionButton("Cancel");
        start.Click += (_, _) => { completion.TrySetResult(TargetDecision.Start); panel.Close(); };
        back.Click += (_, _) => { completion.TrySetResult(TargetDecision.Back); panel.Close(); };
        cancel.Click += (_, _) => { completion.TrySetResult(TargetDecision.Cancel); panel.Close(); };
        actions.Children.Add(back);
        actions.Children.Add(cancel);
        actions.Children.Add(start);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "Check the window before snipping", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var selected = new TextBlock { Text = selection.Tab?.Title ?? target.Title, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 10) };
        text.Children.Add(selected);
        text.Children.Add(new TextBlock
        {
            Text = selection.Tab != null
                ? "Check this tab's content. The selected tab will be shown again before capture."
                : target.IsBrowser
                    ? "Choose the correct browser tab now. Then click Start snip to capture the visible tab."
                    : "Bring the content you want into view. Then click Start snip to select an area.",
            TextWrapping = TextWrapping.Wrap, Foreground = TextBrush
        });
        layout.Children.Add(text);
        panel.Content = layout;
        panel.SourceInitialized += (_, _) =>
        {
            // Positioning is best effort. A target can close while this window opens;
            // the explicit pre-capture validation reports that change safely.
            if (!GetWindowRect(target.Handle, out var bounds)) return;
            var desktop = Forms.SystemInformation.VirtualScreen;
            bounds.Left = Math.Max(bounds.Left, desktop.Left);
            bounds.Top = Math.Max(bounds.Top, desktop.Top);
            bounds.Right = Math.Min(bounds.Right, desktop.Right);
            bounds.Bottom = Math.Min(bounds.Bottom, desktop.Bottom);
            if (bounds.Width < 1 || bounds.Height < 1) return;
            var dpi = Math.Max(1.0, GetDpiForWindow(new WindowInteropHelper(panel).Handle) / 96.0);
            var width = (int)Math.Ceiling(panel.Width * dpi);
            var height = (int)Math.Ceiling(panel.Height * dpi);
            SetWindowPos(new WindowInteropHelper(panel).Handle, new IntPtr(-1),
                Math.Max(bounds.Left, bounds.Right - width - 18),
                Math.Max(bounds.Top, bounds.Bottom - height - 18), width, height, 0x0040);
        };
        var titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        titleTimer.Tick += (_, _) =>
        {
            if (!IsWindow(target.Handle)) { completion.TrySetResult(TargetDecision.Cancel); panel.Close(); }
            else if (selection.Tab == null) selected.Text = ReadWindowTitle(target.Handle);
        };
        panel.Loaded += (_, _) => titleTimer.Start();
        EventHandler ownerClosed = (_, _) =>
        {
            completion.TrySetResult(TargetDecision.Cancel);
            if (panel.IsVisible) panel.Close();
        };
        owner.Closed += ownerClosed;
        panel.Closed += (_, _) =>
        {
            owner.Closed -= ownerClosed;
            titleTimer.Stop();
            completion.TrySetResult(TargetDecision.Cancel);
        };
        if (!owner.IsVisible) { owner.Closed -= ownerClosed; return TargetDecision.Cancel; }
        panel.Show(); // Modeless: Chrome remains clickable while this panel stays visible.
        return await completion.Task;
    }

    private static Button ActionButton(string label, bool primary = false) => new()
    {
        Content = label, MinHeight = 31, Padding = new Thickness(9, 4, 9, 4),
        Margin = new Thickness(5, 0, 0, 0), Foreground = TextBrush,
        Background = primary ? new SolidColorBrush(Color.FromRgb(34, 103, 92)) : new SolidColorBrush(Color.FromRgb(41, 55, 75))
    };

    private sealed record WindowCandidate(IntPtr Handle, uint ProcessId, uint ThreadId, long StartTicks,
        string ClassName, string ProcessName, string Title)
    {
        public bool IsBrowser => ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                                 ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase);
        public string BrowserName => ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";
        public string Display => $"{ProcessName}  ·  {Title}";
    }

    private sealed record SnipTargetSelection(WindowCandidate Window, BrowserTabCandidate? Tab);

    private sealed record BrowserTabRow(WindowCandidate Window, BrowserTabCandidate Tab, int WindowNumber)
    {
        public string Display => $"{(Tab.IsSelected ? "Active  ·  " : "")}{Tab.Title}  ·  {Window.BrowserName} window {WindowNumber}: {Window.Title}";
    }

    private sealed record SavedTargetRow(SavedSnipTarget Target)
    {
        public string Display => Target.TabTitle is { } title
            ? $"{Target.ProcessName} tab  ·  {title}"
            : $"{Target.ProcessName}  ·  {Target.WindowTitle}";
    }

    private sealed class WindowPicker : Window
    {
        private readonly SnipTargetStore _store = new();
        private readonly ListBox _list = CreatePickerList("Windows and browser tabs available to snip");
        private readonly ListBox _savedList = CreatePickerList("Saved snip targets");
        private readonly TextBlock _heading = new() { Text = "Choose the window to snip", FontSize = 19, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _count = new() { Foreground = MutedBrush, Margin = new Thickness(0, 7, 0, 11) };
        private readonly TextBlock _hint = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        private readonly TextBlock _savedHeading = new() { Foreground = TextBrush, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _savedEmpty = new() { Text = "Select a window or tab, then choose Save selected.", Foreground = MutedBrush, Margin = new Thickness(2, 6, 0, 0) };
        private readonly Button _back = ActionButton("Back to windows");
        private readonly Button _showTabs = ActionButton("Show tabs");
        private readonly Button _save = ActionButton("Save selected");
        private readonly Button _remove = ActionButton("Remove saved");
        private readonly Button _choose = ActionButton("Use selected", true);
        private string? _tabBrowser;
        private bool _busy;
        public SnipTargetSelection? SelectedTarget { get; private set; }

        public WindowPicker(Window owner)
        {
            Owner = owner;
            Title = "Snip Image · Choose window";
            Width = 750; Height = 540; MinWidth = 500; MinHeight = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = PanelBrush; Foreground = TextBrush; FontFamily = new FontFamily("Segoe UI");
            var layout = new DockPanel { Margin = new Thickness(18) };
            var header = new StackPanel();
            header.Children.Add(_heading);
            header.Children.Add(_hint);
            header.Children.Add(_count);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);

            var saved = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var savedToolbar = new DockPanel();
            DockPanel.SetDock(_remove, Dock.Right);
            savedToolbar.Children.Add(_remove);
            savedToolbar.Children.Add(_savedHeading);
            saved.Children.Add(savedToolbar);
            saved.Children.Add(_savedEmpty);
            _savedList.MaxHeight = 126;
            _savedList.Margin = new Thickness(0, 7, 0, 0);
            saved.Children.Add(_savedList);
            DockPanel.SetDock(saved, Dock.Top);
            layout.Children.Add(saved);

            var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 11, 0, 0) };
            var refresh = ActionButton("Refresh list");
            var cancel = ActionButton("Cancel");
            refresh.Click += async (_, _) => await RefreshListAsync();
            cancel.Click += (_, _) => Close();
            _back.Click += async (_, _) => { _tabBrowser = null; await RefreshListAsync(); };
            _showTabs.Click += async (_, _) =>
            {
                if (_list.SelectedItem is WindowCandidate { IsBrowser: true } browser)
                    await OpenTabsAsync(browser);
            };
            _save.Click += (_, _) => SaveSelected();
            _remove.Click += (_, _) => RemoveSaved();
            _choose.Click += async (_, _) => await UseSelectedAsync();
            _list.SelectionChanged += (_, e) =>
            {
                if (e.AddedItems.Count > 0) _savedList.SelectedItem = null;
                UpdateButtons();
            };
            _savedList.SelectionChanged += (_, e) =>
            {
                if (e.AddedItems.Count > 0) _list.SelectedItem = null;
                UpdateButtons();
            };
            _list.MouseDoubleClick += async (_, e) =>
            {
                if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
                if (item.Content is WindowCandidate { IsBrowser: true } browser)
                    await OpenTabsAsync(browser);
                else await UseSelectedAsync();
            };
            _list.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (_list.SelectedItem is WindowCandidate { IsBrowser: true } browser)
                    await OpenTabsAsync(browser);
                else await UseSelectedAsync();
            };
            _savedList.MouseDoubleClick += async (_, e) =>
            {
                if (ItemsControl.ContainerFromElement(_savedList, e.OriginalSource as DependencyObject) is ListBoxItem)
                    await UseSelectedAsync();
            };
            _savedList.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await UseSelectedAsync();
            };
            actions.Children.Add(_back);
            actions.Children.Add(_showTabs);
            actions.Children.Add(refresh);
            actions.Children.Add(_save);
            actions.Children.Add(cancel);
            actions.Children.Add(_choose);
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            layout.Children.Add(_list);
            Content = layout;
            RefreshSavedList();
            UpdateButtons();
            Loaded += async (_, _) => await RefreshListAsync();
        }

        private async Task OpenTabsAsync(WindowCandidate browser)
        {
            if (_busy) return;
            _tabBrowser = browser.ProcessName;
            await RefreshListAsync();
        }

        private static ListBox CreatePickerList(string accessibilityName)
        {
            var list = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 38, 53)),
                Foreground = TextBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5),
                AlternationCount = 2,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(list, accessibilityName);
            var itemText = new FrameworkElementFactory(typeof(TextBlock));
            itemText.SetBinding(TextBlock.TextProperty, new Binding("Display"));
            itemText.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Display"));
            itemText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var itemTemplate = new DataTemplate { VisualTree = itemText };
            list.ItemTemplate = itemTemplate;

            var template = new ControlTemplate(typeof(ListBoxItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 10, 12, 10));
            border.AppendChild(content);
            template.VisualTree = border;

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(31, 45, 61))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1)));
            var odd = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            odd.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 55, 73))));
            style.Triggers.Add(odd);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(54, 77, 99))));
            style.Triggers.Add(hover);
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(36, 101, 91))));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Triggers.Add(selected);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(132, 231, 212))));
            style.Triggers.Add(focus);
            list.ItemContainerStyle = style;
            return list;
        }

        private void RefreshSavedList()
        {
            var rows = _store.SavedTargets.Select(target => new SavedTargetRow(target)).ToList();
            _savedList.ItemsSource = rows;
            _savedList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _savedEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _savedHeading.Text = $"Saved targets ({rows.Count})";
        }

        private async Task RefreshListAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                if (_tabBrowser is not { } browser)
                {
                    var lastApp = _store.LastSelectedApp;
                    var windows = await Task.Run(() => ListWindows(lastApp));
                    _list.ItemsSource = windows;
                    _heading.Text = "Choose the window to snip";
                    _hint.Text = "Double-click Chrome or Edge to see tabs from every open window. Save any window or tab for later.";
                    _count.Text = windows.Count == 0 ? "No available windows. Open a window, then refresh." : $"{windows.Count} available windows";
                    Title = "Snip Image · Choose window";
                }
                else
                {
                    var result = await Task.Run(() => ListBrowserTabs(browser));
                    _list.ItemsSource = result.Tabs;
                    var browserName = browser.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";
                    _heading.Text = $"Choose a {browserName} tab to snip";
                    _hint.Text = $"Select a {browserName} tab, then use or save it. Available tabs from all open {browserName} windows appear here.";
                    _count.Text = result.Tabs.Count == 0
                        ? $"No {browserName} tabs were available. Refresh, or return to windows to capture the visible tab."
                        : $"{result.Tabs.Count} tabs across {result.WindowCount} {browserName} windows" +
                          (result.UnavailableWindowCount > 0
                              ? $" · {result.UnavailableWindowCount} {(result.UnavailableWindowCount == 1 ? "window" : "windows")} did not expose tabs"
                              : "");
                    Title = $"Snip Image · {browserName} tabs";
                }
            }
            catch (Exception)
            {
                _list.ItemsSource = null;
                _count.Text = "The window list could not be refreshed. Try again.";
            }
            finally { SetBusy(false); }
        }

        private static (List<BrowserTabRow> Tabs, int WindowCount, int UnavailableWindowCount) ListBrowserTabs(string browser)
        {
            var windows = ListWindows().Where(window => window.ProcessName.Equals(browser, StringComparison.OrdinalIgnoreCase)).ToList();
            var rows = new List<BrowserTabRow>();
            var unavailable = 0;
            for (var index = 0; index < windows.Count; index++)
            {
                try
                {
                    var tabs = BrowserTabAccess.ListTabs(windows[index].Handle);
                    if (tabs.Count == 0) unavailable++;
                    foreach (var tab in tabs)
                        rows.Add(new BrowserTabRow(windows[index], tab, index + 1));
                }
                catch (Exception)
                {
                    // One inaccessible browser window must not hide tabs from another.
                    unavailable++;
                }
            }
            return (rows, windows.Count, unavailable);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _list.IsEnabled = !busy;
            _savedList.IsEnabled = !busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _back.Visibility = _tabBrowser == null ? Visibility.Collapsed : Visibility.Visible;
            _back.IsEnabled = !_busy;
            _showTabs.Visibility = _tabBrowser == null ? Visibility.Visible : Visibility.Collapsed;
            _showTabs.IsEnabled = !_busy && _list.SelectedItem is WindowCandidate { IsBrowser: true };
            _save.IsEnabled = !_busy && _list.SelectedItem is WindowCandidate or BrowserTabRow;
            _remove.IsEnabled = !_busy && _savedList.SelectedItem is SavedTargetRow;
            _choose.IsEnabled = !_busy && (_list.SelectedItem is WindowCandidate or BrowserTabRow || _savedList.SelectedItem is SavedTargetRow);
            _choose.Content = _list.SelectedItem is BrowserTabRow || _savedList.SelectedItem is SavedTargetRow { Target.TabTitle: not null }
                ? "Use selected tab" : "Use selected window";
        }

        private void SaveSelected()
        {
            var selection = _list.SelectedItem switch
            {
                WindowCandidate window => new SnipTargetSelection(window, null),
                BrowserTabRow row => new SnipTargetSelection(row.Window, row.Tab),
                _ => null
            };
            if (selection == null) return;
            var targetWindow = selection.Window;
            try
            {
                _store.Add(new SavedSnipTarget(Guid.NewGuid(), targetWindow.ProcessName, targetWindow.Title,
                    targetWindow.ClassName, selection.Tab?.Title, targetWindow.Handle.ToInt64(), targetWindow.ProcessId,
                    targetWindow.ThreadId, targetWindow.StartTicks, selection.Tab?.RuntimeId.ToArray()));
                RefreshSavedList();
                _count.Text = selection.Tab == null ? "Window saved. Select it under Saved targets later." : "Tab saved. Select it under Saved targets later.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _count.Text = $"Could not save target: {error.Message}";
            }
        }

        private void RemoveSaved()
        {
            if (_savedList.SelectedItem is not SavedTargetRow row) return;
            try
            {
                _store.Remove(row.Target.Id);
                RefreshSavedList();
                _count.Text = "Saved target removed.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _count.Text = $"Could not remove saved target: {error.Message}";
            }
        }

        private async Task UseSelectedAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                SnipTargetSelection? selection = _list.SelectedItem switch
                {
                    WindowCandidate window => new SnipTargetSelection(window, null),
                    BrowserTabRow row => new SnipTargetSelection(row.Window, row.Tab),
                    _ => null
                };
                if (_savedList.SelectedItem is SavedTargetRow saved)
                    selection = await Task.Run(() => ResolveSavedTarget(saved.Target));
                if (selection == null) return;
                try { _store.RecordLastSelectedApp(selection.Window.ProcessName); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    MessageBox.Show(this, $"The selected app could not be remembered: {error.Message}",
                        "Snip Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                SelectedTarget = selection;
                DialogResult = true;
            }
            catch (InvalidOperationException error) { _count.Text = error.Message; }
            finally { if (IsVisible) SetBusy(false); }
        }

        private static SnipTargetSelection ResolveSavedTarget(SavedSnipTarget saved)
        {
            var windows = ListWindows().Where(window =>
                window.ProcessName.Equals(saved.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                window.ClassName.Equals(saved.WindowClass, StringComparison.Ordinal)).ToList();
            var exact = windows.FirstOrDefault(window => window.Handle.ToInt64() == saved.WindowHandle &&
                window.ProcessId == saved.ProcessId && window.ThreadId == saved.ThreadId &&
                window.StartTicks == saved.StartTicks);
            if (saved.TabTitle == null)
            {
                if (exact != null) return new SnipTargetSelection(exact, null);
                var matches = windows.Where(window => window.Title.Equals(saved.WindowTitle, StringComparison.Ordinal)).ToList();
                return matches.Count == 1
                    ? new SnipTargetSelection(matches[0], null)
                    : throw new InvalidOperationException(matches.Count == 0
                        ? "The saved window is not open. Select a current window instead."
                        : "Several windows match this saved target. Select the current window directly.");
            }

            if (exact != null)
            {
                var tabs = BrowserTabAccess.ListTabs(exact.Handle);
                if (saved.TabRuntimeId is { Length: > 0 } runtimeId)
                {
                    var sameTab = tabs.Where(tab => tab.RuntimeId.SequenceEqual(runtimeId)).ToList();
                    if (sameTab.Count == 1) return new SnipTargetSelection(exact, sameTab[0]);
                }
                var sameWindow = tabs
                    .Where(tab => tab.Title.Equals(saved.TabTitle, StringComparison.Ordinal)).ToList();
                if (sameWindow.Count == 1) return new SnipTargetSelection(exact, sameWindow[0]);
                if (sameWindow.Count > 1)
                    throw new InvalidOperationException("Several tabs in the saved window have this title. Select the current tab directly.");
            }
            var matchesAcrossWindows = new List<SnipTargetSelection>();
            foreach (var window in windows)
            {
                if (exact != null && window.Handle == exact.Handle) continue;
                try
                {
                    matchesAcrossWindows.AddRange(BrowserTabAccess.ListTabs(window.Handle)
                        .Where(tab => tab.Title.Equals(saved.TabTitle, StringComparison.Ordinal))
                        .Select(tab => new SnipTargetSelection(window, tab)));
                }
                catch (Exception) { /* An unavailable window cannot resolve a saved tab. */ }
            }
            return matchesAcrossWindows.Count == 1
                ? matchesAcrossWindows[0]
                : throw new InvalidOperationException(matchesAcrossWindows.Count == 0
                    ? "The saved tab is not open or its title changed. Select a current tab instead."
                    : "Several tabs match this saved title. Select the current tab directly.");
        }
    }

    private sealed class RegionSelector : Window
    {
        private readonly BitmapSource _screenshot;
        private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
        private readonly Rectangle _outline = new()
        {
            Stroke = Brushes.White, StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(55, 132, 231, 212)), Visibility = Visibility.Collapsed
        };
        private Point? _origin;
        private BitmapSource? _selection;
        private bool _positionFailed;

        public RegionSelector(BitmapSource screenshot, NativeRect bounds)
        {
            _screenshot = screenshot;
            Title = "Snip area"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; Background = Brushes.Black; Opacity = 0;
            Cursor = Cursors.Cross; WindowStartupLocation = WindowStartupLocation.Manual;
            Width = bounds.Width * 96.0 / Math.Max(96u, GetDpiForWindow(GetForegroundWindow()));
            Height = bounds.Height * 96.0 / Math.Max(96u, GetDpiForWindow(GetForegroundWindow()));
            var grid = new Grid();
            grid.Children.Add(new System.Windows.Controls.Image { Source = screenshot, Stretch = Stretch.Fill, IsHitTestVisible = false });
            _canvas.Children.Add(_outline);
            grid.Children.Add(_canvas);
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 29, 41)), Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12), IsHitTestVisible = false,
                Child = new TextBlock { Text = "Drag to select an area · Esc cancels", Foreground = Brushes.White }
            };
            grid.Children.Add(hint);
            Content = grid;
            Loaded += (_, _) =>
            {
                if (!SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1),
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040))
                {
                    _positionFailed = true;
                    Dispatcher.BeginInvoke(new Action(Close));
                }
                else Opacity = 1;
            };
            _canvas.MouseLeftButtonDown += OnSelectionMouseDown;
            _canvas.MouseMove += OnSelectionMouseMove;
            _canvas.MouseLeftButtonUp += OnSelectionMouseUp;
            PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        }

        public BitmapSource? Select()
        {
            ShowDialog();
            if (_positionFailed) throw new InvalidOperationException("The snip overlay could not be positioned over the selected window.");
            return _selection;
        }

        private void OnSelectionMouseDown(object sender, MouseButtonEventArgs args)
        {
            _origin = args.GetPosition(_canvas);
            _outline.Visibility = Visibility.Visible;
            _canvas.CaptureMouse();
            Draw(_origin.Value, _origin.Value);
        }

        private void OnSelectionMouseMove(object sender, MouseEventArgs args)
        {
            if (_origin is { } origin && args.LeftButton == MouseButtonState.Pressed)
                Draw(origin, args.GetPosition(_canvas));
        }

        private void OnSelectionMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (_origin is not { } origin) return;
            var end = args.GetPosition(_canvas);
            _origin = null;
            _canvas.ReleaseMouseCapture();
            var x = Math.Clamp(Math.Min(origin.X, end.X), 0, _canvas.ActualWidth);
            var y = Math.Clamp(Math.Min(origin.Y, end.Y), 0, _canvas.ActualHeight);
            var right = Math.Clamp(Math.Max(origin.X, end.X), 0, _canvas.ActualWidth);
            var bottom = Math.Clamp(Math.Max(origin.Y, end.Y), 0, _canvas.ActualHeight);
            if (right - x < 4 || bottom - y < 4) { _outline.Visibility = Visibility.Collapsed; return; }
            var pixelX = Math.Clamp((int)Math.Floor(x * _screenshot.PixelWidth / _canvas.ActualWidth), 0, _screenshot.PixelWidth - 1);
            var pixelY = Math.Clamp((int)Math.Floor(y * _screenshot.PixelHeight / _canvas.ActualHeight), 0, _screenshot.PixelHeight - 1);
            var pixelRight = Math.Clamp((int)Math.Ceiling(right * _screenshot.PixelWidth / _canvas.ActualWidth), pixelX + 1, _screenshot.PixelWidth);
            var pixelBottom = Math.Clamp((int)Math.Ceiling(bottom * _screenshot.PixelHeight / _canvas.ActualHeight), pixelY + 1, _screenshot.PixelHeight);
            var crop = new CroppedBitmap(_screenshot, new Int32Rect(pixelX, pixelY, pixelRight - pixelX, pixelBottom - pixelY));
            crop.Freeze();
            _selection = crop;
            Close();
        }

        private void Draw(Point start, Point end)
        {
            Canvas.SetLeft(_outline, Math.Min(start.X, end.X));
            Canvas.SetTop(_outline, Math.Min(start.Y, end.Y));
            _outline.Width = Math.Abs(start.X - end.X);
            _outline.Height = Math.Abs(start.Y - end.Y);
        }
    }

    private sealed class SnipReview : Window
    {
        private ReviewDecision _decision = ReviewDecision.Cancel;

        public SnipReview(BitmapSource snip)
        {
            Title = "Review snip"; Width = 900; Height = 660; MinWidth = 480; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = PanelBrush; Foreground = TextBrush; FontFamily = new FontFamily("Segoe UI");
            var layout = new DockPanel { Margin = new Thickness(16) };
            var title = new TextBlock { Text = "Review image before adding it to the note", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var retry = ActionButton("Select area again");
            var cancel = ActionButton("Cancel");
            var use = ActionButton("Add snip to note", true);
            retry.Click += (_, _) => { _decision = ReviewDecision.Retry; Close(); };
            cancel.Click += (_, _) => Close();
            use.Click += (_, _) => { _decision = ReviewDecision.Use; Close(); };
            actions.Children.Add(retry); actions.Children.Add(cancel); actions.Children.Add(use);
            DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
            layout.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(10, 15, 22)),
                Content = new System.Windows.Controls.Image { Source = snip, Stretch = Stretch.Uniform, Margin = new Thickness(8) }
            });
            Content = layout;
        }

        public ReviewDecision Review() { ShowDialog(); return _decision; }
    }

    private enum TargetDecision { Start, Back, Cancel }
    private enum ReviewDecision { Use, Retry, Cancel }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr handle, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
