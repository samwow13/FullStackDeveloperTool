using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FullStackRemoteCompanion;

internal sealed class CompanionWindow : Window
{
    private readonly Brush _mint = Brush("#8DE2C5");
    private readonly Brush _muted = Brush("#A1B1AE");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _mode = new();
    private readonly TextBlock _code = new() { FontSize = 27, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _expiry = new();
    private readonly TextBox _identity = ReadonlyBox();
    private readonly TextBox _pendingIdentity = ReadonlyBox();
    private readonly TextBlock _pendingName = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pendingExpiry = new();
    private readonly Border _pendingPanel;
    private readonly CheckBox _fingerprintMatches = new() { Content = "Controller fingerprint matches Launcher", Margin = new Thickness(0, 8, 0, 6) };
    private readonly Button _approve;
    private readonly Button _enable;
    private readonly Button _disable;
    private readonly StackPanel _paired = new();
    private readonly Button _install;
    private readonly Button _startService;
    private readonly Button _removeService;
    private readonly CheckBox _login = new() { Content = "Start tray app when I sign in", Margin = new Thickness(0, 8, 0, 10) };
    private readonly CheckBox _allowDesktop = new() { Content = "Allow desktop control", Margin = new Thickness(0, 7, 12, 0), ToolTip = "Allow approved Launchers to view screens and send mouse and keyboard input during this tray session. Turns off when this listener restarts." };
    private readonly TextBlock _desktopStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Button _stopDesktop;
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private CompanionHost? _host;
    private bool _service;
    private bool _busy;
    private bool _refreshing;
    private bool _quitting;
    private bool _settingLogin;
    private bool _settingDesktop;
    private string _pendingId = "";
    private CompanionSnapshot? _lastSnapshot;

    public CompanionWindow()
    {
        Title = "Full Stack Remote Companion"; Width = 690; Height = 780; MinWidth = 560; MinHeight = 500;
        Background = Brush("#101816"); Foreground = Brush("#E8F3EF"); FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Resources.Add(typeof(Button), ButtonStyle());
        Resources.Add(typeof(CheckBox), new Style(typeof(CheckBox)) { Setters = { new Setter(ForegroundProperty, Foreground) } });
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Remote Companion", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = _mint });
        content.Children.Add(new TextBlock { Text = Environment.MachineName + " · Private LAN", Foreground = _muted, Margin = new Thickness(0, 4, 0, 16) });
        _status.Margin = new Thickness(0, 0, 0, 4); content.Children.Add(_status);
        _mode.Foreground = _muted; content.Children.Add(_mode);
        content.Children.Add(Label("Pair a Launcher"));
        content.Children.Add(_code); _expiry.Foreground = _muted; content.Children.Add(_expiry);
        var pairButtons = Row();
        _enable = Button("Enable pairing", async () => await ControlAsync("enablePairing"));
        _disable = Button("Cancel pairing", async () => await ControlAsync("disablePairing"));
        pairButtons.Children.Add(_enable); pairButtons.Children.Add(_disable); content.Children.Add(pairButtons);
        var pending = new StackPanel(); pending.Children.Add(_pendingName);
        pending.Children.Add(new TextBlock { Text = "Compare controller fingerprint with Launcher before approving.", Foreground = _muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) });
        pending.Children.Add(_pendingIdentity); pending.Children.Add(_pendingExpiry); pending.Children.Add(_fingerprintMatches);
        var approvalRow = Row();
        _approve = Button("Approve matching fingerprint", async () => await ControlAsync("approvePairing", _pendingId));
        approvalRow.Children.Add(_approve); approvalRow.Children.Add(Button("Deny", async () => await ControlAsync("denyPairing", _pendingId)));
        pending.Children.Add(approvalRow);
        _pendingPanel = new Border { Child = pending, Background = Brush("#1B3029"), BorderBrush = _mint, BorderThickness = new Thickness(1), Padding = new Thickness(12), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        content.Children.Add(_pendingPanel);
        _fingerprintMatches.Checked += (_, _) => _approve.IsEnabled = !_busy && _pendingId.Length > 0;
        _fingerprintMatches.Unchecked += (_, _) => _approve.IsEnabled = false;
        content.Children.Add(Label("Desktop access"));
        var desktopRow = Row();
        desktopRow.Children.Add(_allowDesktop);
        _stopDesktop = Button("Stop viewer", StopDesktopAsync);
        desktopRow.Children.Add(_stopDesktop); content.Children.Add(desktopRow);
        _desktopStatus.Foreground = _muted; content.Children.Add(_desktopStatus);
        content.Children.Add(Label("Approved Launchers")); content.Children.Add(_paired);
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(_login);
        var setupRow = Row();
        setupRow.Children.Add(Button("Allow private LAN…", async () => await SetupAsync("--configure-firewall")));
        _install = Button("Install background service…", async () => await SetupAsync("--install-service"));
        setupRow.Children.Add(_install); options.Children.Add(setupRow);
        var serviceRow = Row();
        _startService = Button("Restart service…", async () => { if (_service) await SetupAsync("--restart-service"); else await RestartUserHostAsync(); });
        _removeService = Button("Remove service…", async () => await SetupAsync("--remove-service"));
        serviceRow.Children.Add(_startService); serviceRow.Children.Add(_removeService); options.Children.Add(serviceRow);
        options.Children.Add(new TextBlock { Text = "Tray listener supports desktop control while signed in. Background service supports system information after sign-out; use the tray listener for desktop control. Pairing and revocation remain local approvals.", Foreground = _muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        options.Children.Add(Label("This PC certificate fingerprint")); options.Children.Add(_identity);
        content.Children.Add(new Expander { Header = "Functions & startup", Content = options, Foreground = Foreground, Margin = new Thickness(0, 10, 0, 0) });
        content.Children.Add(_feedback);
        var footer = Row(); footer.Margin = new Thickness(0, 16, 0, 0);
        footer.Children.Add(Button("Hide to tray", () => { Hide(); return Task.CompletedTask; }));
        footer.Children.Add(Button("Quit tray app", QuitAsync)); content.Children.Add(footer);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Full Stack Remote Companion", Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Remote Companion", null, (_, _) => Dispatcher.Invoke(Open));
        menu.Items.Add("Stop desktop viewer", null, (_, _) => Dispatcher.InvokeAsync(StopDesktopAsync));
        menu.Items.Add("Quit tray app", null, (_, _) => Dispatcher.InvokeAsync(async () => await QuitAsync()));
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Open);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closing += CloseToTray;
        SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            var caption = 0x00000000;
            var text = 0x00FFFFFF;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        };
        _login.Checked += async (_, _) => await SetLoginAsync(true);
        _login.Unchecked += async (_, _) => await SetLoginAsync(false);
        _allowDesktop.Checked += async (_, _) => { if (!_settingDesktop) await ControlAsync("allowDesktop"); };
        _allowDesktop.Unchecked += async (_, _) => { if (!_settingDesktop) await ControlAsync("denyDesktop"); };
    }
    public async void Initialize(bool background)
    {
        _status.Text = "Starting companion…"; _enable.IsEnabled = false; _disable.IsEnabled = false;
        _busy = true; RenderSetup();
        if (!background) Show();
        try
        {
            _settingLogin = true; _login.IsChecked = ServiceInstallation.StartsAtLogin(); _settingLogin = false;
            _service = await Task.Run(ServiceInstallation.IsInstalled, _stop.Token);
            if (!_service) await StartUserHostAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or OperationCanceledException)
        { _status.Text = ex.Message; _feedback.Text = ex.Message; }
        _busy = false; RenderSetup();
        _timer.Start();
        await RefreshAsync();
    }
    private async Task StartUserHostAsync()
    {
        var token = _stop.Token;
        var host = await Task.Run(async () =>
        {
            var created = new CompanionHost(false);
            if (token.IsCancellationRequested) { await created.DisposeAsync(); token.ThrowIfCancellationRequested(); }
            created.Start(); return created;
        }, token);
        if (_quitting || token.IsCancellationRequested) await host.DisposeAsync();
        else _host = host;
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || _busy || _quitting) return;
        _refreshing = true;
        try
        {
            var host = _host;
            var snapshot = _service ? (await ServiceControlClient.SendAsync(new("snapshot"), _stop.Token)).Snapshot
                : host is null ? null : await Task.Run(host.Snapshot, _stop.Token);
            if (_quitting) return;
            if (snapshot is not null) Render(snapshot);
            else if (_host is null) { _enable.IsEnabled = false; _disable.IsEnabled = false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            if (!_quitting)
            {
                _status.Text = "Service control unavailable: " + (ex is OperationCanceledException ? "Service is stopped or did not respond." : ex.Message);
                _enable.IsEnabled = false; _disable.IsEnabled = false; _approve.IsEnabled = false;
                _allowDesktop.IsEnabled = false; _stopDesktop.IsEnabled = false;
                _pendingPanel.Visibility = Visibility.Collapsed;
                _mode.Text = "Windows service · LocalService"; RenderSetup();
            }
        }
        finally { _refreshing = false; }
    }
    private void Render(CompanionSnapshot snapshot)
    {
        _status.Text = snapshot.Status; _status.Foreground = snapshot.Listening ? _mint : Foreground;
        _mode.Text = snapshot.ServiceMode ? "Windows service · LocalService" : "Current Windows user · Tray app";
        _settingDesktop = true; _allowDesktop.IsChecked = snapshot.DesktopAllowed; _settingDesktop = false;
        _allowDesktop.IsEnabled = !_busy && snapshot.Listening && !snapshot.ServiceMode;
        _stopDesktop.IsEnabled = snapshot.DesktopActive;
        _desktopStatus.Text = snapshot.DesktopStatus;
        _identity.Text = snapshot.Fingerprint;
        _code.Text = snapshot.PairingCode.Length == 12 ? string.Join("-", Enumerable.Range(0, 3).Select(i => snapshot.PairingCode.Substring(i * 4, 4))) : "Pairing off";
        _expiry.Text = snapshot.PairingExpiresAt is { } expiry ? "Expires " + expiry.ToLocalTime().ToString("h:mm:ss tt") : "Enable pairing, then enter this PC's code in Launcher.";
        _enable.IsEnabled = !_busy && snapshot.Listening && snapshot.Pending is null;
        _disable.IsEnabled = !_busy && (snapshot.PairingCode.Length != 0 || snapshot.Pending is not null);
        if (snapshot.Pending is { } pending)
        {
            if (_pendingId != pending.Id)
            {
                _fingerprintMatches.IsChecked = false;
                _pendingId = pending.Id;
                _tray.ShowBalloonTip(5000, "Launcher pairing approval", pending.ControllerName + " requests pairing. Open companion to compare its fingerprint.", System.Windows.Forms.ToolTipIcon.Info);
                Open();
            }
            _pendingName.Text = pending.ControllerName + " · " + pending.Address;
            var shortPin = string.Join(" ", Enumerable.Range(0, 4).Select(i => pending.Fingerprint.Substring(i * 4, 4)));
            _pendingIdentity.Text = shortPin + Environment.NewLine + pending.Fingerprint;
            _pendingExpiry.Text = "Approve before " + pending.ExpiresAt.ToLocalTime().ToString("h:mm:ss tt");
            _pendingPanel.Visibility = Visibility.Visible; _approve.IsEnabled = !_busy && _fingerprintMatches.IsChecked == true;
        }
        else { _pendingId = ""; _fingerprintMatches.IsChecked = false; _pendingPanel.Visibility = Visibility.Collapsed; }
        if (_lastSnapshot is null || !snapshot.Controllers.SequenceEqual(_lastSnapshot.Controllers))
        {
            _paired.Children.Clear();
            if (snapshot.Controllers.Length == 0) _paired.Children.Add(new TextBlock { Text = "No approved Launchers", Foreground = _muted });
            foreach (var controller in snapshot.Controllers)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var revoke = Button("Revoke", async () =>
                {
                    if (MessageBox.Show("Revoke " + controller.Name + "? Its management access ends on the next request. Any active desktop viewer stops now.", "Revoke Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        await ControlAsync("revoke", controller.Fingerprint);
                });
                DockPanel.SetDock(revoke, Dock.Right); row.Children.Add(revoke);
                row.Children.Add(new TextBlock { Text = controller.Name + "\n" + controller.Fingerprint[..16], ToolTip = controller.Fingerprint, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                _paired.Children.Add(row);
            }
        }
        _lastSnapshot = snapshot; RenderSetup();
    }
    private void RenderSetup()
    {
        _install.Visibility = _service ? Visibility.Collapsed : Visibility.Visible;
        _startService.Visibility = Visibility.Visible;
        _startService.Content = _service ? "Restart service…" : "Restart tray listener";
        _removeService.Visibility = _service ? Visibility.Visible : Visibility.Collapsed;
        _install.IsEnabled = !_busy; _startService.IsEnabled = !_busy; _removeService.IsEnabled = !_busy;
    }
    private async Task ControlAsync(string action, string id = "")
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var host = _host;
            var reply = _service ? await ServiceControlClient.SendAsync(new(action, id), _stop.Token)
                : host is null ? null : await Task.Run(() => host.Control(new(action, id)), _stop.Token);
            if (_quitting) return;
            if (reply is null) throw new InvalidOperationException("Companion listener is unavailable.");
            _feedback.Text = reply.Success ? "" : reply.Message;
            if (reply.Snapshot is not null) Render(reply.Snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        { _feedback.Text = ex.Message; }
        finally { _busy = false; await RefreshAsync(); }
    }
    private async Task StopDesktopAsync()
    {
        // Local emergency stop remains available while another setup/pairing action is busy.
        var host = _host;
        if (host is null) return;
        var reply = await Task.Run(() => host.Control(new("stopDesktop")));
        if (!_quitting && reply.Snapshot is not null) Render(reply.Snapshot);
    }
    private async Task SetupAsync(string action)
    {
        if (_busy) return;
        var message = action switch
        {
            "--install-service" => "Install companion as a LocalService Windows service and allow its UDP 45873 / TCP 45874 listeners on private LANs? The service provides system information after sign-out; desktop control requires the tray listener. This switches to a separate protected service identity; pair Launchers again after installation.",
            "--remove-service" => "Stop and remove only the Remote Companion Windows service? Protected pairing data stays on this PC. The tray app resumes its own user identity.",
            "--configure-firewall" => "Allow this companion's UDP 45873 and TCP 45874 listeners through Windows Firewall on Private and Domain profiles, from LocalSubnet only?",
            "--restart-service" => "Restart only the Remote Companion Windows service? Companion connections pause while this service restarts.",
            _ => "Start the installed Remote Companion Windows service?"
        };
        if (MessageBox.Show(message, "Remote Companion setup", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _busy = true; RenderSetup();
        try
        {
            if (action == "--install-service" && _host is not null)
            {
                await _host.DisposeAsync();
                if (_host.ShutdownPending) throw new InvalidOperationException("Existing companion work has not finished. Wait before installing the background service.");
                _host = null;
            }
            _feedback.Text = "Waiting for administrator setup…";
            await ServiceInstallation.ElevateAsync(action);
            _service = ServiceInstallation.IsInstalled();
            if (!_service && _host is null) await StartUserHostAsync();
            _feedback.Text = action switch { "--install-service" => "Background service installed. Enable pairing to connect Launchers.", "--configure-firewall" => "Private LAN firewall rules saved.", "--remove-service" => "Companion service removed. User tray listener resumed. Use Allow private LAN to update firewall rules for the tray app's executable.", "--restart-service" => "Companion service restarted.", _ => "Companion service started." };
            _lastSnapshot = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            _feedback.Text = ex is Win32Exception win32 && win32.NativeErrorCode == 1223 ? "Administrator setup was canceled." : ex.Message;
            try { _service = ServiceInstallation.IsInstalled(); if (!_service && _host is null) await StartUserHostAsync(); }
            catch (Exception restart) { _feedback.Text += " " + restart.Message; }
        }
        finally { _busy = false; RenderSetup(); await RefreshAsync(); }
    }
    private async Task RestartUserHostAsync()
    {
        if (_busy || _service) return;
        if (MessageBox.Show("Restart this tray app's companion listener? Existing companion connections pause; saved pairing stays.", "Restart Remote Companion", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _busy = true;
        try
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
                if (_host.ShutdownPending) throw new InvalidOperationException("Existing companion work has not finished. Wait before restarting the listener.");
                _host = null;
            }
            await StartUserHostAsync(); _feedback.Text = "Tray listener restarted."; _lastSnapshot = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        { _feedback.Text = ex.Message; }
        finally { _busy = false; await RefreshAsync(); }
    }
    private Task SetLoginAsync(bool enabled)
    {
        if (_settingLogin) return Task.CompletedTask;
        try { ServiceInstallation.SetStartsAtLogin(enabled); _feedback.Text = enabled ? "Tray app will start when this Windows user signs in." : "Sign-in startup disabled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _feedback.Text = ex.Message; _settingLogin = true; _login.IsChecked = !enabled; _settingLogin = false; }
        return Task.CompletedTask;
    }
    private void Open() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void CloseToTray(object? sender, CancelEventArgs e) { if (!_quitting) { e.Cancel = true; Hide(); } }
    private async Task QuitAsync()
    {
        if (_busy || _quitting) return;
        if (!_service && _host is not null && MessageBox.Show("Quit companion and stop this PC's remote listener?", "Quit Remote Companion", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _quitting = true; _timer.Stop(); _stop.Cancel();
        if (_host is not null) await _host.DisposeAsync();
        _tray.Visible = false; _tray.Dispose(); _stop.Dispose(); Close(); Application.Current.Shutdown();
    }
    private TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = _muted, Margin = new Thickness(0, 16, 0, 7) };
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { _feedback.Text = ex.Message; } };
        return button;
    }
    private Style ButtonStyle() => new(typeof(Button))
    {
        Setters =
        {
            new Setter(Control.BackgroundProperty, Brush("#243A32")), new Setter(Control.ForegroundProperty, Brush("#E8F3EF")),
            new Setter(Control.BorderBrushProperty, Brush("#416557")), new Setter(Control.PaddingProperty, new Thickness(11, 7, 11, 7)),
            new Setter(Control.FontWeightProperty, FontWeights.SemiBold), new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand),
            new Setter(Control.TemplateProperty, DarkButtonTemplate())
        }
    };
    private ControlTemplate DarkButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#315145"))); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BorderBrushProperty, _mint)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#A1B1AE")));
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#1B2923")));
        disabled.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Arrow)); template.Triggers.Add(disabled);
        return template;
    }
    private static TextBox ReadonlyBox() => new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brush("#17231E"), Foreground = Brush("#DAE9E3"),
        BorderBrush = Brush("#375247"), Padding = new Thickness(8), FontFamily = new FontFamily("Consolas"), FontSize = 12
    };
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
