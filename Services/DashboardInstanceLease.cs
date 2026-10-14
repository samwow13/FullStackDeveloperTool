using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.ProjectTasks;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>Serializes dashboard replacement without terminating service or helper processes.</summary>
internal sealed class DashboardInstanceLease : IDisposable
{
    private const string LegacyGuidance = "This launcher version cannot hand off automatically. Close it once with Leave running & exit, then open the new version.";
    private const string UnavailableGuidance = "The running launcher could not receive the handoff request. Close it with Leave running & exit, then open the new version.";
    private const string TimeoutGuidance = "Launcher replacement timed out. Running apps were not stopped. Check the existing launcher, then open the new version again.";
    private const string CapabilityGuidance = "The running launcher could not confirm that replacement preserves unfinished work. Existing dashboard and queue work were left running. Retry after its current operation finishes.";
    private const string LegacyQueueGuidance = "The running launcher cannot preserve Notes queue work during this upgrade. Existing dashboard, queue owner and saved queue intent were left untouched. Let active work finish, then review and resolve its queues before retrying replacement.";
    private const string LegacyStateGuidance = "Saved Notes queue state could not be checked safely for this older launcher. Existing dashboard, queues and saved files were preserved. Check local task storage access and retry replacement.";
    private const int MaximumLegacyTaskStoreBytes = 256 * 1024 * 1024;
    private static readonly TimeSpan ReplacementTimeout = TimeSpan.FromSeconds(180);
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly ProjectTaskStore _taskStore;
    private readonly ProjectTaskStore[] _legacyTaskStores;
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
        // Constructors resolve paths only. Respect an explicit --settings even
        // when it names the default settings filename, since task scopes differ.
        _taskStore = Path.GetFullPath(settingsPath).Equals(new SettingsStore().SettingsPath, StringComparison.OrdinalIgnoreCase)
            ? new ProjectTaskStore() : new ProjectTaskStore(settingsPath);
        _legacyTaskStores = Path.GetFullPath(settingsPath).Equals(SettingsStore.DefaultSettingsPath, StringComparison.OrdinalIgnoreCase)
            ? new[] { _taskStore, new ProjectTaskStore(SettingsStore.DefaultSettingsPath),
                    ProjectTaskStore.FromStorePathForRead(ProjectTaskStore.DefaultStorePath) }
                .DistinctBy(store => store.StorePath, StringComparer.OrdinalIgnoreCase).ToArray()
            : [_taskStore];
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
            var previousStartedAt = previous.StartTime.ToUniversalTime().Ticks;
            using var current = Process.GetCurrentProcess();
            if (previous.SessionId != current.SessionId)
                throw new InvalidOperationException("The running launcher belongs to another desktop session. Running apps were not stopped.");

            // Query on a separate connection: the replacement stream remains
            // connected to this exact pinned source throughout the preflight.
            var preservesWork = await QueryPreservationCapabilityAsync(previous, previousStartedAt, token);
            _dispatcher.VerifyAccess();
            var legacySnapshots = preservesWork ? null : CaptureSafeLegacyTaskSnapshots(token);
            VerifyPinnedSource(pipe, previous, previousStartedAt);
            if (legacySnapshots is not null) RecheckLegacyTaskSnapshots(legacySnapshots, token);
            VerifyPinnedSource(pipe, previous, previousStartedAt);
            token.ThrowIfCancellationRequested();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(new AgentBridgeRequest
            {
                Action = "replace_dashboard",
                HandoffDeadlineUnixMilliseconds = deadline,
                ReplacementProcessId = Environment.ProcessId,
                ReplacementProcessStartedAtUtcTicks = current.StartTime.ToUniversalTime().Ticks
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

    private async Task<bool> QueryPreservationCapabilityAsync(Process previous, long previousStartedAt,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var capabilityPipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await capabilityPipe.ConnectAsync(timeout.Token);
            VerifyPinnedSource(capabilityPipe, previous, previousStartedAt);
            using var reader = new StreamReader(capabilityPipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(capabilityPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(new AgentBridgeRequest { Action = "dashboard_handoff_capabilities" }, AgentBridgeProtocol.Json);
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);
            var line = await ReadBoundedLineAsync(reader, 8192, timeout.Token);
            VerifyPinnedSource(capabilityPipe, previous, previousStartedAt);
            if (line is null) throw new InvalidOperationException(CapabilityGuidance);
            AgentBridgeResponse? response;
            try { response = JsonSerializer.Deserialize<AgentBridgeResponse>(line, AgentBridgeProtocol.Json); }
            catch (JsonException) { throw new InvalidOperationException(CapabilityGuidance); }
            if (response is { Ok: false, Error: "Unknown launcher bridge action." }) return false;
            if (response is not { Ok: true, Data: JsonElement data } || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var versionNumber) || versionNumber != 1 ||
                !data.TryGetProperty("sourceProcessId", out var sourceId) || sourceId.ValueKind != JsonValueKind.Number ||
                    !sourceId.TryGetInt32(out var pid) || pid != previous.Id ||
                !data.TryGetProperty("sourceProcessStartedAtUtcTicks", out var sourceStarted) ||
                    sourceStarted.ValueKind != JsonValueKind.Number || !sourceStarted.TryGetInt64(out var startedAt) || startedAt != previousStartedAt ||
                !data.TryGetProperty("queueHandoffProtocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number ||
                    !protocol.TryGetInt32(out var protocolNumber) ||
                    protocolNumber != QueueOwnerClient.ProtocolVersion ||
                !data.TryGetProperty("autosaveAndResume", out var autosave) || autosave.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("preservesQueueAndLoopIntent", out var queueIntent) || queueIntent.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("taskStoreIdentity", out var taskStoreIdentity) || taskStoreIdentity.ValueKind != JsonValueKind.String ||
                    taskStoreIdentity.GetString() != QueueOwnerClient.IdentityFor(_taskStore.StorePath))
                throw new InvalidOperationException(CapabilityGuidance);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(CapabilityGuidance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (previous.HasExited) throw new IOException();
            throw new InvalidOperationException(CapabilityGuidance);
        }
    }

    private static void VerifyPinnedSource(NamedPipeClientStream pipe, Process previous, long startedAt)
    {
        if (previous.HasExited) throw new IOException();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) || processId != (uint)previous.Id ||
            previous.StartTime.ToUniversalTime().Ticks != startedAt)
            throw new InvalidOperationException("The source launcher identity changed during replacement preflight. Existing dashboard and work were preserved; retry replacement.");
    }

    private LegacyTaskSnapshot[] CaptureSafeLegacyTaskSnapshots(CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        var snapshots = new List<LegacyTaskSnapshot>();
        try
        {
            foreach (var store in _legacyTaskStores)
            {
                token.ThrowIfCancellationRequested();
                using var ownerClaim = QueueOwnerClient.TryClaimOwnerMutex(store.StorePath)
                    ?? throw new InvalidOperationException(LegacyQueueGuidance);
                using var file = OpenBoundedLegacyTaskFile(store.StorePath);
                var fingerprint = ValidateCurrentLegacyTaskFile(file);
                // Require current-schema JSON before Load, so even its in-memory
                // migration cannot hide legacy queue flags. Load performs the
                // existing full validation without writes, owner or Codex calls.
                var data = store.Load();
                if (!store.CanSave || store.LoadWarning is not null || file is not null && store.LoadedSourceVersion != 9)
                    throw new InvalidOperationException(LegacyStateGuidance);
                if (data.Queues.Count > 0 || data.QueueItems.Count > 0)
                {
                    var candidate = ProjectTaskStore.Clone(data);
                    ProjectQueueMaintenance.ClearForShutdown(candidate);
                    byte[]? before = null;
                    byte[]? after = null;
                    try
                    {
                        before = JsonSerializer.SerializeToUtf8Bytes(data);
                        after = JsonSerializer.SerializeToUtf8Bytes(candidate);
                        if (!before.AsSpan().SequenceEqual(after))
                            throw new InvalidOperationException(LegacyQueueGuidance);
                    }
                    finally
                    {
                        if (before is not null) CryptographicOperations.ZeroMemory(before);
                        if (after is not null) CryptographicOperations.ZeroMemory(after);
                    }
                }
                // The legacy no-owner shutdown skips clearing a completely empty
                // queue collection. Otherwise only an exact idempotent clear is safe.
                snapshots.Add(new(store.StorePath, fingerprint));
            }
            return snapshots.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(LegacyStateGuidance);
        }
    }

    private static void RecheckLegacyTaskSnapshots(IEnumerable<LegacyTaskSnapshot> snapshots, CancellationToken token)
    {
        try
        {
            foreach (var snapshot in snapshots)
            {
                token.ThrowIfCancellationRequested();
                if (QueueOwnerClient.IsOwnerMutexHeld(snapshot.Path))
                    throw new InvalidOperationException(LegacyQueueGuidance);
                using var file = OpenBoundedLegacyTaskFile(snapshot.Path);
                var current = file is null ? null : Convert.ToHexString(SHA256.HashData(file));
                if (current != snapshot.Fingerprint)
                    throw new InvalidOperationException("Saved Notes queue state changed during replacement preflight. Existing work was preserved; retry replacement.");
                if (QueueOwnerClient.IsOwnerMutexHeld(snapshot.Path))
                    throw new InvalidOperationException(LegacyQueueGuidance);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(LegacyStateGuidance);
        }
    }

    private static string? ValidateCurrentLegacyTaskFile(FileStream? file)
    {
        if (file is null) return null;
        var content = new byte[checked((int)file.Length)];
        try
        {
            file.ReadExactly(content);
            ReadOnlyMemory<byte> json = content;
            if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
                json = json[3..];
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(LegacyStateGuidance);
            var versions = document.RootElement.EnumerateObject().Where(property =>
                property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length != 1 || versions[0].Value.ValueKind != JsonValueKind.Number ||
                !versions[0].Value.TryGetInt32(out var version) || version != 9)
                throw new InvalidOperationException(LegacyStateGuidance);
            return Convert.ToHexString(SHA256.HashData(content));
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private static FileStream? OpenBoundedLegacyTaskFile(string path)
    {
        FileStream? file = null;
        try
        {
            // Deny mutation while validating/hashing this read. Release before
            // sending replacement so an idempotent legacy save can complete.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumLegacyTaskStoreBytes) throw new IOException();
            return file;
        }
        catch (FileNotFoundException) { file?.Dispose(); return null; }
        catch (DirectoryNotFoundException) { file?.Dispose(); return null; }
        catch { file?.Dispose(); throw; }
    }

    private sealed record LegacyTaskSnapshot(string Path, string? Fingerprint);

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
