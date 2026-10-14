using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Versioned, current-user commands for the independent queue owner.</summary>
internal static class QueueOwnerClient
{
    // Owner and dashboard must agree on queue behavior and task-store schema before any queue
    // command. Version 2 added receipt images; version 3 added guarded dismissal
    // for attempts with no recorded Codex submission. Version 4 requires exact
    // retry identities and retained task activity presentation fields. Version 5
    // records explicit deletion/abandonment separately from verified review.
    // Version 6 protects saved browser source and its per-image prompt choice.
    // Version 7 retains agent follow-up provenance and retry receipts.
    // Version 8 enforces the saved delay between confirmed queue completions.
    // Version 9 requires a live dashboard's Long Running Task permit for dispatch.
    // Version 10 adds Automatic Loop Mode and version 9 task-store snapshots.
    // Version 11 requires configured access, noninteractive approvals, and a
    // command-access preflight before queue task creation.
    // Version 12 verifies project-aware configuration capability and supports
    // legacy response fields and read-only exact-turn history reconciliation.
    // Version 13 binds queue lifetime to the dashboard and confirms stop/clear
    // before closing, without discarding notes or execution receipts.
    // Version 14 transfers dashboard ownership without interrupting the active
    // turn, and blocks new dispatch until the replacement confirms startup.
    internal const int ProtocolVersion = 14;
    private const string OlderOwnerMessage =
        "An older queue owner is still running. When its task is inactive, exit it from the Full Stack Launcher queue tray icon, then reopen Notes & queue. Existing work was not stopped.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim CompatibilityGate = new(1, 1);
    private static readonly long DashboardStartedAtUtcTicks = GetCurrentProcessStartedAtUtcTicks();

    internal static QueueOwnerDashboardHandoff CreateDashboardHandoff(int? replacementProcessId, long? replacementStartedAtUtcTicks)
    {
        if (replacementProcessId is null or <= 4 || replacementStartedAtUtcTicks is null or <= 0 ||
            replacementProcessId == Environment.ProcessId)
            throw new IOException("Launcher replacement identity is missing. Existing dashboard and queue work were preserved.");
        return new(replacementProcessId.Value, replacementStartedAtUtcTicks.Value);
    }

    internal static async Task CompleteDashboardHandoffAsync(CancellationToken cancellationToken = default)
    {
        var store = new ProjectTaskStore();
        var reply = await TrySendAsync(store.StorePath, new QueueOwnerRequest(ProtocolVersion, "handoff-complete", null, null,
            DashboardProcessId: Environment.ProcessId, DashboardProcessStartedAtUtcTicks: DashboardStartedAtUtcTicks), cancellationToken);
        if (reply == null && !IsOwnerMutexHeld(store.StorePath)) return;
        RequireHandoffReply(reply, "handoff-complete", "handoff-not-pending");
    }

    private static void RequireHandoffReply(QueueOwnerReply? reply, params string[] acceptedStates)
    {
        if (reply != null && IsProtocolMismatch(reply)) throw new IOException(OlderOwnerMessage);
        if (reply?.Success != true || reply.State is not { } state || !acceptedStates.Contains(state, StringComparer.Ordinal))
            throw new IOException(reply?.Message ??
                "The queue owner did not confirm dashboard transfer. Existing work and queue records were preserved; keep Launcher open and retry replacement.");
    }

    internal sealed class QueueOwnerDashboardHandoff(int replacementProcessId, long replacementStartedAtUtcTicks)
    {
        private readonly string _storePath = new ProjectTaskStore().StorePath;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private bool _mayHavePrepared;
        private bool _ownerMissing;
        internal bool SourceOwnershipConfirmed { get; private set; }

        internal async Task PrepareAsync(CancellationToken cancellationToken)
        {
            using var replacement = FullStackLauncher.Services.DashboardReplacementPeer.Pin(replacementProcessId, replacementStartedAtUtcTicks);
            // A lost startup acknowledgement may have left this exact dashboard
            // as the pending target. Complete that idempotent ownership change
            // before beginning another one; never retry dispatch or queue work.
            await CompleteDashboardHandoffAsync(cancellationToken);
            SourceOwnershipConfirmed = true;
            _mayHavePrepared = true;
            var reply = await TrySendAsync(_storePath, Request("handoff-prepare"), cancellationToken);
            if (reply == null && !IsOwnerMutexHeld(_storePath))
            {
                _ownerMissing = true;
                _mayHavePrepared = false;
                return;
            }
            if (reply != null && IsProtocolMismatch(reply)) _mayHavePrepared = false;
            RequireHandoffReply(reply, "handoff-prepared", "handoff-transferred");
        }

        internal async Task TransferAsync(CancellationToken cancellationToken)
        {
            using var replacement = FullStackLauncher.Services.DashboardReplacementPeer.Pin(replacementProcessId, replacementStartedAtUtcTicks);
            if (_ownerMissing)
            {
                if (IsOwnerMutexHeld(_storePath))
                {
                    SourceOwnershipConfirmed = false;
                    throw new IOException("A queue owner appeared during Launcher replacement. Existing work was preserved; retry replacement to verify its identity.");
                }
                return;
            }
            RequireHandoffReply(await TrySendAsync(_storePath, Request("handoff-transfer"), cancellationToken), "handoff-transferred");
        }

        internal async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (!_mayHavePrepared || _ownerMissing) return;
            var reply = await TrySendAsync(_storePath, Request("handoff-rollback"), cancellationToken, connectTimeoutMs: 2000);
            if (reply == null && !IsOwnerMutexHeld(_storePath)) { _mayHavePrepared = false; return; }
            RequireHandoffReply(reply, "handoff-rolled-back");
            _mayHavePrepared = false;
        }

        private QueueOwnerRequest Request(string command) => new(ProtocolVersion, command, null, null,
            DashboardProcessId: Environment.ProcessId, DashboardProcessStartedAtUtcTicks: DashboardStartedAtUtcTicks,
            HandoffId: _id, ReplacementDashboardProcessId: replacementProcessId,
            ReplacementDashboardProcessStartedAtUtcTicks: replacementStartedAtUtcTicks);
    }

    internal static string IdentityFor(string storePath) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storePath).ToUpperInvariant()))).ToLowerInvariant();

    internal static string PipeNameFor(string storePath) => "FullStackLauncher.QueueOwner." + IdentityFor(storePath);
    internal static string MutexNameFor(string storePath) => @"Local\FullStackLauncher.QueueOwner." + IdentityFor(storePath);

    /// <summary>
    /// Require compatible queue behavior before writing a newer task-store schema.
    /// Preserve incompatible owners and their work until the user exits them from
    /// the queue tray icon; never force-terminate an owner during an upgrade.
    /// </summary>
    internal static async Task EnsureCompatibleOwnerAsync(string storePath,
        CancellationToken cancellationToken = default)
    {
        await CompatibilityGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(async () =>
            {
                var current = await TrySendAsync(storePath,
                    new QueueOwnerRequest(ProtocolVersion, "status", null, null), cancellationToken);
                if (current is { Success: true }) return;
                if (current is null)
                {
                    if (IsOwnerMutexHeld(storePath))
                        throw new IOException("The queue owner is running but its status is unavailable. Wait for recovery or exit it from the Full Stack Launcher queue tray icon before saving task data.");
                    return;
                }
                if (!IsProtocolMismatch(current)) return; // A current owner reported its own error.
                throw new IOException(OlderOwnerMessage);
            }, cancellationToken);
        }
        finally { CompatibilityGate.Release(); }
    }

    internal static async Task<QueueOwnerReply> SendAsync(
        string storePath, string command, string? projectId = null, bool? paused = null,
        bool startIfMissing = false, CancellationToken cancellationToken = default,
        string? attemptId = null, bool confirmedNoActiveTask = false,
        string? itemId = null, bool confirmedRetry = false)
    {
        var request = new QueueOwnerRequest(ProtocolVersion, command, projectId, paused,
            attemptId, confirmedNoActiveTask, itemId, confirmedRetry,
            Environment.ProcessId, DashboardStartedAtUtcTicks);
        var reply = await TrySendAsync(storePath, request, cancellationToken);
        if (reply != null && IsProtocolMismatch(reply))
        {
            await EnsureCompatibleOwnerAsync(storePath, cancellationToken);
            reply = await TrySendAsync(storePath, request, cancellationToken);
            if (reply is null) startIfMissing = true;
        }
        if (reply != null) return NormalizeReply(reply);
        if (!startIfMissing)
            return new(false, "The queue owner is not running. Open Notes & queue and use Enable Queue to start it.");

        await StartOwnerAsync(storePath, cancellationToken);
        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            reply = await TrySendAsync(storePath, request, cancellationToken, connectTimeoutMs: 500);
            if (reply != null) return NormalizeReply(reply);
        }
        return new(false, "The queue owner is still recovering or could not start. No queue command was applied. Check the tray and retry.");
    }

    /// <summary>
    /// Delete through an already running owner. A null result authorizes the
    /// caller to try a mutex-guarded local save; it never starts an owner.
    /// </summary>
    internal static async Task<QueueOwnerReply?> TryDeleteAttemptAsync(string storePath,
        string projectId, string attemptId, CancellationToken cancellationToken = default)
    {
        var reply = await TrySendAsync(storePath,
            new QueueOwnerRequest(ProtocolVersion, "delete-attempt", projectId, null, attemptId),
            cancellationToken);
        if (reply is not null) return NormalizeReply(reply);
        if (IsOwnerMutexHeld(storePath))
            return new(false, "The queue owner did not confirm Delete. Reload attempts before retrying; the command may have completed.");
        return null;
    }

    /// <summary>Stop the owned queue turn, clear safe items, and await owner exit.</summary>
    internal static async Task ShutdownForDashboardAsync(CancellationToken cancellationToken = default)
    {
        await CompatibilityGate.WaitAsync(cancellationToken);
        try
        {
            var store = new ProjectTaskStore();
            var reply = await TrySendAsync(store.StorePath,
                new QueueOwnerRequest(ProtocolVersion, "shutdown", null, null,
                    DashboardProcessId: Environment.ProcessId,
                    DashboardProcessStartedAtUtcTicks: DashboardStartedAtUtcTicks), cancellationToken);
            if (reply is not null)
            {
                if (IsProtocolMismatch(reply)) throw new IOException(OlderOwnerMessage);
                if (!reply.Success || reply.State != "shutdown-complete")
                    throw new IOException(reply.Message);
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    while (await Task.Run(() => IsOwnerMutexHeld(store.StorePath), exitTimeout.Token))
                        await Task.Delay(100, exitTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Queues were cleared, but the queue owner has not confirmed exit. Keep the launcher open and retry closing.");
                }
                return;
            }
            // A missing/unresponsive reply is never permission to overwrite a live
            // owner. Keep the same thread's mutex claim throughout load/save.
            await Task.Run(() =>
            {
                using var claim = TryClaimOwnerMutex(store.StorePath)
                    ?? throw new IOException("The queue owner did not confirm shutdown. Keep the launcher open and retry closing after its task settles.");
                var data = store.Load();
                if (!store.CanSave) throw new IOException(store.LoadWarning);
                cancellationToken.ThrowIfCancellationRequested();
                if (data.Queues.Count == 0 && data.QueueItems.Count == 0) return;
                ProjectQueueMaintenance.ClearForShutdown(data);
                store.Save(data);
            }, cancellationToken);
        }
        finally { CompatibilityGate.Release(); }
    }

    internal static bool IsOwnerMutexHeld(string storePath)
    {
        using var claim = TryClaimOwnerMutex(storePath);
        return claim is null;
    }

    /// <summary>Keep the per-store owner slot claimed throughout a schema migration save.</summary>
    internal static IDisposable? TryClaimOwnerMutex(string storePath)
    {
        var instance = new Mutex(false, MutexNameFor(storePath));
        bool acquired;
        try
        {
            acquired = instance.WaitOne(0);
        }
        catch (AbandonedMutexException) { acquired = true; }
        catch
        {
            instance.Dispose();
            throw;
        }
        if (acquired) return new MutexClaim(instance);
        instance.Dispose();
        return null;
    }

    private sealed class MutexClaim(Mutex instance) : IDisposable
    {
        public void Dispose()
        {
            instance.ReleaseMutex();
            instance.Dispose();
        }
    }

    private static async Task StartOwnerAsync(string storePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string>
        {
            "--queue-owner", "--queue-dashboard-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--queue-dashboard-start-ticks", DashboardStartedAtUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!Path.GetFullPath(storePath).Equals(Path.GetFullPath(ProjectTaskStore.DefaultStorePath), StringComparison.OrdinalIgnoreCase))
        {
            const string suffix = ".project-tasks.json";
            if (!storePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The task-store identity is invalid.");
            args.Add("--settings");
            args.Add(storePath[..^suffix.Length]);
        }
        try
        {
            // MonitorRuntime stages an immutable, hashed launcher payload. The queue owner
            // uses the same copy and a distinct command-line mode, so builds remain unlocked.
            var start = await Task.Run(() => MonitorRuntime.CreateStart(args.ToArray()), cancellationToken);
            using var process = Process.Start(start) ?? throw new IOException("The queue owner could not start.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new IOException("The queue owner could not start from its independent runtime copy.", ex);
        }
    }

    private static QueueOwnerReply NormalizeReply(QueueOwnerReply reply) =>
        IsProtocolMismatch(reply) ? reply with { Message = OlderOwnerMessage } : reply;

    private static bool IsProtocolMismatch(QueueOwnerReply reply) => !reply.Success &&
        reply.Message.StartsWith("The queue control protocol changed.", StringComparison.Ordinal);

    private static long GetCurrentProcessStartedAtUtcTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static async Task<QueueOwnerReply?> TrySendAsync(
        string storePath, QueueOwnerRequest request, CancellationToken cancellationToken,
        int connectTimeoutMs = 2000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeNameFor(storePath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connect.CancelAfter(TimeSpan.FromMilliseconds(connectTimeoutMs));
                await pipe.ConnectAsync(connect.Token);
            }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
                { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await ReadBoundedLineAsync(reader, 8192, timeout.Token);
            return JsonSerializer.Deserialize<QueueOwnerReply>(line, JsonOptions)
                ?? throw new IOException("The queue owner returned an empty response.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int maximum,
        CancellationToken cancellationToken)
    {
        var one = new char[1];
        var text = new StringBuilder();
        while (text.Length < maximum)
        {
            if (await reader.ReadAsync(one, cancellationToken) == 0)
                throw new IOException("The queue owner returned an incomplete response.");
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(one[0]);
        }
        throw new IOException("The queue owner response is too large.");
    }
}

internal sealed record QueueOwnerRequest(int Version, string Command, string? ProjectId, bool? Paused,
    string? AttemptId = null, bool ConfirmedNoActiveTask = false,
    string? ItemId = null, bool ConfirmedRetry = false,
    int? DashboardProcessId = null, long? DashboardProcessStartedAtUtcTicks = null,
    string? HandoffId = null, int? ReplacementDashboardProcessId = null,
    long? ReplacementDashboardProcessStartedAtUtcTicks = null);
internal sealed record QueueOwnerReply(bool Success, string Message, string? State = null,
    bool HasActiveWork = false, bool CanStopCurrent = false, string? ActiveProjectId = null,
    string? ActiveAttemptId = null);
