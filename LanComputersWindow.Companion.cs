using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class LanComputersWindow
{
    private const string CompanionReachability = "Companion discovery response";
    private readonly LanCompanionDiscoveryService _companionDiscovery = new();
    private readonly LanCompanionClient _companionClient = new();
    private readonly DispatcherTimer _companionTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly HashSet<string> _expiredCompanions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _companionDiscoveryCancellation;
    private CancellationTokenSource _companionSelectionCancellation = new();
    private LanCompanionSession? _companionSession;
    private LanDesktopWindow? _companionDesktop;
    private int _companionNetworkGeneration;
    private int _companionSelectionGeneration;
    private bool _companionMonitorBusy;
    private bool _companionReconnectPaused;
    private int _companionReconnectAttempts;
    private DateTimeOffset _companionNextReconnect;
    private string _companionStatus = string.Empty;
    private string _controllerFingerprint = string.Empty;
    private string _companionInitializationError = string.Empty;

    private async Task InitializeCompanionAsync()
    {
        try
        {
            var fingerprint = await Task.Run(() => _companionClient.ControllerFingerprint);
            if (_closed) return;
            _controllerFingerprint = fingerprint;
            var shortFingerprint = string.Join("-", Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
            CompanionFingerprintText.Text = $"Launcher fingerprint: {shortFingerprint}. Approve matching fingerprint on remote PC.";
            CompanionFingerprintText.ToolTip = $"Full Launcher fingerprint: {fingerprint}";
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _companionInitializationError = ex.Message;
        }
        UpdateControls();
    }

    private void StartCompanionMonitoring()
    {
        if (_closed) return;
        _companionTimer.Tick += CompanionTimer_Tick;
        _companionTimer.Start();
        if (_companionDiscoveryCancellation == null) RestartCompanionDiscovery();
    }

    private void RestartCompanionDiscovery()
    {
        _companionDiscoveryCancellation?.Cancel();
        _companionDiscoveryCancellation = null;
        _companionNetworkGeneration++;
        _expiredCompanions.Clear();
        if (_closed) return;
        if (NetworkBox.SelectedItem is not LanNetwork network)
        {
            CompanionDiscoveryText.Text = "Select a network to discover companion PCs.";
            return;
        }
        CompanionDiscoveryText.Text = $"Finding companion PCs on {network.Name}…";
        var cancellation = new CancellationTokenSource();
        _companionDiscoveryCancellation = cancellation;
        _ = DiscoverCompanionsAsync(network, _companionNetworkGeneration, cancellation);
    }

    private async Task DiscoverCompanionsAsync(LanNetwork network, int generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var devices = await Task.Run(() => _companionDiscovery.DiscoverAsync(network, token), token);
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _companionNetworkGeneration) return;
                    try { await _companionClient.RefreshTrustAsync(token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _companionStatus = ex.Message; }
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _companionNetworkGeneration) return;
                    foreach (var device in devices) ObserveCompanion(device);
                    ExpireCompanions();
                    var available = _computers.Count(item => item.IsCompanionFresh);
                    CompanionDiscoveryText.Text = $"Auto-discovery · {available} companion{(available == 1 ? "" : "s")} available";
                    CompanionDiscoveryText.ToolTip = $"Last checked {DateTime.Now:t}. Companion PCs appear automatically on the selected network.";
                    UpdateControls();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (_closed || generation != _companionNetworkGeneration) return;
                    CompanionDiscoveryText.Text = $"Companion discovery: {ex.Message}";
                    ExpireCompanions();
                    UpdateControls();
                }
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_companionDiscoveryCancellation, cancellation)) _companionDiscoveryCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ObserveCompanion(LanCompanionDevice device)
    {
        if (_closed) return;
        _expiredCompanions.Remove(device.DeviceId);
        var previousAddress = _computers.FirstOrDefault(item => item.Companion?.DeviceId == device.DeviceId && item.Address != device.Address);
        var movedSelection = ReferenceEquals(previousAddress, Selected) && previousAddress != null;
        var changedIdentity = movedSelection && !previousAddress!.Companion!.CertificateFingerprint.Equals(device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase);
        var refreshingSelection = _refreshingSelection;
        _refreshingSelection |= movedSelection;
        try
        {
            if (previousAddress != null)
            {
                if (previousAddress.ReachabilityEvidence == CompanionReachability) _computers.Remove(previousAddress);
                else _computers[_computers.IndexOf(previousAddress)] = previousAddress with { Companion = null };
            }
            var existing = _computers.FirstOrDefault(item => item.Address == device.Address);
            var observed = existing == null
                ? new LanComputer
                {
                    Address = device.Address,
                    IsLocal = (NetworkBox.SelectedItem as LanNetwork)?.LocalAddress == device.Address,
                    IsOnline = true,
                    Companion = device,
                    LastSeen = device.LastSeen,
                    ReachabilityEvidence = CompanionReachability
                }
                : existing with { Companion = device, IsOnline = true, LastSeen = device.LastSeen > existing.LastSeen ? device.LastSeen : existing.LastSeen };
            observed = AddOrReplace(observed);
            if (movedSelection)
            {
                ComputersList.SelectedItem = observed;
                _session?.Dispose();
                _session = null;
                if (_companionSession?.Address != device.Address)
                {
                    CloseCompanionDesktop();
                    _companionSession?.Dispose();
                    _companionSession = null;
                    _companionStatus = "Address changed. Reconnecting…";
                }
                ConnectionHostBox.Text = device.Address;
                _companionNextReconnect = DateTimeOffset.MinValue;
                RefreshSelectedComputerDetails();
            }
        }
        finally { _refreshingSelection = refreshingSelection; }
        if (changedIdentity) Computer_Changed(ComputersList, new System.Windows.Controls.SelectionChangedEventArgs(
            System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new[] { previousAddress! }, new[] { Selected! }));
        UpdateControls();
    }

    private void ExpireCompanions()
    {
        foreach (var computer in _computers.Where(item => item.Companion != null && !item.IsCompanionFresh).ToArray())
        {
            if (!_expiredCompanions.Add(computer.Companion!.DeviceId)) continue;
            AddOrReplace(computer with { IsOnline = computer.ReachabilityEvidence == CompanionReachability ? false : computer.IsOnline });
        }
    }

    private void ResetCompanionSelection()
    {
        CloseCompanionDesktop();
        _companionSelectionGeneration++;
        _companionSelectionCancellation.Cancel();
        _companionSelectionCancellation.Dispose();
        _companionSelectionCancellation = new();
        _companionReconnectPaused = false;
        _companionReconnectAttempts = 0;
        _companionNextReconnect = DateTimeOffset.MinValue;
        _companionStatus = string.Empty;
    }

    private void SignOutCompanion(bool clearCode)
    {
        CloseCompanionDesktop();
        _companionSession?.Dispose();
        _companionSession = null;
        if (clearCode) CompanionCodeInput.Clear();
    }

    private bool IsCompanionPaired(LanCompanionDevice? device)
    {
        if (device == null || _companionInitializationError.Length != 0) return false;
        try { return _companionClient.IsPaired(device); }
        catch (Exception ex) { _companionStatus = ex.Message; return false; }
    }

    private bool HasSavedCompanionPairing(LanCompanionDevice? device)
    {
        if (device == null || _companionInitializationError.Length != 0) return false;
        try { return _companionClient.HasSavedPairing(device.DeviceId); }
        catch (Exception ex) { _companionStatus = ex.Message; return false; }
    }

    private void UpdateCompanionControls()
    {
        var computer = Selected;
        var device = computer?.Companion;
        var paired = IsCompanionPaired(device);
        var savedPairing = HasSavedCompanionPairing(device);
        var identityChanged = savedPairing && !paired;
        if (!paired && _companionSession != null)
        {
            CloseCompanionDesktop();
            _companionSession.Dispose();
            _companionSession = null;
        }
        var remote = computer is { IsLocal: false };
        var available = remote && computer!.IsCompanionFresh;
        var connected = _companionSession is { IsConnected: true } && _companionSession.DeviceId == device?.DeviceId;
        CompanionPairingFields.Visibility = remote && device != null && !savedPairing && computer!.IsCompanionFresh && device.PairingAvailable
            ? Visibility.Visible : Visibility.Collapsed;
        CompanionPairingFields.IsEnabled = !_busy && available && device!.PairingAvailable && !identityChanged && _controllerFingerprint.Length != 0;
        CompanionFingerprintText.Visibility = CompanionPairingFields.Visibility;
        CompanionConnectButton.Visibility = paired && !connected ? Visibility.Visible : Visibility.Collapsed;
        CompanionConnectButton.IsEnabled = !_busy && available && paired;
        CompanionDisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CompanionDisconnectButton.IsEnabled = !_busy;
        CompanionSystemInfoButton.IsEnabled = CompanionProcessesButton.IsEnabled = !_busy && remote && connected && computer!.IsCompanionFresh;
        CompanionSystemInfoButton.Visibility = CompanionProcessesButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CompanionDesktopButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CompanionDesktopButton.IsEnabled = !_busy && remote && connected && computer!.IsCompanionFresh && _companionSession!.SupportsDesktop;
        CompanionDesktopButton.ToolTip = connected && _companionSession?.SupportsDesktop != true
            ? "Desktop control requires the updated companion tray app in the remote PC's signed-in session. Background service supports management only."
            : "Open the companion's signed-in desktop over the paired LAN connection. Mouse and keyboard control starts off.";
        CompanionActionsPanel.Visibility = paired || connected ? Visibility.Visible : Visibility.Collapsed;
        CompanionOptionsButton.Visibility = savedPairing ? Visibility.Visible : Visibility.Collapsed;
        CompanionOptionsButton.IsEnabled = !_busy && remote;
        CompanionForgetButton.Visibility = savedPairing ? Visibility.Visible : Visibility.Collapsed;
        CompanionForgetButton.IsEnabled = !_busy && remote;
        CompanionStateText.Text = _companionInitializationError.Length != 0 ? _companionInitializationError
            : computer == null ? "Select a companion PC to pair or connect."
            : computer.IsLocal ? "This PC. Select another PC for companion access."
            : device == null ? "For desktop control, system info and processes, run the companion on this PC. See Companion setup."
            : identityChanged ? "Companion identity changed. Forget the saved pairing, then pair again after checking the remote PC."
            : connected ? $"Connected: {_companionSession!.ComputerName}"
            : !computer.IsCompanionFresh ? $"Unavailable · last seen {device.LastSeen.ToLocalTime():t}."
            : _companionStatus.Length != 0 ? _companionStatus
            : paired ? _companionReconnectPaused ? "Paired. Select Connect to reconnect." : "Paired. Connecting automatically…"
            : device.PairingAvailable ? "Pairing available. Enter code, then approve matching fingerprint on remote PC."
            : "Open the companion on this PC and select Enable pairing.";
    }

    private void QueueCompanionReconnect()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(new Action(async () => await MonitorCompanionAsync()));
    }

    private async void CompanionTimer_Tick(object? sender, EventArgs e) => await MonitorCompanionAsync();

    private async Task MonitorCompanionAsync()
    {
        if (_closed || _companionMonitorBusy) return;
        ExpireCompanions();
        UpdateControls();
        if (_busy || _companionReconnectPaused || Selected is not { IsLocal: false, Companion: { } device } computer || !IsCompanionPaired(device)) return;
        _companionMonitorBusy = true;
        var generation = _companionSelectionGeneration;
        var token = _companionSelectionCancellation.Token;
        try
        {
            if (_companionSession is { } session && session.Address == device.Address)
            {
                try
                {
                    await _companionClient.PingAsync(session, token);
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _companionSelectionGeneration || !ReferenceEquals(session, _companionSession)) return;
                    ObserveCompanion(AuthenticatedCompanionObservation(device, session));
                    _companionStatus = $"Connected: {session.ComputerName}";
                    _companionReconnectAttempts = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (_closed || generation != _companionSelectionGeneration || !ReferenceEquals(session, _companionSession)) return;
                    CloseCompanionDesktop();
                    session.Dispose();
                    _companionSession = null;
                    ScheduleCompanionReconnect(ex.Message);
                }
            }
            else if (computer.IsCompanionFresh && DateTimeOffset.UtcNow >= _companionNextReconnect)
            {
                await ConnectCompanionAsync(device, automatic: true);
            }
        }
        finally
        {
            _companionMonitorBusy = false;
            if (!_closed) UpdateControls();
        }
    }

    private void ScheduleCompanionReconnect(string reason)
    {
        _companionReconnectAttempts++;
        var delay = Math.Min(30, 5 * Math.Pow(2, Math.Min(3, _companionReconnectAttempts - 1)));
        _companionNextReconnect = DateTimeOffset.UtcNow.AddSeconds(delay);
        _companionStatus = $"Reconnecting in {delay:0}s · {reason}";
    }

    private async Task ConnectCompanionAsync(LanCompanionDevice device, bool automatic)
    {
        var generation = _companionSelectionGeneration;
        _companionReconnectPaused = false;
        await RunAsync(async operationToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _companionSelectionCancellation.Token);
            _companionStatus = "Connecting…";
            UpdateControls();
            try
            {
                var session = await Task.Run(() => _companionClient.ConnectAsync(device, linked.Token), linked.Token);
                if (_closed || linked.IsCancellationRequested || generation != _companionSelectionGeneration || Selected?.Companion?.DeviceId != device.DeviceId || Selected?.Companion?.Address != device.Address)
                {
                    session.Dispose();
                    linked.Token.ThrowIfCancellationRequested();
                    return;
                }
                PublishCompanionSession(session, device);
            }
            catch (OperationCanceledException)
            {
                if (!_closed && generation == _companionSelectionGeneration)
                {
                    _companionReconnectPaused = true;
                    _companionStatus = "Connection canceled. Select Connect to reconnect.";
                }
                throw;
            }
            catch (Exception ex)
            {
                if (_closed || generation != _companionSelectionGeneration) return;
                if (automatic) ScheduleCompanionReconnect(ex.Message);
                else
                {
                    _companionReconnectPaused = true;
                    _companionStatus = ex.Message;
                    throw;
                }
            }
        }, $"Connecting to companion on {device.ComputerName}…");
    }

    private void PublishCompanionSession(LanCompanionSession session, LanCompanionDevice device)
    {
        CloseCompanionDesktop();
        _companionSession?.Dispose();
        _companionSession = session;
        _companionStatus = $"Connected: {session.ComputerName}";
        _companionReconnectAttempts = 0;
        _companionNextReconnect = DateTimeOffset.MinValue;
        CompanionCodeInput.Clear();
        ObserveCompanion(AuthenticatedCompanionObservation(device, session));
        SetComputerResult("Companion connection", $"Computer: {session.ComputerName}\nOperating system: {session.OperatingSystem}\nConnection: {session.Address} · paired TLS\nAuthenticated: {DateTime.Now:g}");
        SetStatus($"Connected to companion on {session.ComputerName}.");
    }

    private static LanCompanionDevice AuthenticatedCompanionObservation(LanCompanionDevice device, LanCompanionSession session) =>
        device with
        {
            Address = session.Address,
            Advertisement = device.Advertisement with { ComputerName = session.ComputerName },
            LastSeen = DateTimeOffset.UtcNow
        };

    private async void CompanionConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false, IsCompanionFresh: true, Companion: { } device } || !IsCompanionPaired(device)) return;
        await ConnectCompanionAsync(device, automatic: false);
    }

    private async void CompanionPair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _controllerFingerprint.Length == 0 || Selected is not { IsLocal: false, IsCompanionFresh: true, Companion: { PairingAvailable: true } device } || HasSavedCompanionPairing(device)) return;
        var code = CompanionCodeInput.Password;
        if (string.IsNullOrWhiteSpace(code)) { SetStatus("Enter the code displayed by the remote companion.", error: true); CompanionCodeInput.Focus(); return; }
        var generation = _companionSelectionGeneration;
        await RunAsync(async operationToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _companionSelectionCancellation.Token);
            _companionStatus = "Waiting for matching fingerprint approval on remote PC…";
            UpdateControls();
            try
            {
                var session = await Task.Run(() => _companionClient.PairAsync(device, code, linked.Token), linked.Token);
                if (_closed || linked.IsCancellationRequested || generation != _companionSelectionGeneration || Selected?.Companion?.DeviceId != device.DeviceId || Selected?.Companion?.Address != device.Address)
                {
                    session.Dispose();
                    linked.Token.ThrowIfCancellationRequested();
                    return;
                }
                _companionReconnectPaused = false;
                PublishCompanionSession(session, device);
            }
            catch (Exception ex)
            {
                if (!_closed && generation == _companionSelectionGeneration) _companionStatus = ex is OperationCanceledException ? "Pairing canceled." : ex.Message;
                throw;
            }
            finally { if (!_closed) CompanionCodeInput.Clear(); }
        }, $"Pairing with {device.ComputerName}. Approve matching Launcher fingerprint on remote PC…");
    }

    private void CompanionCode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CompanionPair_Click(sender, new RoutedEventArgs());
    }

    private void CompanionDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SignOutCompanion(clearCode: true);
        _companionReconnectPaused = true;
        _companionStatus = "Disconnected. Pairing retained; select Connect to reconnect.";
        SetStatus("Companion disconnected.");
        UpdateControls();
    }

    private async void CompanionForget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false, Companion: { } device }) return;
        await RunAsync(async token =>
        {
            await _companionClient.ForgetAsync(device.DeviceId, token);
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            SignOutCompanion(clearCode: true);
            _companionReconnectPaused = true;
            _companionStatus = "Pairing forgotten on this Launcher. Revoke this Launcher in the remote companion to remove remote permission.";
            SetStatus("Local companion pairing removed.");
        }, "Removing local companion pairing…");
    }

    private void CompanionSystemInfo_Click(object sender, RoutedEventArgs e) => QueryCompanion(LanRemoteAction.SystemInfo);
    private void CompanionProcesses_Click(object sender, RoutedEventArgs e) => QueryCompanion(LanRemoteAction.Processes);

    private void CompanionDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { IsLocal: false, IsCompanionFresh: true } || _companionSession is not { IsConnected: true, SupportsDesktop: true } session) return;
        if (_companionDesktop != null)
        {
            if (_companionDesktop.WindowState == WindowState.Minimized) _companionDesktop.WindowState = WindowState.Normal;
            _companionDesktop.Activate();
            return;
        }
        var desktop = new LanDesktopWindow(_companionClient, session) { Owner = this };
        _companionDesktop = desktop;
        desktop.Closed += (_, _) => { if (ReferenceEquals(_companionDesktop, desktop)) _companionDesktop = null; };
        desktop.Show();
    }

    private void CloseCompanionDesktop()
    {
        var desktop = _companionDesktop;
        _companionDesktop = null;
        desktop?.Close();
    }

    private async void QueryCompanion(LanRemoteAction action)
    {
        if (_busy || Selected is not { IsLocal: false, IsCompanionFresh: true } || _companionSession is not { IsConnected: true } session) return;
        var generation = _companionSelectionGeneration;
        await RunAsync(async operationToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _companionSelectionCancellation.Token);
            try
            {
                var result = await _companionClient.QueryAsync(session, action, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (_closed || generation != _companionSelectionGeneration || !ReferenceEquals(session, _companionSession)) return;
                if (Selected?.Companion is { } device) ObserveCompanion(AuthenticatedCompanionObservation(device, session));
                SetComputerResult(result.Title, result.Text, reveal: true);
                _companionStatus = $"Connected: {session.ComputerName}";
                SetStatus($"{result.Title} received from {session.ComputerName} · {result.CompletedAt.ToLocalTime():t}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_closed && generation == _companionSelectionGeneration)
                {
                    CloseCompanionDesktop();
                    session.Dispose();
                    _companionSession = null;
                    ScheduleCompanionReconnect(ex.Message);
                }
                throw;
            }
        }, $"Reading {action} from companion on {session.ComputerName}…");
    }

    private void StopCompanionMonitoring()
    {
        CloseCompanionDesktop();
        _companionTimer.Stop();
        _companionTimer.Tick -= CompanionTimer_Tick;
        _companionDiscoveryCancellation?.Cancel();
        _companionSelectionCancellation.Cancel();
        _companionSelectionCancellation.Dispose();
    }
}
