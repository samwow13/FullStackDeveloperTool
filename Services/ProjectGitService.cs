using System.IO;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// One process-wide owner of project discovery, checkout snapshots and derived Git views.
/// Opening a panel consumes existing state. Only scheduled/explicit refreshes and completed
/// operations replace it; GitRepositoryService still validates every actual command.
/// </summary>
public sealed class ProjectGitService
{
    public static ProjectGitService Shared { get; } = new();
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectEntry> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryEntry> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _backgroundSlots = new(2, 2);
    private readonly SemaphoreSlim _readSlots = new(4, 4);
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _backgroundLifetime;
    private Task? _background;

    public event EventHandler<ProjectGitChangedEventArgs>? Changed;

    public bool TryGetProject(string projectId, out ProjectGitState state)
    {
        lock (_gate)
        {
            if (_projects.TryGetValue(projectId, out var entry) && entry.State is { } saved)
            {
                state = saved with { IsRefreshing = entry.Worker is { IsCompleted: false } };
                return true;
            }
        }
        state = new() { ProjectId = projectId };
        return false;
    }

    public bool TryGetRepository(string folder, out GitRepositoryState state)
    {
        var root = Normalize(folder);
        lock (_gate)
        {
            if (_repositories.TryGetValue(root, out var entry) && entry.State is { } saved)
            {
                state = saved with { IsRefreshing = entry.RefreshInProgress };
                return true;
            }
        }
        state = new() { RepositoryRoot = root };
        return false;
    }

    /// <summary>Capture the current library, removing obsolete projects from future scheduling.</summary>
    public void SynchronizeProjects(IReadOnlyList<ProjectGitRegistration> registrations)
    {
        var retained = registrations.Select(item => item.ProjectId).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var removed in _projects.Keys.Where(id => !retained.Contains(id)).ToArray())
                _projects.Remove(removed);
            foreach (var registration in registrations)
                RegisterProject(registration.ProjectId, CaptureFolders(registration.Folders));
        }
    }

    public void RemoveProject(string projectId)
    {
        lock (_gate) _projects.Remove(projectId);
    }

    public Task<ProjectGitState> GetProjectAsync(string projectId,
        IReadOnlyList<GitRepositoryDiscoveryFolder> folders, CancellationToken token = default) =>
        ReadProjectAsync(projectId, folders, refresh: false, fetchRemote: false, token);

    public Task<ProjectGitState> RefreshProjectAsync(string projectId,
        IReadOnlyList<GitRepositoryDiscoveryFolder> folders, bool fetchRemote = true,
        CancellationToken token = default) =>
        ReadProjectAsync(projectId, folders, refresh: true, fetchRemote, token);

    public Task<GitRepositoryState> GetRepositoryAsync(string folder, string? remoteName = null,
        CancellationToken token = default) => ReadRepositoryAsync(folder, remoteName, false, false, token);

    public Task<GitRepositoryState> RefreshRepositoryAsync(string folder, string? remoteName = null,
        bool fetchRemote = false, CancellationToken token = default) =>
        ReadRepositoryAsync(folder, remoteName, true, fetchRemote, token);

    /// <summary>Retain visible evidence but make the next consumer obtain fresh command state.</summary>
    public void InvalidateRepository(string folder)
    {
        var root = Normalize(folder);
        lock (_gate)
        {
            if (!_repositories.TryGetValue(root, out var entry)) return;
            entry.Generation++;
            entry.Dirty = true;
            entry.Details.Clear();
        }
    }

    /// <summary>Discard stale refresh results only for repositories using the changed Azure token scope.</summary>
    internal void InvalidateAzureTokenScope(string scope)
    {
        static bool Matches(GitRemoteInfo remote, string expected)
        {
            if ((remote.Provider ?? GitConnectionService.InferProvider(remote.FetchUrl)) != GitHostingProvider.AzureDevOps)
                return false;
            try
            {
                var target = GitConnectionService.ParseTarget(GitHostingProvider.AzureDevOps, remote.FetchUrl);
                return !target.IsSsh && AzureDevOpsImportService.ParseProjectUrl(target.CloneUrl).TokenScope == expected;
            }
            catch (InvalidOperationException) { return false; }
        }
        List<(string Root, GitRepositoryState State)> affected = [];
        lock (_gate)
        {
            foreach (var root in _repositories.Where(pair => pair.Value.State?.Snapshot?.Remotes.Any(remote => Matches(remote, scope)) == true)
                .Select(pair => pair.Key).ToArray())
            {
                var entry = _repositories[root];
                entry.Generation++;
                entry.Dirty = true;
                entry.Details.Clear();
                if (entry.State is { } state)
                {
                    var updated = state with { ActiveConnection = new()
                    {
                        Label = "Reading connection" + (state.ActiveRemote is { } active ? " · " + active.Name : ""),
                        HeaderLabel = "Git - Reading connection…",
                        Detail = "The saved Azure DevOps token scope changed. The active connection route needs a fresh local read."
                    } };
                    entry.State = updated;
                    affected.Add((root, updated));
                }
            }
        }
        foreach (var (root, state) in affected) PublishRepository(root, state);
    }

    /// <summary>Record only a confirmed explicit fetch; this never performs a Git operation.</summary>
    public void MarkFetched(string folder, string remoteName)
    {
        var root = Normalize(folder);
        GitRepositoryState? published = null;
        lock (_gate)
        {
            if (!_repositories.TryGetValue(root, out var entry) || entry.State is not { } state
                || state.Snapshot?.Remotes.Any(remote => remote.Name == remoteName && remote.UrlCanCopy) != true) return;
            entry.State = published = state with { LastFetchAt = DateTimeOffset.UtcNow, FetchError = null };
            foreach (var key in entry.Details.Keys.ToArray())
                entry.Details[key] = entry.Details[key] with { LastFetchAt = published.LastFetchAt, FetchError = null };
        }
        PublishRepository(root, published);
    }

    /// <summary>Read selection and summaries locally. Only a changed selected remote rereads its Git authentication route.</summary>
    public async Task<GitRepositoryState> RefreshLedgerAsync(string folder, CancellationToken token = default)
    {
        var root = Normalize(folder);
        var state = await GetRepositoryAsync(root, token: token).ConfigureAwait(false);
        if (state.Snapshot is not { IsRepository: true } snapshot || state.Error is not null || state.IsRefreshing) return state;
        RepositoryEntry entry;
        Task<GitRepositoryState> worker;
        lock (_gate)
        {
            entry = _repositories[root];
            if (entry.LedgerWorker is { IsCompleted: false } pending) worker = pending;
            else
            {
                var generation = entry.Generation;
                worker = entry.LedgerWorker = Task.Run(() => ReadLedgerCoreAsync(root, entry, state, generation, _lifetime.Token));
            }
        }
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return state with { PendingSummaries = null, LedgerError = "Agent summaries are taking too long to read. They will refresh when the folder responds." };
        }
    }

    public void StartBackgroundRefresh()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) _lifetime = new();
            if (_background is { IsCompleted: false } && _backgroundLifetime?.IsCancellationRequested == false) return;
            _backgroundLifetime = _lifetime;
            var token = _lifetime.Token;
            _background = Task.Run(() => BackgroundLoopAsync(token));
        }
    }

    /// <summary>Stop owned background work. Cached state survives a canceled dashboard close.</summary>
    public async Task StopAsync()
    {
        Task[] workers;
        lock (_gate)
        {
            _lifetime.Cancel();
            _backgroundLifetime?.Cancel();
            workers = _projects.Values.Select(entry => (Task?)entry.Worker)
                .Concat(_repositories.Values.SelectMany(entry => new[] { (Task?)entry.Worker, entry.LedgerWorker }
                    .Concat(entry.DetailWorkers.Values)))
                .Append(_background).Where(task => task is not null).Cast<Task>().Distinct().ToArray();
        }
        if (workers.Length == 0) return;
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        GitCommandExitUnconfirmedException[] unconfirmed;
        lock (_gate) unconfirmed = _repositories.Values.SelectMany(entry => new Exception?[] { entry.State?.Failure, entry.UnconfirmedCommand })
            .OfType<GitCommandExitUnconfirmedException>().Distinct().ToArray();
        foreach (var command in unconfirmed)
            if (!await Task.Run(command.IsExitConfirmed).ConfigureAwait(false)) throw command;
    }

    private async Task<ProjectGitState> ReadProjectAsync(string projectId,
        IReadOnlyList<GitRepositoryDiscoveryFolder> folders, bool refresh, bool fetchRemote, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = CaptureFolders(folders);
        ProjectEntry entry;
        Task<ProjectGitState> worker;
        var followWithFetch = false;
        lock (_gate)
        {
            EnsureLifetime();
            entry = RegisterProject(projectId, captured);
            if (!refresh && entry.State is { } cached)
                return cached with { IsRefreshing = entry.Worker is { IsCompleted: false } };
            if (entry.Worker is { IsCompleted: false } pending)
            {
                worker = pending;
                followWithFetch = fetchRemote && !entry.WorkerFetches;
            }
            else
            {
                entry.WorkerFetches = fetchRemote;
                worker = entry.Worker = Task.Run(() => ReadProjectCoreAsync(projectId, entry, refresh, fetchRemote, _lifetime.Token));
            }
        }
        var result = await worker.WaitAsync(token).ConfigureAwait(false);
        lock (_gate)
            if (!_projects.TryGetValue(projectId, out var current) || !ReferenceEquals(current, entry))
                throw new InvalidOperationException("The configured project folders changed during Git inspection. Refresh the project before continuing.");
        return followWithFetch ? await ReadProjectAsync(projectId, captured, true, true, token).ConfigureAwait(false) : result;
    }

    private async Task<ProjectGitState> ReadProjectCoreAsync(string projectId, ProjectEntry entry,
        bool refresh, bool fetchRemote, CancellationToken token)
    {
        ProjectGitState state;
        try
        {
            var discovery = await GitRepositoryDiscovery.DiscoverAsync(entry.Folders, token).ConfigureAwait(false);
            var roots = discovery.Repositories.Select(item => item.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var repositories = await Task.WhenAll(roots.Select(root => refresh
                ? RefreshRepositoryAsync(root, fetchRemote: fetchRemote, token: token)
                : GetRepositoryAsync(root, token: token))).ConfigureAwait(false);
            state = new()
            {
                ProjectId = projectId, Folders = entry.Folders, Discovery = discovery,
                Repositories = repositories.ToDictionary(item => item.RepositoryRoot, StringComparer.OrdinalIgnoreCase),
                ReadAt = DateTimeOffset.UtcNow
            };
            state = UpdateMetadata(state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            state = (entry.State ?? new ProjectGitState { ProjectId = projectId, Folders = entry.Folders })
                with { Error = FailureMessage(exception), IsRefreshing = false };
        }
        var publish = false;
        lock (_gate)
        {
            if (_projects.TryGetValue(projectId, out var current) && ReferenceEquals(current, entry))
            {
                entry.State = state;
                publish = true;
            }
        }
        if (publish) Notify(projectId, null);
        return state;
    }

    private async Task<GitRepositoryState> ReadRepositoryAsync(string folder, string? remoteName,
        bool refresh, bool fetchRemote, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Normalize(folder);
        while (true)
        {
            RepositoryEntry entry;
            Task<GitRepositoryState>? worker;
            GitRepositoryState? saved;
            var followWithFetch = false;
            long generation;
            var followWithRefresh = false;
            var started = false;
            lock (_gate)
            {
                EnsureLifetime();
                entry = GetRepositoryEntry(root);
                generation = entry.Generation;
                if (!refresh && !entry.Dirty && entry.State is { } cached)
                {
                    worker = null;
                    saved = cached with { IsRefreshing = entry.RefreshInProgress };
                }
                else if (entry.Worker is { IsCompleted: false } pending)
                {
                    worker = pending;
                    generation = entry.WorkerGeneration;
                    followWithFetch = fetchRemote && !entry.WorkerFetches;
                    followWithRefresh = refresh && !entry.WorkerRefreshes;
                    saved = null;
                }
                else
                {
                    entry.WorkerFetches = fetchRemote;
                    entry.WorkerRefreshes = refresh;
                    entry.WorkerGeneration = generation;
                    entry.RefreshInProgress = true;
                    started = true;
                    saved = null;
                    worker = entry.Worker = Task.Run(() => ReadRepositoryCoreAsync(root, entry, generation, fetchRemote,
                        remoteName, _lifetime.Token));
                }
            }
            if (started) Notify(null, root);
            var result = saved ?? await worker!.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
                if (entry.Generation != generation) { refresh = false; continue; }
            if (followWithFetch || followWithRefresh)
            {
                refresh = true;
                continue;
            }
            await RequireConfirmedCommandAsync(entry, token).ConfigureAwait(false);
            long ledgerGeneration;
            lock (_gate) ledgerGeneration = entry.LedgerGeneration;
            var details = await ReadDetailsAsync(root, entry, result, remoteName, token).ConfigureAwait(false);
            lock (_gate)
                if (entry.Generation != generation || entry.LedgerGeneration != ledgerGeneration
                    || !ReferenceEquals(entry.State?.Snapshot, details.Snapshot))
                { refresh = false; continue; }
            if (details.Failure is GitCommandExitUnconfirmedException held
                && !await Task.Run(held.IsExitConfirmed, token).ConfigureAwait(false)) throw held;
            lock (_gate)
            {
                if (entry.Generation != generation || entry.LedgerGeneration != ledgerGeneration
                    || !ReferenceEquals(entry.State?.Snapshot, details.Snapshot))
                { refresh = false; continue; }
                return details with { IsRefreshing = entry.RefreshInProgress };
            }
        }
    }

    private async Task<GitRepositoryState> ReadRepositoryCoreAsync(string root, RepositoryEntry entry,
        long generation, bool fetchRemote, string? requestedRemote, CancellationToken token)
    {
        GitRepositoryState state;
        var acquired = false;
        try
        {
            await _readSlots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            await RequireConfirmedCommandAsync(entry, token).ConfigureAwait(false);
            var snapshot = await GitRepositoryService.ReadAsync(root, token).ConfigureAwait(false);
            if (snapshot.RepositoryRoot is { } actual && !SameRoot(actual, root))
                throw new InvalidOperationException("The selected folder now resolves to another repository. Refresh project discovery before continuing.");
            state = new()
            {
                RepositoryRoot = root, Snapshot = snapshot, ReadAt = DateTimeOffset.UtcNow,
                LastFetchAt = entry.State?.LastFetchAt, FetchError = entry.State?.FetchError
            };
            state = await ReadLedgerStateAsync(state, token).ConfigureAwait(false);
            if (fetchRemote && snapshot.IsRepository)
            {
                var remote = requestedRemote is null ? state.ActiveRemote
                    : snapshot.Remotes.SingleOrDefault(item => item.Name == requestedRemote && item.UrlCanCopy);
                if (remote is not null)
                {
                    string? fetchError = null;
                    var fetched = false;
                    try
                    {
                        var provider = remote.Provider ?? GitConnectionService.InferProvider(remote.FetchUrl);
                        if (provider is null)
                            throw new InvalidOperationException("Choose this remote's provider in Git Config before automatic refresh can verify access.");
                        var access = await GitConnectionWarmup.GetOrCheckAsync(root, provider.Value, remote.FetchUrl, token).ConfigureAwait(false);
                        if (!access.Ready) throw new InvalidOperationException(access.Message);
                        if (requestedRemote is null)
                        {
                            var currentSelection = await ReadLedgerStateAsync(state, token).ConfigureAwait(false);
                            if (currentSelection.ActiveRemote is not { } active
                                || AgentGitChangeStore.ConnectionId(active) != AgentGitChangeStore.ConnectionId(remote))
                                throw new InvalidOperationException("The active Git connection changed during refresh. Review the selected connection before fetching.");
                        }
                        await GitRepositoryService.RunWithSnapshotAsync(snapshot,
                            current => GitRepositoryService.FetchAsync(root, remote.Name, current, strictSsh: true), token).ConfigureAwait(false);
                        fetched = true;
                    }
                    catch (GitCommandExitUnconfirmedException) { throw; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) { fetchError = FailureMessage(exception); }
                    // A failed fetch can still update refs. Publish its actual local outcome once.
                    snapshot = await GitRepositoryService.ReadAsync(root, token).ConfigureAwait(false);
                    if (snapshot.RepositoryRoot is null || !SameRoot(snapshot.RepositoryRoot, root))
                        throw new InvalidOperationException("The repository changed during refresh. Review its configured folder.");
                    state = await ReadLedgerStateAsync(state with
                    {
                        Snapshot = snapshot, FetchError = fetchError,
                        LastFetchAt = fetched ? DateTimeOffset.UtcNow : state.LastFetchAt,
                        ReadAt = DateTimeOffset.UtcNow
                    }, token).ConfigureAwait(false);
                }
            }
            state = state with { ActiveConnection = await GitActiveConnectionService.ReadAsync(state, token).ConfigureAwait(false) };
            var detailRemote = state.ActiveRemote?.Name ?? "";
            if (snapshot.IsRepository) state = await BuildDetailsAsync(state, detailRemote, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
                if (entry.WorkerGeneration == generation) entry.RefreshInProgress = false;
            Notify(null, root);
            throw;
        }
        catch (Exception exception)
        {
            state = (entry.State ?? new GitRepositoryState { RepositoryRoot = root }) with
            {
                Error = FailureMessage(exception), Failure = exception, IsRefreshing = false,
                PendingSummaries = null, ActiveConnection = new()
                {
                    Label = "Connection unavailable",
                    Detail = "The repository could not be read. Refresh Git status to read the active connection."
                }
            };
        }
        finally { if (acquired) _readSlots.Release(); }
        var publish = false;
        lock (_gate)
        {
            if (state.Failure is GitCommandExitUnconfirmedException command) entry.UnconfirmedCommand = command;
            if (entry.Generation == generation)
            {
                entry.State = state;
                entry.LedgerGeneration++;
                entry.RefreshInProgress = false;
                entry.Dirty = false;
                entry.Details.Clear();
                entry.DetailWorkers.Clear();
                entry.Details[state.RemoteName] = state;
                publish = true;
            }
            else if (entry.WorkerGeneration == generation) entry.RefreshInProgress = false;
        }
        if (publish) PublishRepository(root, state);
        return state;
    }

    private async Task<GitRepositoryState> ReadDetailsAsync(string root, RepositoryEntry entry,
        GitRepositoryState state, string? requestedRemote, CancellationToken token)
    {
        if (state.Error is not null || state.Snapshot is not { IsRepository: true }) return state;
        var remoteName = requestedRemote ?? state.ActiveRemote?.Name ?? "";
        Task<GitRepositoryState> worker;
        lock (_gate)
        {
            if (entry.Details.TryGetValue(remoteName, out var cached))
                return cached with { IsRefreshing = entry.RefreshInProgress };
            if (entry.RefreshInProgress)
                return state with { RemoteName = remoteName, Comparison = null, PowerDetails = null, IsRefreshing = true };
            if (!entry.DetailWorkers.TryGetValue(remoteName, out worker!) || worker.IsCanceled || worker.IsFaulted)
            {
                var generation = entry.Generation;
                var ledgerGeneration = entry.LedgerGeneration;
                var lifetime = _lifetime.Token;
                worker = entry.DetailWorkers[remoteName] = Task.Run(async () =>
                {
                    GitRepositoryState details;
                    await _readSlots.WaitAsync(lifetime).ConfigureAwait(false);
                    try { details = await BuildDetailsAsync(state, remoteName, lifetime).ConfigureAwait(false); }
                    finally { _readSlots.Release(); }
                    var publish = false;
                    lock (_gate)
                    {
                        if (details.Failure is GitCommandExitUnconfirmedException command) entry.UnconfirmedCommand = command;
                        if (entry.Generation == generation && entry.LedgerGeneration == ledgerGeneration
                            && ReferenceEquals(entry.State?.Snapshot, state.Snapshot))
                        {
                            entry.Details[remoteName] = details;
                            if (remoteName == (entry.State?.ActiveRemote?.Name ?? ""))
                            {
                                entry.State = details;
                                publish = true;
                            }
                        }
                    }
                    if (publish) PublishRepository(root, details);
                    return details;
                });
            }
        }
        return await worker.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<GitRepositoryState> BuildDetailsAsync(GitRepositoryState state,
        string remoteName, CancellationToken token)
    {
        var snapshot = state.Snapshot!;
        GitRemoteComparison? comparison = null;
        GitPowerDetails? power = null;
        string? comparisonError = null;
        string? powerError = null;
        Exception? failure = null;
        if (remoteName.Length > 0 && !snapshot.Remotes.Any(remote => remote.Name == remoteName && remote.UrlCanCopy))
            return state with { RemoteName = remoteName, Comparison = null, PowerDetails = null,
                ComparisonError = "The selected remote changed or is unavailable. Review Git Config.",
                PowerError = "The selected remote changed or is unavailable. Review Git Config." };
        try
        {
            await GitRepositoryService.RunWithSnapshotAsync(snapshot, async current =>
            {
                if (remoteName.Length > 0 && !snapshot.IsDetached)
                {
                    try { comparison = await GitRepositoryService.ReadComparisonAsync(state.RepositoryRoot,
                        remoteName, snapshot.Branch, current).ConfigureAwait(false); }
                    catch (GitCommandExitUnconfirmedException) { throw; }
                    catch (OperationCanceledException) when (current.IsCancellationRequested) { throw; }
                    catch (Exception exception) { comparisonError = FailureMessage(exception); }
                }
                try { power = await GitRepositoryService.ReadPowerDetailsAsync(state.RepositoryRoot, remoteName, current).ConfigureAwait(false); }
                catch (GitCommandExitUnconfirmedException) { throw; }
                catch (OperationCanceledException) when (current.IsCancellationRequested) { throw; }
                catch (Exception exception) { powerError = FailureMessage(exception); }
                if (comparison is { Available: true } && powerError is not null)
                {
                    comparison = null;
                    comparisonError = powerError;
                }
                else if (comparison is { Available: true } && power is not null
                    && !string.Equals(comparison.Branch, power.ReleaseBranch, StringComparison.Ordinal))
                {
                    comparison = null;
                    comparisonError = "The release branch changed while reading Git details. Refresh to obtain current totals.";
                }
                return "";
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GitCommandExitUnconfirmedException exception)
        {
            failure = exception;
            comparisonError = powerError = FailureMessage(exception);
        }
        return state with
        {
            RemoteName = remoteName, Comparison = comparison, ComparisonError = comparisonError,
            PowerDetails = power, PowerError = powerError,
            Error = failure is null ? state.Error : FailureMessage(failure), Failure = failure ?? state.Failure,
            PendingSummaries = state.LedgerError is null ? SummariesFor(state.SummaryBatches, snapshot, remoteName) : null
        };
    }

    private static AgentGitSummaryBatch? SummariesFor(IReadOnlyList<AgentGitSummaryBatch> summaries,
        GitRepositorySnapshot snapshot, string remoteName)
    {
        var remote = snapshot.Remotes.SingleOrDefault(item => item.Name == remoteName && item.UrlCanCopy);
        if (remote is null || snapshot.IsDetached) return null;
        var connection = AgentGitChangeStore.ConnectionId(remote);
        return summaries.SingleOrDefault(batch => batch.RemoteName == remoteName && batch.ConnectionId == connection)
            ?? new() { RepositoryRoot = snapshot.RepositoryRoot!, Branch = snapshot.Branch,
                RemoteName = remoteName, ConnectionId = connection };
    }

    private async Task<GitRepositoryState> ReadLedgerCoreAsync(string root, RepositoryEntry entry,
        GitRepositoryState before, long generation, CancellationToken token)
    {
        var after = await ReadLedgerStateAsync(before, token).ConfigureAwait(false);
        var selectedChanged = before.ActiveRemote?.Name != after.ActiveRemote?.Name
            || before.Selection != after.Selection || before.SelectionError != after.SelectionError;
        if (before.ActiveRemote != after.ActiveRemote)
        {
            await _readSlots.WaitAsync(token).ConfigureAwait(false);
            try { after = after with { ActiveConnection = await GitActiveConnectionService.ReadAsync(after, token).ConfigureAwait(false) }; }
            catch (GitCommandExitUnconfirmedException exception)
            {
                lock (_gate) entry.UnconfirmedCommand = exception;
                after = after with
                {
                    Error = FailureMessage(exception), Failure = exception,
                    ActiveConnection = new() { Label = "Connection unavailable",
                        Detail = "The Git connection read has not exited. Confirm its exit before retrying." }
                };
            }
            finally { _readSlots.Release(); }
        }
        var changed = !SameLedger(before, after);
        var publish = false;
        lock (_gate)
        {
            if (entry.Generation == generation && ReferenceEquals(entry.State?.Snapshot, before.Snapshot)
                && entry.Worker is not { IsCompleted: false })
            {
                if (selectedChanged)
                {
                    after = after with { RemoteName = after.ActiveRemote?.Name ?? "", Comparison = null,
                        ComparisonError = null, PowerDetails = null, PowerError = null };
                    entry.Details.Clear();
                    entry.DetailWorkers.Clear();
                }
                else
                {
                    foreach (var key in entry.Details.Keys.ToArray())
                        entry.Details[key] = entry.Details[key] with
                        {
                            Selection = after.Selection, SelectionError = after.SelectionError,
                            LedgerError = after.LedgerError, SummaryBatches = after.SummaryBatches,
                            PendingSummaries = after.LedgerError is null
                                ? SummariesFor(after.SummaryBatches, before.Snapshot!, key) : null
                        };
                }
                entry.State = after;
                if (changed) entry.LedgerGeneration++;
                publish = changed;
            }
            else return entry.State ?? before;
        }
        if (publish) PublishRepository(root, after);
        return after;
    }

    private static async Task<GitRepositoryState> ReadLedgerStateAsync(GitRepositoryState state, CancellationToken token)
    {
        if (state.Snapshot is not { IsRepository: true } snapshot)
            return state with { ActiveConnection = GitActiveConnectionService.NoRemote(state) };
        try
        {
            var ledger = await AgentGitChangeStore.ReadCachedAsync(snapshot, token).ConfigureAwait(false);
            var (active, source, error) = SelectRemote(snapshot, ledger.Selection);
            var connectionId = active is null ? null : AgentGitChangeStore.ConnectionId(active);
            var summaries = active is null || snapshot.IsDetached ? null
                : ledger.Summaries.SingleOrDefault(batch => batch.RemoteName == active.Name && batch.ConnectionId == connectionId)
                    ?? new AgentGitSummaryBatch { RepositoryRoot = state.RepositoryRoot, Branch = snapshot.Branch,
                        RemoteName = active.Name, ConnectionId = connectionId! };
            var updated = state with { Selection = ledger.Selection, ActiveRemote = active, SelectionSource = source,
                SelectionError = error, LedgerError = null, PendingSummaries = summaries, SummaryBatches = ledger.Summaries };
            return active is null ? updated with { ActiveConnection = GitActiveConnectionService.NoRemote(updated) } : updated;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var updated = state with { Selection = null, ActiveRemote = null, SelectionSource = null,
                SelectionError = FailureMessage(exception), LedgerError = FailureMessage(exception), PendingSummaries = null, SummaryBatches = [] };
            return updated with { ActiveConnection = GitActiveConnectionService.NoRemote(updated) };
        }
    }

    private static (GitRemoteInfo? Remote, string? Source, string? Error) SelectRemote(
        GitRepositorySnapshot snapshot, AgentGitConnectionSelection? selection)
    {
        var eligible = snapshot.Remotes.Where(item => item.UrlCanCopy).ToArray();
        if (selection is not null)
        {
            var selected = eligible.SingleOrDefault(item => item.Name == selection.RemoteName
                && AgentGitChangeStore.ConnectionId(item) == selection.ConnectionId);
            return (selected, "saved_workspace_selection", selected is null
                ? "The saved active connection changed or was removed. Select a connection in Git Config." : null);
        }
        var upstream = eligible.SingleOrDefault(item => snapshot.Upstream?.StartsWith(item.Name + "/", StringComparison.Ordinal) == true);
        if (upstream is not null) return (upstream, "current_branch_upstream", null);
        if (snapshot.Upstream is null && snapshot.Remotes.Count == 1 && eligible.Length == 1)
            return (eligible[0], "only_configured_connection", null);
        return (null, null, eligible.Length == 0 ? "No supported credential-free connection is configured. Complete Git setup."
            : "Choose the active connection in Git Config.");
    }

    private async Task BackgroundLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                ProjectGitRegistration[] captured;
                lock (_gate) captured = _projects.Select(pair => new ProjectGitRegistration(pair.Key, pair.Value.Folders)).ToArray();
                // Refresh configured discovery locally, then fetch each distinct checkout once.
                var projects = await Task.WhenAll(captured.Select(async project =>
                {
                    await _backgroundSlots.WaitAsync(token).ConfigureAwait(false);
                    try { return await RefreshProjectDiscoveryAsync(project, token).ConfigureAwait(false); }
                    catch (InvalidOperationException) { return null; }
                    finally { _backgroundSlots.Release(); }
                })).ConfigureAwait(false);
                var roots = projects.Where(project => project is { Error: null })
                    .SelectMany(project => project!.Repositories.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                await Task.WhenAll(roots.Select(async root =>
                {
                    await _backgroundSlots.WaitAsync(token).ConfigureAwait(false);
                    try { await RefreshRepositoryAsync(root, fetchRemote: true, token: token).ConfigureAwait(false); }
                    finally { _backgroundSlots.Release(); }
                })).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<ProjectGitState> RefreshProjectDiscoveryAsync(ProjectGitRegistration registration,
        CancellationToken token)
    {
        Task<ProjectGitState> worker;
        lock (_gate)
        {
            if (!_projects.TryGetValue(registration.ProjectId, out var entry)
                || !SameFolders(entry.Folders, registration.Folders))
                throw new InvalidOperationException("The project was removed or changed before background refresh.");
            if (entry.Worker is { IsCompleted: false } pending) worker = pending;
            else
            {
                entry.WorkerFetches = false;
                worker = entry.Worker = Task.Run(() => ReadProjectCoreAsync(registration.ProjectId, entry,
                    refresh: false, fetchRemote: false, token));
            }
        }
        return await worker.WaitAsync(token).ConfigureAwait(false);
    }

    private void PublishRepository(string root, GitRepositoryState state)
    {
        lock (_gate)
        {
            foreach (var project in _projects.Values)
            {
                if (project.State is not { } saved || !BelongsToProject(saved, root)) continue;
                var repositories = new Dictionary<string, GitRepositoryState>(saved.Repositories, StringComparer.OrdinalIgnoreCase)
                    { [root] = state };
                project.State = UpdateMetadata(saved with { Repositories = repositories });
            }
        }
        Notify(null, root);
    }

    private static ProjectGitState UpdateMetadata(ProjectGitState state)
    {
        GitBranchSnapshot? MetadataFor(string root)
        {
            if (!state.Repositories.TryGetValue(root, out var repository)) return null;
            if (repository.Snapshot is not { } snapshot)
                return repository.Error is null ? null : new(root, "Branch unavailable", repository.Error, true, GitBranchState.Unavailable);
            return !snapshot.IsRepository ? new GitBranchSnapshot(null, "Not a Git repository",
                    $"No Git repository is available at {root}.", false, GitBranchState.NotRepository)
                : repository.Error is not null ? new GitBranchSnapshot(root, "Branch unavailable", repository.Error,
                    true, GitBranchState.Unavailable)
                : snapshot.IsDetached ? new(root, $"Detached · {snapshot.HeadCommit[..Math.Min(8, snapshot.HeadCommit.Length)]}",
                    $"Detached HEAD: {snapshot.HeadCommit}\nRepository: {root}", true, GitBranchState.Detached)
                : new GitBranchSnapshot(root, snapshot.Branch, $"Active branch: {snapshot.Branch}\nRepository: {root}",
                    true, GitBranchState.Branch);
        }
        var resolved = state.Discovery.ResolvedFolders.Select(folder =>
            MetadataFor(folder.Snapshot.RepositoryPath ?? folder.Directory) is { } metadata
                ? folder with { Snapshot = metadata } : folder).ToList();
        var choices = state.Discovery.Folders.Select(choice => MetadataFor(choice.Directory) is { } metadata
            ? choice with { IsRepository = metadata.HasRepository, State = metadata.State, Detail = metadata.Detail }
            : choice).ToList();
        foreach (var repository in state.Repositories.Values.Where(repository => repository.Snapshot?.IsRepository == true))
        {
            if (MetadataFor(repository.RepositoryRoot) is not { } metadata) continue;
            if (!resolved.Any(folder => SameRoot(folder.Directory, repository.RepositoryRoot)))
                resolved.Add(new(repository.RepositoryRoot, metadata));
            if (!choices.Any(choice => SameRoot(choice.Directory, repository.RepositoryRoot)))
                choices.Add(new(Path.GetFileName(repository.RepositoryRoot), repository.RepositoryRoot,
                    metadata.HasRepository, metadata.State, metadata.Detail));
        }
        return state with { Discovery = state.Discovery with
            { ResolvedFolders = resolved.ToArray(), Folders = choices.ToArray(),
                Repositories = choices.Where(choice => choice.IsRepository).ToArray() } };
    }

    private static bool BelongsToProject(ProjectGitState project, string root) =>
        project.Repositories.ContainsKey(root)
        || project.Discovery.Folders.Any(folder => SameRoot(folder.Directory, root))
        || project.Discovery.ResolvedFolders.Any(folder => SameRoot(folder.Directory, root))
        || project.Folders.Any(folder => SameRoot(folder.Directory, root));

    private void Notify(string? projectId, string? root)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<ProjectGitChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(projectId, root)); }
            catch (Exception) { /* One closing UI consumer cannot break shared Git publication. */ }
        }
    }

    private ProjectEntry RegisterProject(string id, GitRepositoryDiscoveryFolder[] folders)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A project identity is required.", nameof(id));
        if (_projects.TryGetValue(id, out var existing) && SameFolders(existing.Folders, folders)) return existing;
        return _projects[id] = new(folders);
    }

    private RepositoryEntry GetRepositoryEntry(string root)
    {
        if (!_repositories.TryGetValue(root, out var entry)) _repositories[root] = entry = new();
        return entry;
    }

    private void EnsureLifetime()
    {
        // A protected/canceled close may resume the dashboard. Do not strand later UI readers.
        if (_lifetime.IsCancellationRequested) _lifetime = new();
    }

    private async Task RequireConfirmedCommandAsync(RepositoryEntry entry, CancellationToken token)
    {
        GitCommandExitUnconfirmedException? unconfirmed;
        lock (_gate) unconfirmed = entry.UnconfirmedCommand ?? entry.State?.Failure as GitCommandExitUnconfirmedException;
        if (unconfirmed is null) return;
        if (!await Task.Run(unconfirmed.IsExitConfirmed, token).ConfigureAwait(false)) throw unconfirmed;
        lock (_gate)
            if (ReferenceEquals(entry.UnconfirmedCommand, unconfirmed)) entry.UnconfirmedCommand = null;
    }

    private static bool SameLedger(GitRepositoryState left, GitRepositoryState right) =>
        left.Selection == right.Selection && left.ActiveRemote?.Name == right.ActiveRemote?.Name
        && left.ActiveConnection == right.ActiveConnection
        && left.SelectionError == right.SelectionError && left.LedgerError == right.LedgerError
        && SameSummaries(left.PendingSummaries, right.PendingSummaries)
        && left.SummaryBatches.Count == right.SummaryBatches.Count
        && left.SummaryBatches.Zip(right.SummaryBatches).All(pair => SameSummaries(pair.First, pair.Second));

    private static bool SameSummaries(AgentGitSummaryBatch? left, AgentGitSummaryBatch? right) =>
        left is null && right is null || left is not null && right is not null
        && left.RepositoryRoot == right.RepositoryRoot && left.Branch == right.Branch
        && left.RemoteName == right.RemoteName && left.ConnectionId == right.ConnectionId
        && left.Entries.Count == right.Entries.Count && left.Entries.Zip(right.Entries).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.UpdateId == pair.Second.UpdateId
            && pair.First.CreatedAt == pair.Second.CreatedAt && pair.First.ViewedAt == pair.Second.ViewedAt
            && pair.First.ConsumedCommitId == pair.Second.ConsumedCommitId
            && pair.First.Bullets.SequenceEqual(pair.Second.Bullets, StringComparer.Ordinal));

    private static GitRepositoryDiscoveryFolder[] CaptureFolders(IReadOnlyList<GitRepositoryDiscoveryFolder> folders) =>
        folders.Select(folder => folder with { Directory = Normalize(folder.Directory) })
            .GroupBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GitRepositoryDiscoveryFolder(string.Join(" / ", group.Select(folder => folder.Label).Distinct()),
                group.Key, group.Any(folder => folder.ScanImmediateChildren))).ToArray();

    private static bool SameFolders(IReadOnlyList<GitRepositoryDiscoveryFolder> left,
        IReadOnlyList<GitRepositoryDiscoveryFolder> right) => left.Count == right.Count
        && left.All(folder => right.Any(other => string.Equals(folder.Directory, other.Directory, StringComparison.OrdinalIgnoreCase)
            && folder.ScanImmediateChildren == other.ScanImmediateChildren));

    private static string Normalize(string folder) => GitConnectionService.NormalizeRoot(folder);
    private static bool SameRoot(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    private static string FailureMessage(Exception exception) => exception is TimeoutException or OperationCanceledException
        ? "Git refresh timed out. Existing information remains visible; use Force refresh to retry."
        : SensitiveDataProtection.Redact(exception.Message);

    private sealed class ProjectEntry(GitRepositoryDiscoveryFolder[] folders)
    {
        public GitRepositoryDiscoveryFolder[] Folders { get; } = folders;
        public ProjectGitState? State { get; set; }
        public Task<ProjectGitState>? Worker { get; set; }
        public bool WorkerFetches { get; set; }
    }

    private sealed class RepositoryEntry
    {
        public GitRepositoryState? State { get; set; }
        public Task<GitRepositoryState>? Worker { get; set; }
        public Task<GitRepositoryState>? LedgerWorker { get; set; }
        public bool WorkerFetches { get; set; }
        public bool WorkerRefreshes { get; set; }
        public long WorkerGeneration { get; set; }
        public bool RefreshInProgress { get; set; }
        public long LedgerGeneration { get; set; }
        public GitCommandExitUnconfirmedException? UnconfirmedCommand { get; set; }
        public bool Dirty { get; set; }
        public long Generation { get; set; }
        public Dictionary<string, GitRepositoryState> Details { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Task<GitRepositoryState>> DetailWorkers { get; } = new(StringComparer.Ordinal);
    }
}
