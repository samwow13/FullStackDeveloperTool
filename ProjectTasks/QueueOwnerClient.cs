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
    internal const int ProtocolVersion = 11;
    private const string OlderOwnerMessage =
        "An older queue owner is still running. When its task is inactive, exit it from the Full Stack Launcher queue tray icon, then reopen Notes & queue. Existing work was not stopped.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim CompatibilityGate = new(1, 1);

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
            attemptId, confirmedNoActiveTask, itemId, confirmedRetry);
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

    internal static async Task StartIfSavedWorkAsync()
    {
        // Reading is safe during dashboard startup. Never execute a prepared note here.
        await Task.Run(async () =>
        {
            var store = new ProjectTaskStore();
            var data = store.Load();
            if (!store.CanSave) return;
            await EnsureCompatibleOwnerAsync(store.StorePath);
            // The compatible owner may have saved a final status during this check.
            data = store.Load();
            if (!store.CanSave) return;
            var pending = data.Receipts.Any(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.QueueReviewCompletedAt is null && receipt.QueueAbandonedAt is null &&
                ((receipt.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
                    ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or
                    ProjectTaskRunState.NeedsAttention or ProjectTaskRunState.Failed or
                    ProjectTaskRunState.Interrupted) ||
                 (receipt.State == ProjectTaskRunState.Completed &&
                    (receipt.FinishedAt is null || receipt.Outcome != ProjectTaskOutcome.Succeeded ||
                     string.IsNullOrWhiteSpace(receipt.ThreadId) || string.IsNullOrWhiteSpace(receipt.TurnId)))));
            var pendingNotification = data.Receipts.Any(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.NotificationState == ProjectTaskNotificationState.Pending &&
                !string.IsNullOrWhiteSpace(receipt.CompletionMessage));
            if (!pending && !pendingNotification && !data.Queues.Any(queue => queue.Enabled)) return;
            var existing = await TrySendAsync(store.StorePath,
                new QueueOwnerRequest(ProtocolVersion, "status", null, null), CancellationToken.None);
            if (existing is { Success: true })
                return;
            if (existing != null && IsProtocolMismatch(existing))
                throw new IOException(OlderOwnerMessage);
            await StartOwnerAsync(store.StorePath, CancellationToken.None);
            for (var attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(500);
                if (await TrySendAsync(store.StorePath,
                    new QueueOwnerRequest(ProtocolVersion, "status", null, null), CancellationToken.None,
                    connectTimeoutMs: 500) is { Success: true })
                    return;
            }
            throw new IOException("The queue owner is still recovering or could not start. No new queue item was submitted by dashboard startup.");
        });
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
        var args = new List<string> { "--queue-owner" };
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
    string? ItemId = null, bool ConfirmedRetry = false);
internal sealed record QueueOwnerReply(bool Success, string Message, string? State = null,
    bool HasActiveWork = false, bool CanStopCurrent = false, string? ActiveProjectId = null,
    string? ActiveAttemptId = null);
