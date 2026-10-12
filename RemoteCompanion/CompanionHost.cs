using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CompanionProtocol;

namespace FullStackRemoteCompanion;

internal sealed record PendingApproval(string Id, string ControllerName, string Fingerprint, string Address, DateTimeOffset ExpiresAt);
internal sealed record CompanionSnapshot(string DeviceId, string Fingerprint, bool Listening, string Status, string PairingCode,
    DateTimeOffset? PairingExpiresAt, PendingApproval? Pending, ApprovedController[] Controllers, bool ServiceMode,
    bool DesktopAllowed = false, bool DesktopActive = false, string DesktopStatus = "");
internal sealed record ControlRequest(string Action, string Id = "");
internal sealed record ControlReply(bool Success, string Message, CompanionSnapshot? Snapshot);

internal sealed class CompanionHost : IAsyncDisposable
{
    private readonly CompanionStore _store;
    private readonly bool _service;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(16);
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sourceSessions = new(StringComparer.Ordinal);
    private UdpClient? _udp;
    private TcpListener? _listener;
    private Task? _discovery;
    private Task? _accept;
    private Task? _shutdownCompletion;
    private long _sessionId;
    private bool _listening;
    private string _status = "Starting companion listener…";
    private string _code = "";
    private DateTimeOffset _codeExpires;
    private PendingApproval? _pending;
    private TaskCompletionSource<bool>? _approval;
    private bool _desktopAllowed;
    private DesktopSession? _desktop;
    private string _desktopStatus = "Allow desktop control to let approved Launchers view and control this signed-in session.";

    public CompanionHost(bool service) { _service = service; _store = new CompanionStore(service); }
    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, RemoteProtocol.ManagementPort);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(16);
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.ExclusiveAddressUse = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, RemoteProtocol.DiscoveryPort));
            lock (_gate) { _listening = true; _status = $"Listening · UDP {RemoteProtocol.DiscoveryPort} · TLS {RemoteProtocol.ManagementPort}"; }
            _discovery = DiscoveryLoopAsync(_stop.Token);
            _accept = AcceptLoopAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _listener?.Stop(); _udp?.Dispose();
            lock (_gate) { _listening = false; _status = "Listener failed: " + ex.Message; }
        }
    }
    public CompanionSnapshot Snapshot()
    {
        lock (_gate)
        {
            var active = _code.Length != 0 && DateTimeOffset.UtcNow < _codeExpires;
            return new(_store.DeviceId, _store.Fingerprint, _listening, _status, active ? _code : "", active ? _codeExpires : null,
                _pending, _store.Controllers, _service, _desktopAllowed, _desktop is not null,
                _service ? "Desktop control requires the tray listener. Remove the background service to use desktop control." : _desktopStatus);
        }
    }
    public ControlReply Control(ControlRequest request)
    {
        try
        {
            switch (request.Action)
            {
                case "snapshot": break;
                case "enablePairing":
                    lock (_gate)
                    {
                        if (!_listening) throw new InvalidOperationException("Start the listener successfully before enabling pairing.");
                        if (_pending is not null) throw new InvalidOperationException("Accept or deny the pending pairing request first.");
                        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
                        _code = new string(Enumerable.Range(0, 12).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
                        _codeExpires = DateTimeOffset.UtcNow.AddMinutes(5);
                    }
                    break;
                case "disablePairing": lock (_gate) { _code = ""; _approval?.TrySetResult(false); } break;
                case "approvePairing": case "denyPairing":
                    lock (_gate)
                    {
                        if (_pending?.Id != request.Id || DateTimeOffset.UtcNow >= _pending.ExpiresAt) throw new InvalidOperationException("Pairing request has expired or changed.");
                        _approval?.TrySetResult(request.Action == "approvePairing");
                    }
                    break;
                case "revoke":
                    if (!RemoteProtocol.ValidFingerprint(request.Id)) throw new InvalidOperationException("Select a paired PC to revoke.");
                    _store.Revoke(request.Id);
                    StopDesktop("Desktop viewer stopped because a Launcher approval was revoked.");
                    break;
                case "allowDesktop":
                    if (_service || !DesktopNative.InteractiveProcess) throw new InvalidOperationException("Desktop control requires the signed-in user's tray listener. Windows service mode cannot control a desktop.");
                    lock (_gate) { _desktopAllowed = true; _desktopStatus = "Desktop control allowed for approved Launchers while this tray listener runs."; }
                    break;
                case "denyDesktop":
                    lock (_gate) _desktopAllowed = false;
                    StopDesktop("Desktop control is off.");
                    break;
                case "stopDesktop":
                    // Also disable reconnect until the local user explicitly allows access again.
                    lock (_gate) _desktopAllowed = false;
                    StopDesktop("Desktop viewer stopped locally. Allow desktop control to accept another viewer.");
                    break;
                default: throw new InvalidOperationException("Unsupported local control action.");
            }
            return new(true, "", Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        { return new(false, ex.Message, Snapshot()); }
    }
    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        try
        {
            long discoverySecond = 0;
            var discoveryCount = 0;
            while (!token.IsCancellationRequested)
            {
                var datagram = await _udp!.ReceiveAsync(token).ConfigureAwait(false);
                var second = Environment.TickCount64 / 1000;
                if (second != discoverySecond) { discoverySecond = second; discoveryCount = 0; }
                if (++discoveryCount > 64) continue;
                if (datagram.Buffer.Length is < 1 or > RemoteProtocol.MaximumDiscoveryBytes || !RemoteProtocol.SamePrivateLan(datagram.RemoteEndPoint.Address)) continue;
                CompanionDiscoveryQuery? query;
                try { query = JsonSerializer.Deserialize<CompanionDiscoveryQuery>(datagram.Buffer, RemoteProtocol.Json); }
                catch (JsonException) { continue; }
                if (query is null || query.Kind != RemoteProtocol.DiscoveryKind || query.Version != RemoteProtocol.Version ||
                    !Guid.TryParseExact(query.Nonce, "N", out _)) continue;
                var snapshot = Snapshot();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new CompanionAdvertisement
                {
                    Nonce = query.Nonce, DeviceId = snapshot.DeviceId, ComputerName = RemoteProtocol.CleanName(Environment.MachineName),
                    CertificateFingerprint = snapshot.Fingerprint, PairingAvailable = snapshot.PairingCode.Length != 0
                }, RemoteProtocol.Json);
                if (bytes.Length <= RemoteProtocol.MaximumDiscoveryBytes) await _udp.SendAsync(bytes, datagram.RemoteEndPoint, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { if (!token.IsCancellationRequested) ListenerError(ex); }
    }
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) { client.Dispose(); break; }
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                var local = (IPEndPoint)client.Client.LocalEndPoint!;
                if (!RemoteProtocol.SamePrivateLan(remote.Address, local.Address) || !_slots.Wait(0)) { client.Dispose(); continue; }
                var address = remote.Address.ToString();
                var reserved = false;
                lock (_gate)
                {
                    var existing = _sourceSessions.GetValueOrDefault(address);
                    if (existing < 2) { _sourceSessions[address] = existing + 1; reserved = true; }
                }
                if (!reserved) { client.Dispose(); _slots.Release(); continue; }
                var id = Interlocked.Increment(ref _sessionId);
                var task = ServeAsync(client, address, token);
                _sessions[id] = task;
                _ = RemoveSessionAsync(id, address, task);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { if (!token.IsCancellationRequested) ListenerError(ex); }
    }
    private async Task RemoveSessionAsync(long id, string address, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* An isolated client must never stop other companion sessions. */ }
        finally
        {
            _sessions.TryRemove(id, out _);
            lock (_gate)
            {
                var count = _sourceSessions.GetValueOrDefault(address);
                if (count <= 1) _sourceSessions.Remove(address); else _sourceSessions[address] = count - 1;
            }
            _slots.Release();
        }
    }
    private void ListenerError(Exception ex)
    {
        lock (_gate) { _listening = false; _status = "Listener stopped: " + ex.Message; }
        _stop.Cancel(); _listener?.Stop(); _udp?.Dispose();
    }
    private static bool AcceptClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        try
        {
            using var cert = new X509Certificate2(certificate);
            return cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        }
        catch (CryptographicException) { return false; }
    }
    private async Task ServeAsync(TcpClient client, string address, CancellationToken token)
    {
        using (client)
        using (var ssl = new SslStream(client.GetStream(), false, AcceptClientCertificate))
        {
            try
            {
                client.NoDelay = true;
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _store.Certificate, ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false
                    }, handshake.Token).ConfigureAwait(false);
                }
                // Requesting a TLS certificate is insufficient: reject absent certificates explicitly.
                if (ssl.RemoteCertificate is null || !AcceptClientCertificate(ssl, ssl.RemoteCertificate, null, SslPolicyErrors.None)) return;
                var fingerprint = RemoteProtocol.Fingerprint(ssl.RemoteCertificate);
                while (!token.IsCancellationRequested)
                {
                    var approved = _store.IsApproved(fingerprint);
                    if (!approved && !PairingAvailableFor(fingerprint)) return;
                    CompanionRequest request;
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(token))
                    { read.CancelAfter(TimeSpan.FromSeconds(approved ? 90 : 5)); request = await RemoteProtocol.ReadAsync<CompanionRequest>(ssl, read.Token).ConfigureAwait(false); }
                    if (!AcceptClientCertificate(ssl, ssl.RemoteCertificate, null, SslPolicyErrors.None)) return;
                    var response = await DispatchAsync(request, fingerprint, address, token).ConfigureAwait(false);
                    if (request.Action == "desktopOpen" && response.Success)
                    {
                        await ServeDesktopAsync(ssl, request, fingerprint, token).ConfigureAwait(false);
                        return;
                    }
                    using (var write = CancellationTokenSource.CreateLinkedTokenSource(token))
                    { write.CancelAfter(TimeSpan.FromSeconds(20)); await RemoteProtocol.WriteAsync(ssl, response, write.Token).ConfigureAwait(false); }
                    if (!response.Success && response.ErrorCode == "NotPaired") return;
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or JsonException or CryptographicException or InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException or ArgumentException) { }
        }
    }
    private bool PairingAvailableFor(string fingerprint)
    {
        lock (_gate) return _code.Length != 0 && DateTimeOffset.UtcNow < _codeExpires || _pending?.Fingerprint == fingerprint && DateTimeOffset.UtcNow < _pending.ExpiresAt;
    }
    private async Task<CompanionResponse> DispatchAsync(CompanionRequest request, string fingerprint, string address, CancellationToken token)
    {
        if (request.Version != RemoteProtocol.Version || !Guid.TryParseExact(request.RequestId, "N", out _) || request.DeviceId != _store.DeviceId ||
            request.Action is not ("pair" or "ping" or "systemInfo" or "processes" or "desktopOpen")) return Reply(request, false, "InvalidRequest", "Companion request is invalid.");
        if (request.Action == "pair") return await PairAsync(request, fingerprint, address, token).ConfigureAwait(false);
        // Recheck the durable current approval on every operation, including an existing TLS session.
        if (!_store.IsApproved(fingerprint)) return Reply(request, false, "NotPaired", "This Launcher is not paired, or its approval was revoked.");
        if (request.Action == "desktopOpen") return Reply(request, true, "", "Opening desktop.");
        string text;
        try { text = request.Action switch
        {
            "systemInfo" => NativeReads.SystemInfo(),
            "processes" => NativeReads.Processes(),
            _ => $"{Environment.MachineName} · Companion {RemoteProtocol.Version} · {DateTimeOffset.UtcNow:O}"
        }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Reply(request, false, "ReadFailed", "Windows information could not be read."); }
        return Reply(request, true, "", "Connected.") with { Text = text, OperatingSystem = RuntimeInformation.OSDescription };
    }
    private async Task<CompanionResponse> PairAsync(CompanionRequest request, string fingerprint, string address, CancellationToken token)
    {
        TaskCompletionSource<bool> approval;
        PendingApproval pending;
        string pairingCode;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _attempts.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _attempts.Remove(key);
            if (_attempts.TryGetValue(address, out var attempts) && attempts.Count >= 5 || _attempts.Count >= 256 && !_attempts.ContainsKey(address))
                return Reply(request, false, "RateLimited", "Too many pairing attempts. Wait three minutes before retrying.");
            if (_pending is not null) return Reply(request, false, "PairingBusy", "Another pairing request is awaiting approval.");
            if (_code.Length == 0 || now >= _codeExpires) return Reply(request, false, "PairingUnavailable", "Enable pairing on this PC before connecting.");
            if (!RemoteProtocol.ValidFingerprint(request.PairingProof) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(request.PairingProof),
                    Convert.FromHexString(RemoteProtocol.PairingProof(_code, _store.Fingerprint, fingerprint, _store.DeviceId, request.RequestId))))
            {
                _attempts[address] = (attempts.Count + 1, now.AddMinutes(3));
                return Reply(request, false, "PairingCodeInvalid", "Pairing code is incorrect.");
            }
            var name = RemoteProtocol.CleanName(request.ControllerName);
            if (string.IsNullOrWhiteSpace(name) || name != request.ControllerName) return Reply(request, false, "InvalidRequest", "Controller name is invalid.");
            pending = new(Guid.NewGuid().ToString("N"), name, fingerprint, address, now.AddSeconds(70));
            approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pairingCode = _code; _pending = pending; _approval = approval; _code = "";
        }
        try
        {
            bool accepted;
            try { accepted = await approval.Task.WaitAsync(TimeSpan.FromSeconds(70), token).ConfigureAwait(false); }
            catch (TimeoutException) { return Reply(request, false, "PairingExpired", "Local approval timed out. Enable a new pairing code to retry."); }
            if (!accepted) return Reply(request, false, "PairingDenied", "Pairing was denied on the remote PC.");
            try { _store.Approve(pending.ControllerName, pending.Fingerprint); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
            {
                lock (_gate) _status = "Listening · Approval save failed: " + ex.Message;
                return Reply(request, false, "PairingSaveFailed", "Approval could not be saved. Check the remote companion window.");
            }
            return Reply(request, true, "", "Pairing approved and saved.") with
            { OperatingSystem = RuntimeInformation.OSDescription, PairingProof = RemoteProtocol.PairingProof(pairingCode, _store.Fingerprint, fingerprint, _store.DeviceId, request.RequestId, serverProof: true) };
        }
        finally { lock (_gate) { if (_pending?.Id == pending.Id) { _pending = null; _approval = null; } } }
    }
    private CompanionResponse Reply(CompanionRequest request, bool success, string code, string message) => new()
    {
        RequestId = request.RequestId ?? "", Success = success, ErrorCode = code, Message = message, DeviceId = _store.DeviceId,
        ComputerName = RemoteProtocol.CleanName(Environment.MachineName),
        // Older clients reject unknown capabilities. Only new clients opt in to desktop discovery.
        Capabilities = (request.DesktopVersion == 1 || request.Action == "desktopOpen") && !_service && DesktopNative.InteractiveProcess
            ? ["systemInfo", "processes", "desktop-v1"] : ["systemInfo", "processes"]
    };
    private void StopDesktop(string status)
    {
        DesktopSession? desktop;
        lock (_gate) { desktop = _desktop; _desktopStatus = status; }
        desktop?.Stop();
    }
    private async Task ServeDesktopAsync(SslStream stream, CompanionRequest request, string fingerprint, CancellationToken token)
    {
        DesktopSession? desktop = null;
        CompanionResponse response;
        var errorCode = "DesktopUnavailable";
        try
        {
            if (_service) { errorCode = "DesktopRequiresTray"; throw new InvalidOperationException("Desktop control requires the companion tray listener in a signed-in Windows session. Remove the background service to use desktop control."); }
            using var controllerCertificate = new X509Certificate2(stream.RemoteCertificate!);
            var controllerValidUntil = controllerCertificate.NotAfter.ToUniversalTime();
            var serverValidUntil = _store.Certificate.NotAfter.ToUniversalTime();
            lock (_gate)
            {
                if (!_desktopAllowed) { errorCode = "DesktopDisabled"; throw new InvalidOperationException("Enable Allow desktop control in this PC's companion window before opening its desktop."); }
                if (_desktop is not null) { errorCode = "DesktopBusy"; throw new InvalidOperationException("Another Launcher is viewing this desktop. Stop its viewer in this PC's companion window first."); }
                desktop = new DesktopSession(request.DesktopOptions ?? new(), RemoteProtocol.CleanName(request.ControllerName),
                    () => { lock (_gate) return _desktopAllowed && _store.IsApproved(fingerprint) && !_stop.IsCancellationRequested && DateTime.UtcNow < controllerValidUntil && DateTime.UtcNow < serverValidUntil; }, token);
                _desktop = desktop;
                _desktopStatus = "Desktop viewer connected · " + (desktop.ControllerName.Length == 0 ? "Approved Launcher" : desktop.ControllerName);
            }
            response = Reply(request, true, "", "Desktop connected.") with { Monitors = desktop.Monitors, OperatingSystem = RuntimeInformation.OSDescription };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { response = Reply(request, false, errorCode, ex.Message); }
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            { deadline.CancelAfter(TimeSpan.FromSeconds(5)); await RemoteProtocol.WriteAsync(stream, response, deadline.Token).ConfigureAwait(false); }
            if (desktop is not null) await desktop.RunAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException or ArgumentException or JsonException)
        {
            lock (_gate)
                if (_desktopAllowed) _desktopStatus = ex is OperationCanceledException ? "Desktop viewer disconnected." : "Desktop viewer stopped: " + ex.Message;
        }
        finally
        {
            desktop?.Dispose();
            lock (_gate)
            {
                if (desktop is not null && ReferenceEquals(_desktop, desktop))
                { _desktop = null; if (_desktopStatus.StartsWith("Desktop viewer connected", StringComparison.Ordinal)) _desktopStatus = "Desktop viewer disconnected."; }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task completion;
        lock (_gate)
        {
            if (_shutdownCompletion is null)
            {
                _stop.Cancel(); _listener?.Stop(); _udp?.Dispose();
                _desktopAllowed = false;
                _approval?.TrySetResult(false); _code = ""; _listening = false; _status = "Listener stopped.";
                _shutdownCompletion = DisposeAfterSessionsAsync();
            }
            completion = _shutdownCompletion;
        }
        try { await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
    }
    public bool ShutdownPending { get { lock (_gate) return _shutdownCompletion is { IsCompleted: false }; } }
    private async Task DisposeAfterSessionsAsync()
    {
        // Stop accepting before capturing sessions so no late accepted client escapes shutdown.
        try { await Task.WhenAll(_discovery ?? Task.CompletedTask, _accept ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception) { }
        try
        {
            await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally { _store.Dispose(); _stop.Dispose(); }
    }
}

internal static class NativeReads
{
    public static string SystemInfo()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var known = GlobalMemoryStatusEx(ref memory);
        var text = new StringBuilder().AppendLine("Computer: " + Environment.MachineName).AppendLine("Windows: " + RuntimeInformation.OSDescription)
            .AppendLine("Architecture: " + RuntimeInformation.OSArchitecture).AppendLine("Logical processors: " + Environment.ProcessorCount)
            .AppendLine("Uptime: " + TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss"));
        if (known) text.AppendLine($"Memory: {memory.TotalPhysical / 1024 / 1024:N0} MB total · {memory.AvailablePhysical / 1024 / 1024:N0} MB available");
        return text.ToString();
    }
    public static string Processes()
    {
        var entries = new List<(int Id, string Name, long Memory)>();
        var skipped = 0;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes.Take(4096))
                try { entries.Add((process.Id, RemoteProtocol.CleanName(process.ProcessName, 100), process.WorkingSet64)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { skipped++; }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        var lines = entries.OrderByDescending(p => p.Memory).ThenBy(p => p.Id).Take(150)
            .Select(p => $"{p.Id,7}  {p.Name,-36}  {p.Memory / 1024 / 1024,8:N0} MB");
        return "PID      Process                                    Memory\n" + string.Join("\n", lines) +
            $"\nTop {Math.Min(entries.Count, 150)} by memory · {skipped} inaccessible or exited" + (processes.Length > 4096 ? " · Snapshot limited to 4,096 processes" : "");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
