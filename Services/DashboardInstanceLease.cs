using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.AgentBridge;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>Serializes dashboard replacement without terminating service or helper processes.</summary>
internal sealed class DashboardInstanceLease : IDisposable
{
    private const string LegacyGuidance = "This launcher version cannot hand off automatically. Close it once with Leave running & exit, then open the new version.";
    private const string UnavailableGuidance = "The running launcher could not receive the handoff request. Close it with Leave running & exit, then open the new version.";
    private const string TimeoutGuidance = "Launcher replacement timed out. Running apps were not stopped. Check the existing launcher, then open the new version again.";
    private static readonly TimeSpan ReplacementTimeout = TimeSpan.FromSeconds(180);
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly Mutex _startup;
    private readonly Mutex _dashboard;
    private bool _ownsStartup;
    private bool _ownsDashboard;
    private bool _disposed;

    private DashboardInstanceLease(Dispatcher dispatcher, string settingsPath)
    {
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _pipeName = AgentBridgeProtocol.PipeName(settingsPath);
        _startup = new Mutex(false, @"Local\" + _pipeName + ".DashboardStartup");
        try { _dashboard = new Mutex(false, @"Local\" + _pipeName + ".DashboardOwner"); }
        catch { _startup.Dispose(); throw; }
    }

    internal static async Task<DashboardInstanceLease> AcquireAsync(string settingsPath,
        CancellationToken token = default)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Dashboard startup requires the application dispatcher.");
        dispatcher.VerifyAccess();
        DashboardInstanceLease? lease = null;
        try
        {
            lease = new DashboardInstanceLease(dispatcher, settingsPath);
            // The dispatcher operation supplies a WPF synchronization context even if
            // startup's caller has not installed one. Mutex ownership is thread-affine.
            await dispatcher.InvokeAsync(() => lease.AcquireCoreAsync(token)).Task.Unwrap();
            return lease;
        }
        catch (Exception ex)
        {
            if (lease is not null) await dispatcher.InvokeAsync(lease.Dispose);
            if (ex is Win32Exception or UnauthorizedAccessException)
                throw new IOException(UnavailableGuidance);
            throw;
        }
    }

    private async Task AcquireCoreAsync(CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var deadline = DateTimeOffset.UtcNow.Add(ReplacementTimeout).ToUnixTimeMilliseconds();
        timeout.CancelAfter(ReplacementTimeout);
        try
        {
            await WaitForClaimAsync(_startup, dashboard: false, timeout.Token);
            TryClaim(_dashboard, ref _ownsDashboard);
            // Older dashboards know only the agent-bridge mutex. Check that mutex
            // even when this version's dashboard-ownership mutex was available.
            while (!_ownsDashboard || HasBridgeOwner())
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var previous = await RequestReplacementAsync(deadline, timeout.Token);
                _dispatcher.VerifyAccess();
                if (previous is not null)
                {
                    await previous.WaitForExitAsync(timeout.Token);
                    _dispatcher.VerifyAccess();
                    if (!previous.HasExited)
                        throw new IOException("The previous launcher has not confirmed exit. Running apps were not stopped.");
                }
                await WaitForClaimAsync(_dashboard, dashboard: true, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException(TimeoutGuidance);
        }
    }

    private bool TryClaim(Mutex mutex, ref bool owned)
    {
        _dispatcher.VerifyAccess();
        if (owned) return true;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        return owned;
    }

    private async Task WaitForClaimAsync(Mutex mutex, bool dashboard, CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (dashboard ? TryClaim(mutex, ref _ownsDashboard) : TryClaim(mutex, ref _ownsStartup)) return;
            await Task.Delay(100, token);
            _dispatcher.VerifyAccess();
        }
    }

    private bool HasBridgeOwner()
    {
        _dispatcher.VerifyAccess();
        using var bridge = new Mutex(false, @"Local\" + _pipeName);
        var acquired = false;
        try
        {
            try { acquired = bridge.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            return !acquired;
        }
        finally { if (acquired) bridge.ReleaseMutex(); }
    }

    private async Task<Process?> RequestReplacementAsync(long deadline, CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? previous = null;
        var requestSent = false;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(3));
                try { await pipe.ConnectAsync(connect.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException(UnavailableGuidance);
                }
            }
            _dispatcher.VerifyAccess();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) ||
                processId <= 4 || processId > int.MaxValue || processId == Environment.ProcessId)
                throw new InvalidOperationException("The running launcher process could not be identified. Running apps were not stopped.");
            previous = Process.GetProcessById((int)processId);
            // Opening the native handle pins this exact process. Do not re-open a
            // PID after the request: Windows may already have reused that PID.
            _ = previous.Handle;
            _ = previous.StartTime.ToUniversalTime().Ticks;
            using var current = Process.GetCurrentProcess();
            if (previous.SessionId != current.SessionId)
                throw new InvalidOperationException("The running launcher belongs to another desktop session. Running apps were not stopped.");

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(new AgentBridgeRequest
            {
                Action = "replace_dashboard",
                HandoffDeadlineUnixMilliseconds = deadline
            }, AgentBridgeProtocol.Json);
            await writer.WriteLineAsync(payload.AsMemory(), token);
            requestSent = true;
            var line = await ReadBoundedLineAsync(reader, 8192, token);
            if (line is not null)
            {
                AgentBridgeResponse? response;
                try { response = JsonSerializer.Deserialize<AgentBridgeResponse>(line, AgentBridgeProtocol.Json); }
                catch (JsonException)
                {
                    throw new InvalidOperationException("The running launcher returned an invalid handoff response. Running apps were not stopped.");
                }
                if (response is null)
                    throw new InvalidOperationException("The running launcher returned an invalid handoff response. Running apps were not stopped.");
                if (!response.Ok && !previous.HasExited)
                {
                    var error = response.Error == "Unknown launcher bridge action." ? LegacyGuidance
                        : string.IsNullOrWhiteSpace(response.Error)
                            ? "The existing launcher could not hand off. Running apps were not stopped. Check the existing launcher and retry."
                            : SensitiveDataProtection.Redact(response.Error);
                    throw new InvalidOperationException(error);
                }
            }
            // Closing the dashboard can cancel its bridge before a reply is sent.
            // EOF is success only after the pinned process actually exits below.
            var result = previous;
            previous = null;
            return result;
        }
        catch (IOException)
        {
            _dispatcher.VerifyAccess();
            if (previous is not null && (requestSent || previous.HasExited))
            {
                var result = previous;
                previous = null;
                return result;
            }
            // The old process may have completed shutdown before a pipe connected.
            if (TryClaim(_dashboard, ref _ownsDashboard) && !HasBridgeOwner()) return null;
            throw new IOException(UnavailableGuidance);
        }
        catch (ArgumentException)
        {
            throw new IOException("The running launcher process could not be verified. Running apps were not stopped.");
        }
        finally { previous?.Dispose(); }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximum,
        CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return text.Length == 0 ? null : text.ToString().TrimEnd('\r');
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n') return text.ToString().TrimEnd('\r');
                if (text.Length >= maximum)
                    throw new InvalidOperationException("The running launcher returned an oversized handoff response. Running apps were not stopped.");
                text.Append(buffer[index]);
            }
        }
    }

    internal void CompleteStartup()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_ownsStartup) return;
        _startup.ReleaseMutex();
        _ownsStartup = false;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_ownsDashboard) _dashboard.ReleaseMutex();
        }
        finally
        {
            _ownsDashboard = false;
            try { CompleteStartupForDispose(); }
            finally { _dashboard.Dispose(); _startup.Dispose(); }
        }
    }

    private void CompleteStartupForDispose()
    {
        if (!_ownsStartup) return;
        _startup.ReleaseMutex();
        _ownsStartup = false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
