using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using FullStackLauncher.CompanionProtocol;
using Microsoft.Win32.SafeHandles;

namespace FullStackRemoteCompanion;

internal sealed class CompanionService : ServiceBase
{
    private CompanionHost? _host;
    private ServiceControlPipe? _pipe;
    public CompanionService() { ServiceName = ServiceInstallation.ServiceName; CanStop = true; AutoLog = true; }
    protected override void OnStart(string[] args)
    {
        try
        {
            var owner = ServiceInstallation.ReadOwnerSid();
            _host = new CompanionHost(true); _host.Start();
            if (!_host.Snapshot().Listening) throw new InvalidOperationException(_host.Snapshot().Status);
            _pipe = new ServiceControlPipe(_host, owner); _pipe.Start();
        }
        catch { ShutdownAsync().GetAwaiter().GetResult(); throw; }
    }
    protected override void OnStop() { RequestAdditionalTime(10000); ShutdownAsync().GetAwaiter().GetResult(); }
    private async Task ShutdownAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
        _pipe = null; _host = null;
    }
}

internal sealed class ServiceControlPipe : IAsyncDisposable
{
    private readonly CompanionHost _host;
    private readonly string _ownerSid;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private Task? _loop;
    public ServiceControlPipe(CompanionHost host, string ownerSid)
    {
        _host = host; _ownerSid = ownerSid;
        // Hold the first instance continuously so another account cannot replace the local endpoint.
        _pipe = NamedPipeServerStreamAcl.Create(ServiceInstallation.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, Security());
    }
    public void Start() => _loop = RunAsync(_stop.Token);
    private PipeSecurity Security()
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_ownerSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pipe = _pipe;
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
                // Network-deny ACL is reinforced with a verified local process and impersonated account.
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0 ||
                    !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId) || sessionId == 0) continue;
                var request = await RemoteProtocol.ReadAsync<ControlRequest>(pipe, deadline.Token).ConfigureAwait(false);
                if (request.Action is null || request.Action.Length > 32 || request.Id is null || request.Id.Length > 64) continue;
                var authorized = false;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent(true);
                    if (identity is null || identity.Groups?.Any(g => g.Value == "S-1-5-2") == true) return;
                    authorized = identity.User?.Value == _ownerSid || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                });
                if (!authorized) continue;
                await RemoteProtocol.WriteAsync(pipe, _host.Control(request), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            { if (!token.IsCancellationRequested) await Task.Delay(100, token).ConfigureAwait(false); }
            finally { if (_pipe.IsConnected) try { _pipe.Disconnect(); } catch (IOException) { } }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _pipe.Dispose();
        if (_loop is not null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        _stop.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}

internal static class ServiceControlClient
{
    public static async Task<ControlReply> SendAsync(ControlRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", ServiceInstallation.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        NativeServiceIdentity.RequireCurrentServer(pipe.SafePipeHandle);
        await RemoteProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        return await RemoteProtocol.ReadAsync<ControlReply>(pipe, deadline.Token).ConfigureAwait(false);
    }
}

internal static class NativeServiceIdentity
{
    public static void RequireCurrentServer(SafePipeHandle pipe)
    {
        if (!ServiceInstallation.IsInstalled() || !GetNamedPipeServerProcessId(pipe, out var pipePid) || pipePid == 0 ||
            !GetNamedPipeServerSessionId(pipe, out var session) || session != 0) throw new IOException("Companion service pipe identity could not be verified.");
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new IOException("Windows service manager could not be inspected.");
        using var service = OpenService(manager, ServiceInstallation.ServiceName, 4);
        if (service.IsInvalid || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) || status.State != 4 || status.ProcessId != pipePid)
            throw new IOException("Companion control pipe is not owned by the installed running service.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel, out ServiceStatus status, int size, out int bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
}
