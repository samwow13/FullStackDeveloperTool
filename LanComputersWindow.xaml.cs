using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class LanComputersWindow : Window
{
    private readonly LanDiscoveryService _discovery = new();
    private readonly LanRemoteControlService _remote = new();
    private readonly ObservableCollection<LanComputer> _computers = [];
    private CancellationTokenSource? _operation;
    private LanRemoteSession? _session;
    private bool _busy;
    private bool _closed;
    private bool _ready;
    private bool _refreshingSelection;
    private LanComputer? Selected => ComputersList.SelectedItem as LanComputer;
    private bool UseHttps => TransportBox.SelectedIndex != 1;

    public LanComputersWindow()
    {
        InitializeComponent();
        InitializeLanTheme();
        ComputersList.ItemsSource = _computers;
        _ready = true;
        UpdateControls();
        Loaded += async (_, _) =>
        {
            await InitializeCompanionAsync();
            await LoadNetworksAsync();
            StartCompanionMonitoring();
        };
    }

    private async Task LoadNetworksAsync()
    {
        await RunAsync(async token =>
        {
            var oldAddress = (NetworkBox.SelectedItem as LanNetwork)?.LocalAddress;
            var networks = await Task.Run(() => _discovery.GetNetworks(), token);
            token.ThrowIfCancellationRequested();
            NetworkBox.ItemsSource = networks;
            NetworkBox.SelectedItem = networks.FirstOrDefault(item => item.LocalAddress == oldAddress) ?? networks.FirstOrDefault();
            if (networks.Count == 0) ShowNetworkSettings();
            SetStatus(networks.Count == 0 ? "No connected IPv4 LAN adapter found. Connect to your LAN, then refresh networks." : "");
        }, "Finding connected LAN networks…");
    }

    private async void Networks_Click(object sender, RoutedEventArgs e) => await LoadNetworksAsync();

    private void Network_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (NetworkBox.SelectedItem is LanNetwork network) RangeBox.Text = network.SuggestedCidr;
        SignOut(clearPassword: true);
        _computers.Clear();
        RestartCompanionDiscovery();
        UpdateControls();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || NetworkBox.SelectedItem is not LanNetwork network) return;
        var range = RangeBox.Text.Trim();
        SignOut(clearPassword: true);
        foreach (var item in _computers.Where(item => item.Companion == null).ToArray()) _computers.Remove(item);
        await RunAsync(async token =>
        {
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Value = 0;
            var activeOperation = _operation;
            var progress = new Progress<LanDiscoveryProgress>(value =>
            {
                if (_closed || !ReferenceEquals(activeOperation, _operation)) return;
                ScanProgress.Maximum = Math.Max(1, value.Total);
                ScanProgress.Value = value.Completed;
                if (value.Computer != null) AddOrReplace(value.Computer);
                SetStatus($"Scanning {range}: {value.Completed}/{value.Total} addresses checked · {_computers.Count} online.");
            });
            var computers = await Task.Run(() => _discovery.DiscoverAsync(network, range, progress, token), token);
            token.ThrowIfCancellationRequested();
            foreach (var computer in computers) AddOrReplace(computer);
            SetStatus($"Scan complete · {_computers.Count} online device(s) · {DateTime.Now:t}.");
        }, "Scanning LAN…");
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (_busy) return;
        if (host.Length == 0) { SetStatus("Enter a hostname or IPv4 address on your LAN.", error: true); HostBox.Focus(); return; }
        var added = false;
        await RunAsync(async token =>
        {
            var computer = await Task.Run(() => _discovery.ProbeHostAsync(host, token), token);
            token.ThrowIfCancellationRequested();
            computer = AddOrReplace(computer);
            ComputersList.SelectedItem = computer;
            added = true;
            SetStatus($"Added {computer.DisplayName} ({computer.Address}).");
        }, $"Checking {host}…");
        if (added && !_closed)
        {
            SetAddComputerVisible(false);
            ComputersList.Focus();
        }
    }

    private void Host_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add_Click(sender, e);
    }

    private LanComputer AddOrReplace(LanComputer computer)
    {
        var existing = _computers.FirstOrDefault(item => item.Address == computer.Address);
        if (existing != null)
        {
            if (computer.Companion == null && existing.Companion != null)
                computer = computer with { Companion = existing.Companion, IsOnline = computer.IsOnline || existing.IsCompanionFresh };
            var selected = ReferenceEquals(existing, Selected);
            var index = _computers.IndexOf(existing);
            var refreshingSelection = _refreshingSelection;
            _refreshingSelection |= selected;
            try
            {
                _computers[index] = computer;
                if (selected) ComputersList.SelectedItem = computer;
            }
            finally { _refreshingSelection = refreshingSelection; }
            if (selected)
            {
                if (existing.Companion != null && computer.Companion != null && (existing.Companion.DeviceId != computer.Companion.DeviceId ||
                    !existing.Companion.CertificateFingerprint.Equals(computer.Companion.CertificateFingerprint, StringComparison.OrdinalIgnoreCase)))
                    Computer_Changed(ComputersList, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, new[] { existing }, new[] { computer }));
                else RefreshSelectedComputerDetails();
            }
        }
        else
        {
            var index = 0;
            while (index < _computers.Count && string.Compare(_computers[index].DisplayName, computer.DisplayName, StringComparison.OrdinalIgnoreCase) < 0) index++;
            _computers.Insert(index, computer);
        }
        UpdateControls();
        return computer;
    }

    private void Computer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingSelection) return;
        SignOut(clearPassword: true);
        ResetCompanionSelection();
        var computer = Selected;
        RefreshSelectedComputerDetails();
        ConnectionHostBox.Text = computer == null ? "" : string.IsNullOrWhiteSpace(computer.HostName) ? computer.Address : computer.HostName;
        SetComputerResult("Device details", computer == null ? "" : FormatDiscoveryDetails(computer));
        ResultsExpander.IsExpanded = false;
        SelectedComputerPanel.ScrollToTop();
        UpdateControls();
        QueueCompanionReconnect();
    }

    private void RefreshSelectedComputerDetails()
    {
        var computer = Selected;
        SelectedNameText.Text = computer?.DisplayName ?? "Choose a computer";
        SelectedDetailsText.Text = computer == null ? "" : $"{computer.Address}{(computer.IsLocal ? " · This PC" : "")} · {computer.DeviceSummary}\n{computer.DeviceModelSummary}".TrimEnd();
    }

    private void Transport_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateControls(); }

    private static string FormatDiscoveryDetails(LanComputer computer)
    {
        static string Available(string value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
        var device = computer.Device;
        return $"Device: {device.DeviceType}\nConfidence: {device.Confidence}\nManufacturer: {Available(device.Manufacturer)}\nModel: {Available(device.Model)}\n"
            + $"Advertised name: {Available(device.FriendlyName)}\nHostname: {Available(computer.HostName)}\nIPv4 address: {computer.Address}\nMAC address: {Available(computer.MacAddress)}\n"
            + $"Reachability: {Available(computer.ReachabilityEvidence)}\nRemote services: {computer.ConnectionSummary}\n"
            + $"Observed services: {(device.Services.Count == 0 ? "None observed" : string.Join(", ", device.Services))}\nLast seen: {computer.LastSeen.ToLocalTime():g}\n"
            + (computer.Companion is { } companion ? $"Companion: {computer.CompanionSummary}\nAdvertised companion name: {companion.ComputerName}\nCompanion last seen: {companion.LastSeen.ToLocalTime():g}\n" : "") + "\n"
            + "Identification evidence:\n" + (device.Evidence.Count == 0 ? "No identifying information observed." : string.Join("\n", device.Evidence.Select(value => "• " + value)));
    }

    private async void RefreshComputer_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } computer) return;
        await RunAsync(async token =>
        {
            var refreshed = await Task.Run(() => _discovery.ProbeHostAsync(computer.Address, token), token);
            token.ThrowIfCancellationRequested();
            refreshed = AddOrReplace(refreshed);
            if (refreshed.Companion != null)
            {
                var companion = await Task.Run(() => _companionDiscovery.ProbeAsync(refreshed.Address, token), token);
                token.ThrowIfCancellationRequested();
                if (companion != null) ObserveCompanion(companion);
            }
            SetStatus($"Refreshed {refreshed.DisplayName}.");
        }, $"Refreshing {computer.DisplayName}…");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false } computer) return;
        using var password = PasswordInput.SecurePassword;
        if (string.IsNullOrWhiteSpace(UserNameBox.Text) || password.Length == 0)
        {
            SetStatus("Enter your remote Windows username and account password.", error: true);
            return;
        }
        var host = ConnectionHostBox.Text.Trim();
        var https = UseHttps;
        var userName = UserNameBox.Text.Trim();
        await RunAsync(async token =>
        {
            using var credentials = new LanRemoteCredentials(userName, password);
            var target = await Task.Run(() => _discovery.ProbeHostAsync(host, token), token);
            if (target.Address != computer.Address) throw new InvalidOperationException("Connection name resolves to a different PC. Use a name for the selected address.");
            var session = await Task.Run(() => _remote.ConnectAsync(host, https, credentials, token, expectedAddress: computer.Address), token);
            if (_closed || token.IsCancellationRequested)
            {
                session.Dispose();
                token.ThrowIfCancellationRequested();
                return;
            }
            _session?.Dispose();
            _session = session;
            PasswordInput.Clear();
            SetComputerResult("Windows connection", $"Computer: {session.Identity.ComputerName}\nWindows: {session.Identity.OperatingSystem}\nAccount: {session.Identity.Identity}\nConnection: {session.Target} · {(session.UseHttps ? "HTTPS" : "Kerberos")}\nAuthenticated: {session.AuthenticatedAt.ToLocalTime():g}");
            SetStatus($"Signed in to {session.Identity.ComputerName} as {session.Identity.Identity}.");
        }, $"Signing in to {computer.DisplayName}…");
        UpdateControls();
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Connect_Click(sender, e);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        SignOut(clearPassword: true);
        SetComputerResult("Device details", Selected == null ? "" : FormatDiscoveryDetails(Selected));
        SetStatus("Signed out. Management credentials cleared.");
        UpdateControls();
    }

    private void SignOut(bool clearPassword)
    {
        _session?.Dispose();
        _session = null;
        SignOutCompanion(clearPassword);
        if (clearPassword) PasswordInput.Clear();
    }

    private void SystemInfo_Click(object sender, RoutedEventArgs e) => Query(LanRemoteAction.SystemInfo);
    private void Processes_Click(object sender, RoutedEventArgs e) => Query(LanRemoteAction.Processes);

    private async void Query(LanRemoteAction action)
    {
        if (_busy || _session is not { } session) return;
        await RunAsync(async token =>
        {
            var result = await Task.Run(() => _remote.QueryAsync(session, action, token), token);
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            SetComputerResult(result.Title, result.Text, reveal: true);
            SetStatus($"{result.Title} received from {session.Identity.ComputerName} · {result.CompletedAt.ToLocalTime():t}.");
        }, $"Reading {action} from {session.Identity.ComputerName}…");
    }

    private async void Desktop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false } computer) return;
        await RunAsync(async token =>
        {
            var refreshed = await Task.Run(() => _discovery.ProbeHostAsync(computer.Address, token), token);
            if (!refreshed.RdpAvailable) throw new InvalidOperationException("Remote Desktop port 3389 is not reachable. Enable Remote Desktop on this PC, then refresh.");
            token.ThrowIfCancellationRequested();
            await LanRemoteControlService.OpenRemoteDesktopAsync(computer.Address, token);
            SetStatus($"Opened Remote Desktop for {computer.Address}. Complete the Windows sign-in prompt.");
        }, "Checking Remote Desktop…");
    }

    private async void Shares_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false } computer) return;
        await RunAsync(async token =>
        {
            var refreshed = await Task.Run(() => _discovery.ProbeHostAsync(computer.Address, token), token);
            if (!refreshed.SmbAvailable) throw new InvalidOperationException("File sharing port 445 is not reachable. Check sharing permissions and the target firewall, then refresh.");
            token.ThrowIfCancellationRequested();
            await LanRemoteControlService.OpenSharedFoldersAsync(computer.Address, token);
            SetStatus($"Opened shared folders for {computer.Address}. Windows handles share access and sign-in.");
        }, "Checking file sharing…");
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, string status)
    {
        if (_busy || _closed) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _busy = true;
        ScanProgress.IsIndeterminate = true;
        ScanProgress.Visibility = Visibility.Visible;
        SetStatus(status);
        UpdateControls();
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus($"Canceled · {_computers.Count} discovered device(s) retained."); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
        finally
        {
            _operation = null;
            _busy = false;
            if (!_closed) { ScanProgress.Visibility = Visibility.Collapsed; UpdateControls(); }
        }
    }

    private void UpdateControls()
    {
        if (!_ready || _closed) return;
        var computer = Selected;
        var remote = computer is { IsLocal: false, IsOnline: true };
        NetworkBox.IsEnabled = !_busy;
        RangeBox.IsEnabled = !_busy && NetworkBox.SelectedItem != null;
        NetworksButton.IsEnabled = !_busy;
        ScanButton.IsEnabled = !_busy && NetworkBox.SelectedItem != null;
        CancelButton.IsEnabled = _busy;
        HostBox.IsEnabled = AddButton.IsEnabled = ComputersList.IsEnabled = !_busy;
        RefreshComputerButton.IsEnabled = !_busy && computer != null;
        DesktopButton.IsEnabled = !_busy && remote && computer!.RdpAvailable;
        SharesButton.IsEnabled = !_busy && remote && computer!.SmbAvailable;
        LoginFields.IsEnabled = !_busy && remote && _session == null;
        ConnectButton.Visibility = _session == null ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = !_busy && remote;
        DisconnectButton.Visibility = _session != null ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.IsEnabled = !_busy;
        SystemInfoButton.IsEnabled = ProcessesButton.IsEnabled = !_busy && _session != null;
        LoginStateText.Text = _session != null
            ? $"Authenticated: {_session.Identity.ComputerName} · {_session.Identity.Identity}"
            : computer == null ? "Select a PC to sign in."
            : computer.IsLocal ? "This PC. Select another PC for remote controls."
            : (UseHttps ? computer.WinRmHttpsAvailable : computer.WinRmHttpAvailable) ? "Use an account allowed to manage this PC."
            : $"WinRM port {(UseHttps ? "5986" : "5985")} was not detected. Check Connection setup before signing in.";
        ComputerCountText.Text = $"Computers ({_computers.Count})";
        EmptyText.Visibility = _computers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompanionControls();
        UpdateRemotePresentation();
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_closed) return;
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = error ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 172, 169))
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void Help_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
        e.Handled = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _operation?.Cancel();
        StopCompanionMonitoring();
        SignOut(clearPassword: true);
        UserNameBox.Clear();
    }
}
