using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CodexMonitor;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Versioned, current-user commands for the independent queue owner.</summary>
internal static class QueueOwnerClient
{
    // Owner and dashboard must agree on the task-store schema before any queue
    // command. Version 2 added receipt images; version 3 added guarded dismissal
    // for attempts with no recorded Codex submission. Version 4 requires exact
    // retry identities and retained task activity presentation fields. Version 5
    // records explicit deletion/abandonment separately from verified review.
    // Version 6 protects saved browser source and its per-image prompt choice.
    internal const int ProtocolVersion = 6;
    private const string OlderOwnerMessage =
        "An older queue owner is still running. Automatic replacement requires a verified idle status and exact process identity. Exit it from the Full Stack Launcher queue tray icon, then reopen Notes & queue.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim ReplacementGate = new(1, 1);

    internal static string IdentityFor(string storePath) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storePath).ToUpperInvariant()))).ToLowerInvariant();

    internal static string PipeNameFor(string storePath) => "FullStackLauncher.QueueOwner." + IdentityFor(storePath);
    internal static string MutexNameFor(string storePath) => @"Local\FullStackLauncher.QueueOwner." + IdentityFor(storePath);

    /// <summary>
    /// Retire an older idle owner before this build writes the newer task-store
    /// schema. An ambiguous identity or idle check leaves the owner untouched.
    /// A failed migration keeps the original store file.
    /// </summary>
    internal static async Task EnsureCompatibleOwnerAsync(string storePath,
        CancellationToken cancellationToken = default)
    {
        await ReplacementGate.WaitAsync(cancellationToken);
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

                using var executionLock = TryAcquireExecutionLock()
                    ?? throw new IOException(OlderOwnerMessage);
                if (!IsOwnerMutexHeld(storePath)) throw new IOException(OlderOwnerMessage);

                LegacyStatus? idle = null;
                foreach (var version in new[] { 5, 4, 3, 2 })
                {
                    var status = await TryLegacyStatusAsync(storePath, version, cancellationToken);
                    if (status is null) continue;
                    if (status.Reply.Success)
                    {
                        idle = status;
                        break;
                    }
                    if (!IsProtocolMismatch(status.Reply)) throw new IOException(OlderOwnerMessage);
                }
                if (idle is null || !idle.HasActiveWorkField || idle.Reply.HasActiveWork ||
                    idle.Reply.CanStopCurrent || !string.IsNullOrWhiteSpace(idle.Reply.ActiveAttemptId))
                    throw new IOException(OlderOwnerMessage);

                StopVerifiedIdleOwner(storePath, idle);
                // Store.Save performs an optimistic conflict check and writes the
                // migrated v6 snapshot only after the old process has exited.
                MigrateStoreAfterReplacement(storePath);
            }, cancellationToken);
        }
        finally { ReplacementGate.Release(); }
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
            // The older owner may have saved a final status before handoff.
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

    private static FileStream? TryAcquireExecutionLock()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher");
        Directory.CreateDirectory(folder);
        try
        {
            return new FileStream(Path.Combine(folder, "project-queue-execution.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }

    private static void MigrateStoreAfterReplacement(string storePath)
    {
        const string suffix = ".project-tasks.json";
        var store = Path.GetFullPath(storePath).Equals(
            Path.GetFullPath(ProjectTaskStore.DefaultStorePath), StringComparison.OrdinalIgnoreCase)
            ? new ProjectTaskStore()
            : storePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? new ProjectTaskStore(storePath[..^suffix.Length])
                : throw new IOException("The task-store identity is invalid.");
        if (!Path.GetFullPath(store.StorePath).Equals(Path.GetFullPath(storePath),
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("The task-store identity changed during queue-owner replacement.");
        var data = store.Load();
        if (!store.CanSave) throw new IOException(store.LoadWarning ?? "The task store is unavailable.");
        store.Save(data);
    }

    private static async Task<LegacyStatus?> TryLegacyStatusAsync(string storePath, int version,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeNameFor(storePath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.ConnectAsync(connect.Token);
            }
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) ||
                processId <= 4 || processId > int.MaxValue)
                throw new IOException("The queue owner process could not be identified.");
            using var process = OpenProcess(QueryLimitedInformation, false, processId);
            if (process.IsInvalid || !GetProcessTimes(process, out var created, out var exited, out _, out _) ||
                exited != 0 || created <= 0)
                throw new IOException("The queue owner process identity is unavailable.");

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
                { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new QueueOwnerRequest(version, "status", null, null), JsonOptions));
            var line = await ReadBoundedLineAsync(reader, 8192, timeout.Token);
            using var document = JsonDocument.Parse(line);
            var hasActiveWorkField = document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("hasActiveWork", out var active) &&
                active.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var reply = document.RootElement.Deserialize<QueueOwnerReply>(JsonOptions)
                ?? throw new IOException("The queue owner returned an empty response.");
            return new(reply, hasActiveWorkField, processId, created);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private static void StopVerifiedIdleOwner(string storePath, LegacyStatus status)
    {
        using var process = OpenProcess(QueryLimitedInformation | Terminate | Synchronize,
            false, status.ProcessId);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out var exited, out _, out _) ||
            exited != 0 || created != status.CreatedFileTime || !IsOwnerMutexHeld(storePath))
            throw new IOException(OlderOwnerMessage);
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length) || length <= 0 ||
            !IsExactStagedOwner(storePath, path.ToString(), ReadCommandLine(process)))
            throw new IOException(OlderOwnerMessage);
        if (!GetProcessTimes(process, out created, out exited, out _, out _) ||
            exited != 0 || created != status.CreatedFileTime || !IsOwnerMutexHeld(storePath))
            throw new IOException(OlderOwnerMessage);
        if (!TerminateProcess(process, 0) || WaitForSingleObject(process, 5000) != WaitObject0)
            throw new IOException(OlderOwnerMessage);
    }

    private static bool IsExactStagedOwner(string storePath, string imagePath, string commandLine)
    {
        var runtimeRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher", "monitor-runtime"));
        var args = ParseCommandLine(commandLine);
        if (args.Length < 2) return false;
        var dotnetHosted = Path.GetFileNameWithoutExtension(imagePath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var launcherPath = dotnetHosted ? args[1] : imagePath;
        var expectedName = typeof(App).Assembly.GetName().Name + (dotnetHosted ? ".dll" : ".exe");
        if (!Path.IsPathFullyQualified(launcherPath) ||
            !Path.GetFileName(launcherPath).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            return false;
        var launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherPath));
        if (launcherDirectory is null) return false;
        var hash = Path.GetFileName(launcherDirectory);
        if (!Path.GetFullPath(Path.GetDirectoryName(launcherDirectory) ?? "")
                .Equals(runtimeRoot, StringComparison.OrdinalIgnoreCase) ||
            hash.Length != 64 ||
            !hash.All(Uri.IsHexDigit))
            return false;
        var modeIndex = dotnetHosted ? 2 : 1;
        if (args.Length <= modeIndex || !args[modeIndex].Equals("--queue-owner", StringComparison.Ordinal))
            return false;
        if (Path.GetFullPath(storePath).Equals(Path.GetFullPath(ProjectTaskStore.DefaultStorePath),
            StringComparison.OrdinalIgnoreCase)) return args.Length == modeIndex + 1;
        const string suffix = ".project-tasks.json";
        return storePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            args.Length == modeIndex + 3 && args[modeIndex + 1] == "--settings" &&
            Path.IsPathFullyQualified(args[modeIndex + 2]) &&
            Path.GetFullPath(args[modeIndex + 2]).Equals(
                Path.GetFullPath(storePath[..^suffix.Length]), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadCommandLine(SafeProcessHandle process)
    {
        NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out var size);
        if (size <= 0 || size > 1024 * 1024) return "";
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(process, 60, buffer, size, out _) != 0) return "";
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Length == 0 || offset < 0 || offset + value.Length > size) return "";
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero || count is < 1 or > 16) return [];
        try
        {
            var args = new string[count];
            for (var index = 0; index < count; index++)
                args[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(pointer); }
    }

    private sealed record LegacyStatus(QueueOwnerReply Reply, bool HasActiveWorkField,
        uint ProcessId, long CreatedFileTime);

    private const uint QueryLimitedInformation = 0x1000;
    private const uint Terminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder name, ref int size);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        IntPtr information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

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
