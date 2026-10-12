using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FullStackLauncher.CompanionProtocol;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Persistent, mutually authenticated companion connections. Pairing is never automatically repeated.</summary>
public sealed partial class LanCompanionClient
{
    private readonly LanCompanionTrustStore _store = new();
    private readonly LanCompanionDiscoveryService _discovery = new();
    private readonly object _cacheGate = new();
    private IReadOnlyList<LanCompanionTrust> _trustCache = [];
    private string? _trustReadError;
    private string? _controllerFingerprint;

    public string ControllerFingerprint
    {
        get
        {
            lock (_cacheGate) { if (_controllerFingerprint is not null) return _controllerFingerprint; }
            using var certificate = _store.GetControllerCertificate();
            var fingerprint = RemoteProtocol.Fingerprint(certificate);
            var trust = _store.ReadTrustedDevices();
            lock (_cacheGate)
            {
                if (_controllerFingerprint is not null && _controllerFingerprint != fingerprint)
                    throw new InvalidOperationException("Controller identity changed. Refresh the Launcher before approving pairing.");
                _trustCache = trust;
                _trustReadError = null;
                return _controllerFingerprint = fingerprint;
            }
        }
    }

    public bool IsPaired(LanCompanionDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var trust = CachedTrust(device.DeviceId);
        return trust is not null && trust.CertificateFingerprint.Equals(device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasSavedPairing(string deviceId) => CachedTrust(deviceId) is not null;

    public async Task RefreshTrustAsync(CancellationToken cancellationToken)
    {
        try
        {
            var trust = await Task.Run(_store.ReadTrustedDevices, cancellationToken).ConfigureAwait(false);
            lock (_cacheGate) { _trustCache = trust; _trustReadError = null; }
        }
        catch (InvalidOperationException exception)
        {
            lock (_cacheGate) { _trustCache = []; _trustReadError = exception.Message; }
            throw;
        }
    }

    public string DescribeTrust(LanCompanionDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var trust = CachedTrust(device.DeviceId);
        return trust is null ? "Pairing approval required" :
            trust.CertificateFingerprint.Equals(device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase) ? "Paired" :
            "Advertised identity changed. Review the remote PC before forgetting its saved pairing.";
    }

    public async Task<LanCompanionSession> PairAsync(LanCompanionDevice device, string code, CancellationToken cancellationToken)
    {
        LanCompanionDiscoveryService.ValidateDevice(device);
        if (code is null || code.Length > 128) throw new ArgumentException("Enter the 12-character pairing code shown by the companion.", nameof(code));
        var existing = await Task.Run(() => _store.Find(device.DeviceId), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException(existing.CertificateFingerprint.Equals(device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase)
                ? "This companion is already paired. Choose Connect."
                : "Saved companion certificate differs from its advertisement. Review the remote PC and forget its old pairing before pairing again.");
        if (!device.PairingAvailable) throw new InvalidOperationException("Enable pairing on the companion, then refresh this computer.");
        var certificate = await Task.Run(_store.GetControllerCertificate, cancellationToken).ConfigureAwait(false);
        var session = new LanCompanionSession(this, device.Address,
            new(device.DeviceId, device.CertificateFingerprint.ToUpperInvariant(), device.ComputerName), certificate);
        var controllerFingerprint = RemoteProtocol.Fingerprint(certificate);
        lock (_cacheGate)
        {
            if (_controllerFingerprint is not null && _controllerFingerprint != controllerFingerprint)
            {
                session.Dispose();
                throw new InvalidOperationException("Controller identity changed. Refresh the Launcher before approving pairing.");
            }
            _controllerFingerprint = controllerFingerprint;
        }
        var approved = false;
        try
        {
            var request = Request(session, "pair");
            request = request with { PairingProof = RemoteProtocol.PairingProof(code, session.Trust.CertificateFingerprint,
                controllerFingerprint, session.DeviceId, request.RequestId) };
            var expectedProof = RemoteProtocol.PairingProof(code, session.Trust.CertificateFingerprint,
                controllerFingerprint, session.DeviceId, request.RequestId, serverProof: true);
            var response = await ExecuteOnceAsync(session, request, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            if (!RemoteProtocol.ValidFingerprint(response.PairingProof) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(response.PairingProof), Convert.FromHexString(expectedProof)))
                throw new InvalidOperationException("Companion pairing proof failed. Local trust was not saved. Review the remote PC before trying again.");
            approved = true;
            var trust = session.Trust with { ComputerName = response.ComputerName };
            try { await Task.Run(() => _store.Remember(trust, controllerFingerprint), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
                throw new InvalidOperationException("Companion approved pairing, but local trust was not confirmed saved. Check this controller on the remote PC before pairing again.", exception);
            }
            await RefreshTrustAsync(CancellationToken.None).ConfigureAwait(false);
            return session;
        }
        catch (OperationCanceledException) when (!approved)
        {
            session.Dispose();
            throw new InvalidOperationException("Pairing was interrupted. Approval outcome is uncertain; check the companion's approved controllers before trying again.");
        }
        catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or JsonException)
        {
            session.Dispose();
            throw new InvalidOperationException("Pairing connection failed. Approval outcome is uncertain; check the companion's approved controllers before trying again.", exception);
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<LanCompanionSession> ConnectAsync(LanCompanionDevice device, CancellationToken cancellationToken)
    {
        LanCompanionDiscoveryService.ValidateDevice(device);
        var trust = await Task.Run(() => _store.Find(device.DeviceId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This companion is not paired. Enable pairing on the remote PC and approve this Launcher first.");
        var certificate = await Task.Run(_store.GetControllerCertificate, cancellationToken).ConfigureAwait(false);
        // Discovery selects an address only. Authentication always uses the saved certificate and device ID.
        var session = new LanCompanionSession(this, device.Address, trust, certificate);
        try { await PingAsync(session, cancellationToken).ConfigureAwait(false); return session; }
        catch { session.Dispose(); throw; }
    }

    public async Task<LanRemoteCommandResult> QueryAsync(LanCompanionSession session, LanRemoteAction action, CancellationToken cancellationToken)
    {
        if (action is not (LanRemoteAction.SystemInfo or LanRemoteAction.Processes)) throw new ArgumentOutOfRangeException(nameof(action));
        var response = await ExecuteReadAsync(session, action == LanRemoteAction.SystemInfo ? "systemInfo" : "processes", cancellationToken).ConfigureAwait(false);
        return new(action == LanRemoteAction.SystemInfo ? "System information" : "Processes", response.Text, DateTimeOffset.UtcNow);
    }

    public async Task PingAsync(LanCompanionSession session, CancellationToken cancellationToken) =>
        _ = await ExecuteReadAsync(session, "ping", cancellationToken).ConfigureAwait(false);

    public async Task ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        await Task.Run(() => _store.Forget(deviceId), cancellationToken).ConfigureAwait(false);
        await RefreshTrustAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private LanCompanionTrust? CachedTrust(string deviceId)
    {
        lock (_cacheGate)
        {
            if (_trustReadError is not null) throw new InvalidOperationException(_trustReadError);
            return _trustCache.SingleOrDefault(item => item.DeviceId == deviceId);
        }
    }

    private async Task<CompanionResponse> ExecuteReadAsync(LanCompanionSession session, string action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed(this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken);
        await session.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            session.ThrowIfDisposed(this);
            var trust = await Task.Run(() => _store.Find(session.DeviceId), lifetime.Token).ConfigureAwait(false);
            if (trust is null || !trust.CertificateFingerprint.Equals(session.Trust.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                session.DropConnection();
                throw new InvalidOperationException("Saved companion pairing was removed or changed. Review pairing before reconnecting.");
            }
            if (!LanDiscoveryService.IsAllowedLanAddress(session.Address) || LanDiscoveryService.IsLocalAddress(session.Address))
            {
                session.DropConnection();
                var rediscovered = await RediscoverAsync(session.Trust, lifetime.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Paired companion is no longer reachable on a directly connected private IPv4 LAN. Refresh discovery.");
                session.Address = rediscovered.Address;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await ExecuteOnceAsync(session, Request(session, action), TimeSpan.FromSeconds(20), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException ||
                    exception is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    session.DropConnection();
                    if (attempt == 1)
                        throw new InvalidOperationException(exception is AuthenticationException
                            ? "Companion certificate verification failed. Saved identity was preserved; review the remote PC, certificate expiry, and both computers' clocks."
                            : "Companion connection failed. Check that the remote PC is awake, companion is running, and its LAN firewall rule allows this connection.", exception);
                    var device = await RediscoverAsync(session.Trust, lifetime.Token).ConfigureAwait(false);
                    if (device is not null) session.Address = device.Address;
                }
                catch { session.DropConnection(); throw; }
            }
            throw new InvalidOperationException("Companion connection could not be restored.");
        }
        finally { session.Gate.Release(); }
    }

    private async Task<LanCompanionDevice?> RediscoverAsync(LanCompanionTrust trust, CancellationToken token)
    {
        var networks = new LanDiscoveryService().GetNetworks().Take(8).ToArray();
        if (networks.Length == 0) throw new InvalidOperationException("No directly connected private IPv4 LAN is available.");
        var discoveries = await Task.WhenAll(networks.Select(network => DiscoverForReconnectAsync(network, token))).ConfigureAwait(false);
        var matches = discoveries.SelectMany(items => items).Where(device => device.DeviceId == trust.DeviceId &&
            device.CertificateFingerprint.Equals(trust.CertificateFingerprint, StringComparison.OrdinalIgnoreCase)).DistinctBy(device => device.Address).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Paired companion advertised multiple LAN addresses. Refresh and select its verified address before reconnecting.");
        return matches.SingleOrDefault();
    }

    private async Task<IReadOnlyList<LanCompanionDevice>> DiscoverForReconnectAsync(LanNetwork network, CancellationToken token)
    {
        try { return await _discovery.DiscoverAsync(network, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException or InvalidDataException) { return []; }
    }

    private static CompanionRequest Request(LanCompanionSession session, string action) => new()
    {
        Action = action, DeviceId = session.DeviceId, ControllerName = RemoteProtocol.CleanName(Environment.MachineName), DesktopVersion = 1
    };

    private static async Task<CompanionResponse> ExecuteOnceAsync(LanCompanionSession session, CompanionRequest request, TimeSpan duration, CancellationToken token)
    {
        session.ThrowIfDisposed(session.Owner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, session.LifetimeToken);
        timeout.CancelAfter(duration);
        var stream = await session.EnsureConnectionAsync(timeout.Token).ConfigureAwait(false);
        await RemoteProtocol.WriteAsync(stream, request, timeout.Token).ConfigureAwait(false);
        CompanionResponse response;
        try { response = await RemoteProtocol.ReadAsync<CompanionResponse>(stream, timeout.Token).ConfigureAwait(false); }
        catch (JsonException exception) { throw new InvalidDataException("Companion response is invalid.", exception); }
        if (response.Version != RemoteProtocol.Version || response.RequestId != request.RequestId || response.DeviceId != session.DeviceId)
            throw new InvalidOperationException("Companion response identity or protocol version changed. Connection was closed; review the paired remote PC.");
        if (response.ErrorCode is null || response.ErrorCode.Length > 80 || response.Message is null || response.Message.Length > 1024 ||
            response.Text is null || response.Text.Length > 262144 || response.Capabilities is null || response.Capabilities.Length > 16 ||
            response.Capabilities.Any(item => item is not ("systemInfo" or "processes" or "ping" or "desktop-v1")) ||
            response.PairingProof is null || response.PairingProof.Length > 64)
            throw new InvalidOperationException("Companion returned an invalid or oversized response. Connection was closed.");
        if (!response.Success) throw new InvalidOperationException(DescribeError(response.ErrorCode));
        if (!LanCompanionDiscoveryService.ValidName(response.ComputerName) || !LanCompanionDiscoveryService.ValidName(response.OperatingSystem, 240))
            throw new InvalidOperationException("Companion identity details could not be verified. Connection was closed.");
        session.ComputerName = response.ComputerName;
        session.OperatingSystem = response.OperatingSystem;
        session.SupportsDesktop = response.Capabilities.Contains("desktop-v1", StringComparer.Ordinal);
        return response;
    }

    private static string DescribeError(string errorCode) => errorCode switch
    {
        "NotPaired" or "AuthorizationDenied" => "Companion denied this controller. Pairing may have been revoked on the remote PC; review approved controllers there.",
        "PairingUnavailable" => "Companion pairing is disabled or expired. Enable a fresh pairing code on the remote PC.",
        "PairingCodeInvalid" or "PairingProofInvalid" => "Companion rejected the pairing code. Read the current code on the remote PC and try again.",
        "PairingBusy" => "Companion already has a pending approval. Resolve it on the remote PC before trying again.",
        "PairingDenied" => "Pairing was denied on the remote PC.",
        "PairingExpired" => "Remote pairing approval expired. Check approved controllers before enabling a fresh code.",
        "RateLimited" => "Companion temporarily limited requests. Wait before trying again.",
        "PairingSaveFailed" => "Companion could not save protected pairing data. Review its Windows-user storage permissions.",
        "InvalidRequest" => "Companion rejected this request. Check that Launcher and companion use compatible versions.",
        "QueryFailed" or "ReadFailed" => "Companion could not read Windows system information or processes with its current permissions.",
        "DesktopRequiresTray" => "Use the companion tray app in the remote PC's signed-in session. Background service supports management only.",
        "DesktopDisabled" => "Enable Allow desktop control in the companion tray app on the remote PC before opening its screen.",
        "DesktopBusy" => "Another controller already uses this desktop. Close that viewer before connecting here.",
        "DesktopUnavailable" => "Companion cannot capture the current Windows desktop. Unlock the remote PC, check its monitor connection, and keep the companion tray app running in the signed-in session.",
        _ => "Companion could not complete this action. Check its local status."
    };
}

/// <summary>One paired device's authenticated connection. Queries reconnect using this immutable trust identity.</summary>
public sealed class LanCompanionSession : IDisposable
{
    private readonly object _connectionGate = new();
    private readonly X509Certificate2 _controllerCertificate;
    private readonly string _controllerCertificateFingerprint;
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _client;
    private SslStream? _stream;
    private int _disposed;

    internal LanCompanionSession(LanCompanionClient owner, string address, LanCompanionTrust trust, X509Certificate2 controllerCertificate)
    {
        Owner = owner; Address = address; Trust = trust; _controllerCertificate = controllerCertificate;
        _controllerCertificateFingerprint = RemoteProtocol.Fingerprint(controllerCertificate);
        LifetimeToken = _lifetime.Token;
        ComputerName = trust.ComputerName;
    }

    internal LanCompanionClient Owner { get; }
    internal LanCompanionTrust Trust { get; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal CancellationToken LifetimeToken { get; }
    public string DeviceId => Trust.DeviceId;
    public string Address { get; internal set; }
    public string ComputerName { get; internal set; }
    public string OperatingSystem { get; internal set; } = "";
    public bool SupportsDesktop { get; internal set; }
    internal string ControllerCertificateFingerprint => _controllerCertificateFingerprint;
    public bool IsConnected { get { lock (_connectionGate) return _disposed == 0 && _client?.Connected == true && _stream?.IsAuthenticated == true; } }

    internal void ThrowIfDisposed(LanCompanionClient owner)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ReferenceEquals(owner, Owner)) throw new ArgumentException("Companion session belongs to another client.");
    }

    internal async Task<SslStream> EnsureConnectionAsync(CancellationToken token)
    {
        ThrowIfDisposed(Owner);
        lock (_connectionGate) { if (_client?.Connected == true && _stream?.IsAuthenticated == true) return _stream; }
        DropConnection();
        if (!LanDiscoveryService.IsAllowedLanAddress(Address) || LanDiscoveryService.IsLocalAddress(Address))
            throw new InvalidOperationException("Paired companion is outside the current directly connected private IPv4 LAN. Refresh discovery.");
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? stream = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(IPAddress.Parse(Address), RemoteProtocol.ManagementPort, timeout.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) => ValidateServerCertificate(certificate));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "FullStackLauncher Companion",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { _controllerCertificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => _controllerCertificate,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false
            }, timeout.Token).ConfigureAwait(false);
            lock (_connectionGate)
            {
                ThrowIfDisposed(Owner);
                _client = client; _stream = stream;
            }
            return stream;
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    private bool ValidateServerCertificate(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var now = DateTime.UtcNow;
            if (now < presented.NotBefore.ToUniversalTime() || now >= presented.NotAfter.ToUniversalTime()) return false;
            var expected = Convert.FromHexString(Trust.CertificateFingerprint);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.RawData), expected);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException) { return false; }
    }

    internal void DropConnection()
    {
        lock (_connectionGate)
        {
            try { _stream?.Dispose(); } finally { _stream = null; _client?.Dispose(); _client = null; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        DropConnection();
        _controllerCertificate.Dispose();
        _lifetime.Dispose();
    }
}
