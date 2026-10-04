using System.IO;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Process-local evidence of recent authenticated repository read access. Stores no credentials
/// and never grants permission for a push; Git still authenticates each actual operation.
/// </summary>
public static class GitConnectionWarmup
{
    private static readonly object Sync = new();
    private static readonly Dictionary<ConnectionKey, Entry> Entries = [];
    private static readonly Dictionary<ConnectionKey, SessionEvidence> ReadyForSession = [];
    private static readonly HashSet<ConnectionKey> SessionRechecks = [];
    private static readonly HashSet<Entry> Workers = [];
    private static readonly Dictionary<GitCommandExitUnconfirmedException, string> UnconfirmedCommands = [];
    private static readonly SemaphoreSlim BackgroundChecks = new(2, 2);
    private static readonly TimeSpan ReadyLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedLifetime = TimeSpan.FromSeconds(30);

    public static bool TryGet(string root, GitHostingProvider provider, string url, out GitConnectionCheckResult result)
    {
        result = new();
        if (!TryCreateKey(root, provider, url, out var key, out _)) return false;
        lock (Sync)
        {
            if (UnconfirmedCommands.Values.Contains(key!.Root)) return false;
            if (!Entries.TryGetValue(key!, out var entry) || entry.Result is null || entry.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            result = entry.Result;
            return true;
        }
    }

    /// <summary>Open immediately using this session's successful evidence, rechecking stale evidence in the background.</summary>
    public static bool TryGetReadyForSession(string root, GitHostingProvider provider, string url, out GitConnectionCheckResult result)
    {
        result = new();
        if (!TryCreateKey(root, provider, url, out var key, out _)) return false;
        var recheck = false;
        lock (Sync)
        {
            if (UnconfirmedCommands.Values.Contains(key!.Root)) return false;
            if (!ReadyForSession.TryGetValue(key!, out var evidence)) return false;
            result = evidence.Result with
            {
                Message = "Git authentication and repository read access were verified earlier this launcher session. Git checks authentication again for repository operations."
            };
            if (evidence.VerifiedAt + ReadyLifetime <= DateTimeOffset.UtcNow) recheck = SessionRechecks.Add(key!);
        }
        // Calling directly registers the shared worker before returning to the UI. The worker's
        // actual repository and network operations run off the dispatcher.
        if (recheck) _ = RecheckSessionAsync(key!, root, provider, url);
        return true;
    }

    /// <summary>Reuse fresh evidence or share one noninteractive check with startup and other panels.</summary>
    public static Task<GitConnectionCheckResult> GetOrCheckAsync(string root, GitHostingProvider provider, string url, CancellationToken token = default) =>
        GetAsync(root, provider, url, refresh: false, interactive: false, token);

    internal static Task<GitConnectionCheckResult> RefreshAsync(string root, GitHostingProvider provider, string url, bool interactive, CancellationToken token) =>
        GetAsync(root, provider, url, refresh: true, interactive, token);

    /// <summary>Invalidate a changed repository configuration without touching credentials.</summary>
    public static void Invalidate(string root)
    {
        string normalized;
        try { normalized = GitConnectionService.NormalizeRoot(root).ToUpperInvariant(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return; }
        lock (Sync)
        {
            foreach (var key in Entries.Keys.Where(key => key.Root == normalized).ToArray()) RemoveEntry(key);
            foreach (var key in ReadyForSession.Keys.Where(key => key.Root == normalized).ToArray()) ReadyForSession.Remove(key);
        }
    }

    private static async Task RecheckSessionAsync(ConnectionKey key, string root, GitHostingProvider provider, string url)
    {
        try { await GetOrCheckAsync(root, provider, url).ConfigureAwait(false); }
        catch (Exception) { /* Closing or an explicit check can replace this background check. */ }
        finally { lock (Sync) SessionRechecks.Remove(key); }
    }

    /// <summary>Final dashboard shutdown only: stop owned checks and wait for bounded child cleanup.</summary>
    public static async Task CancelAndDrainPendingAsync()
    {
        Task[] workers;
        lock (Sync)
        {
            var pending = Workers.ToArray();
            foreach (var worker in pending) worker.Cancellation.Cancel();
            foreach (var key in Entries.Where(pair => !pair.Value.Completion.Task.IsCompleted).Select(pair => pair.Key).ToArray()) Entries.Remove(key);
            workers = pending.Select(worker => worker.WorkerCompletion.Task).ToArray();
        }
        if (workers.Length == 0) return;
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    /// <summary>Warm only the caller's captured configured folders; no scanning of descendants.</summary>
    public static Task WarmConfiguredFoldersAsync(IReadOnlyList<string> folders, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await Parallel.ForEachAsync(folders.Distinct(StringComparer.OrdinalIgnoreCase),
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = deadline.Token }, async (folder, lifetime) =>
                    {
                        try
                        {
                            IReadOnlyList<GitConnectionService.ConfiguredConnection> connections;
                            using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                            {
                                discovery.CancelAfter(TimeSpan.FromSeconds(15));
                                connections = await GitConnectionService.ReadConfiguredConnectionsAsync(folder, discovery.Token).ConfigureAwait(false);
                            }
                            foreach (var connection in connections)
                            {
                                lifetime.ThrowIfCancellationRequested();
                                await GetOrCheckAsync(connection.Root, connection.Target.Provider, connection.Target.CloneUrl, lifetime).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            // Missing, untrusted and unsupported repositories wait for explicit setup.
                            // Do not retain exception data from background credential operations.
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }, token);

    private static async Task<GitConnectionCheckResult> GetAsync(string root, GitHostingProvider provider, string url, bool refresh, bool interactive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = GitConnectionService.ParseTarget(provider, url);
        var normalizedRoot = GitConnectionService.NormalizeRoot(root);
        var key = new ConnectionKey(normalizedRoot.ToUpperInvariant(), provider, target.CloneUrl);
        while (true)
        {
            GitCommandExitUnconfirmedException? pending;
            lock (Sync) pending = UnconfirmedCommands.FirstOrDefault(pair => pair.Value == key.Root).Key;
            if (pending == null) break;
            if (!await Task.Run(pending.IsExitConfirmed, token).ConfigureAwait(false)) throw pending;
            lock (Sync) UnconfirmedCommands.Remove(pending);
        }
        Entry entry;
        var start = false;
        lock (Sync)
        {
            if (UnconfirmedCommands.FirstOrDefault(pair => pair.Value == key.Root).Key is { } heldCommand) throw heldCommand;
            // Sign-in can change the helper account shared by multiple repositories/domains.
            if (interactive)
            {
                foreach (var existing in Entries.Keys.ToArray()) RemoveEntry(existing);
                ReadyForSession.Clear();
            }
            else if (refresh)
            {
                RemoveEntry(key);
                ReadyForSession.Remove(key);
            }

            if (Entries.TryGetValue(key, out var saved) && (saved.Result is null && !saved.Completion.Task.IsCompleted
                || saved.Result is not null && saved.ExpiresAt > DateTimeOffset.UtcNow)) entry = saved;
            else
            {
                RemoveEntry(key);
                foreach (var expired in Entries.Where(pair => pair.Value.Completion.Task.IsCompleted
                    && pair.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) RemoveEntry(expired);
                // Bound retained evidence even when many independent repositories are configured.
                while (Entries.Count >= 512)
                {
                    var oldest = Entries.Where(pair => pair.Value.Completion.Task.IsCompleted)
                        .OrderBy(pair => pair.Value.ExpiresAt).FirstOrDefault();
                    if (oldest.Key is null) break;
                    RemoveEntry(oldest.Key);
                }
                entry = new Entry();
                Entries[key] = entry;
                Workers.Add(entry);
                start = true;
            }
            entry.Waiters++;
        }
        if (start) _ = RunEntryAsync(key, entry, normalizedRoot, target, refresh, interactive);
        try { return await entry.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            Task? stopped = null;
            lock (Sync)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && !entry.Completion.Task.IsCompleted)
                {
                    // One canceled panel cannot cancel a check still awaited by startup or another panel.
                    if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Entries.Remove(key);
                    entry.Cancellation.Cancel();
                    stopped = entry.Completion.Task;
                }
            }
            if (stopped is not null)
            {
                // Keep the last owner's operation alive until its bounded Git child cleanup completes.
                try { await stopped.ConfigureAwait(false); }
                catch (GitCommandExitUnconfirmedException) { throw; }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task RunEntryAsync(ConnectionKey key, Entry entry, string root, GitConnectionTarget target, bool explicitCheck, bool interactive)
    {
        var acquired = false;
        GitConnectionCheckResult? result = null;
        GitCommandExitUnconfirmedException? unconfirmed = null;
        try
        {
            // Explicit user actions have their own bounded command lifetime; startup cannot queue
            // an account dialog behind checks for unrelated projects.
            if (!explicitCheck)
            {
                await BackgroundChecks.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
                acquired = true;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
            deadline.CancelAfter(interactive ? TimeSpan.FromMinutes(3) : explicitCheck ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));
            result = await GitConnectionService.CheckUncachedAsync(root, target, interactive, automatic: !explicitCheck, deadline.Token).ConfigureAwait(false);
        }
        catch (GitCommandExitUnconfirmedException exception) { unconfirmed = exception; }
        catch (OperationCanceledException) when (!entry.Cancellation.IsCancellationRequested)
        {
            result = new() { Message = "The saved Git connection check timed out. Check the network and use Check connection to try again." };
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            result = new() { Message = "The saved Git connection could not be checked. Refresh local status, then use Config to review the connection." };
        }
        finally
        {
            if (acquired) BackgroundChecks.Release();
            lock (Sync)
            {
                var current = Entries.TryGetValue(key, out var saved) && ReferenceEquals(saved, entry);
                if (interactive && current)
                {
                    foreach (var other in Entries.Keys.Where(other => other != key).ToArray()) RemoveEntry(other);
                    foreach (var other in ReadyForSession.Keys.Where(other => other != key).ToArray()) ReadyForSession.Remove(other);
                }
                if (unconfirmed != null)
                {
                    UnconfirmedCommands[unconfirmed] = key.Root;
                    if (current) Entries.Remove(key);
                    ReadyForSession.Remove(key);
                    entry.Completion.TrySetException(unconfirmed);
                }
                else if (result is not null && current && !entry.Cancellation.IsCancellationRequested)
                {
                    entry.Result = result;
                    entry.ExpiresAt = DateTimeOffset.UtcNow + (result.Ready ? ReadyLifetime : FailedLifetime);
                    if (result.Ready)
                    {
                        ReadyForSession[key] = new(result, DateTimeOffset.UtcNow);
                        if (ReadyForSession.Count > 512)
                            ReadyForSession.Remove(ReadyForSession.OrderBy(pair => pair.Value.VerifiedAt).First().Key);
                    }
                    else ReadyForSession.Remove(key);
                    entry.Completion.TrySetResult(result);
                }
                else
                {
                    if (current) Entries.Remove(key);
                    entry.Completion.TrySetCanceled();
                }
                entry.Cancellation.Dispose();
                Workers.Remove(entry);
                entry.WorkerCompletion.TrySetResult();
            }
        }
    }

    // Caller holds Sync. Removing an in-flight entry makes its eventual result ineligible for caching.
    private static void RemoveEntry(ConnectionKey key)
    {
        if (Entries.Remove(key, out var entry) && !entry.Completion.Task.IsCompleted) entry.Cancellation.Cancel();
    }

    private static bool TryCreateKey(string root, GitHostingProvider provider, string url, out ConnectionKey? key, out GitConnectionTarget? target)
    {
        key = null;
        target = null;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            target = GitConnectionService.ParseTarget(provider, url);
            key = new(GitConnectionService.NormalizeRoot(root).ToUpperInvariant(), provider, target.CloneUrl);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or NotSupportedException) { return false; }
    }

    private sealed record ConnectionKey(string Root, GitHostingProvider Provider, string Url);
    private sealed record SessionEvidence(GitConnectionCheckResult Result, DateTimeOffset VerifiedAt);

    private sealed class Entry
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<GitConnectionCheckResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WorkerCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GitConnectionCheckResult? Result { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int Waiters { get; set; }
    }
}
