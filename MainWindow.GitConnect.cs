using System.IO;
using System.Text.Json;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private GitProjectConnectionLease? _gitProjectConnectionLease;

    private async Task<IDisposable> PrepareGitProjectConnectAsync(ProjectViewModel project,
        GitProjectConnectRequest request)
    {
        if (_gitProjectConnectionLease is not null || !ReferenceEquals(SelectedProjectItem, project) ||
            !CanEdit || IsEditing || _checkingStartupServices || _closed || _apiLaunchProfileEditing)
            throw new InvalidOperationException("Finish the current project operation before connecting Git.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceFolder));
        if (!BranchFolders(project).Any(folder => SameGitConnectPath(folder.Directory, source)))
            throw new InvalidOperationException("Choose this project's configured root or service folder.");
        var relocate = request.Mode == GitProjectConnectMode.CopyIntoRepository;
        if (relocate && !SameGitConnectPath(source, _store.ResolveRoot(project.Profile)))
            throw new InvalidOperationException("Choose Project root when copying this project into another repository.");

        var lease = new GitProjectConnectionLease(this, project, request, source,
            JsonSerializer.Serialize(project.Profile), GetRunners(project.Profile).ToArray());
        _gitProjectConnectionLease = lease;
        SetSavingProjectEdits(true);
        try
        {
            // A modal dialog still pumps bridge requests. Keep dashboard service actions
            // and new queue dispatch blocked through copy, detached save, and rebind.
            _queueDashboardPermit?.SetEnabled(false);
            await RequireGitConnectIdleAsync(lease);
            if (relocate)
                await Task.Run(() => ValidateGitConnectRelocation(project.Profile, source));
            RequireGitConnectCurrent(lease);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task RequireGitConnectIdleAsync(GitProjectConnectionLease lease)
    {
        RequireGitConnectCurrent(lease);
        RequireGitConnectAgentIdle(lease.Project.Profile.Id);
        await Task.WhenAll(lease.Services.Select(service => service.Runner.RefreshForProfileEditAsync()));
        RequireGitConnectCurrent(lease);
        foreach (var service in lease.Services)
        {
            service.Update();
            if (service.IsBusy || service.IsStopping || service.Runner.HasManagedProcess ||
                !service.Runner.HasVerifiedNoServiceProcesses || service.Runner.Snapshot.ProcessIds.Count != 0)
                throw new InvalidOperationException($"Stop {service.Name} and complete a fresh process check before connecting Git.");
        }
        await Task.Run(() => RequireGitConnectQueueIdle(lease.Project.Profile.Id));
        RequireGitConnectCurrent(lease);
        RequireGitConnectAgentIdle(lease.Project.Profile.Id);
    }

    private void RequireGitConnectCurrent(GitProjectConnectionLease lease)
    {
        if (!ReferenceEquals(_gitProjectConnectionLease, lease) ||
            !ReferenceEquals(SelectedProjectItem, lease.Project) ||
            !_settings.Projects.Contains(lease.Project.Profile) ||
            JsonSerializer.Serialize(lease.Project.Profile) != lease.ProfileSnapshot ||
            !lease.Services.SequenceEqual(GetRunners(lease.Project.Profile)) ||
            _closeRequested || _closing || _closed || _batchBusy || _forceStopBatchBusy)
            throw new InvalidOperationException("The project changed while connecting Git. Original project settings were retained.");
    }

    private void RequireGitConnectAgentIdle(string projectId)
    {
        var reservation = JsonSerializer.SerializeToElement(_agentCoordination.ReservationStatus(projectId));
        if (reservation.GetProperty("owner").ValueKind != JsonValueKind.Null ||
            reservation.GetProperty("waiting").GetInt32() > 0 ||
            _projectAgentActivity.GetActivity(projectId).RunningCount > 0 ||
            _settings.Projects.Single(project => project.Id == projectId).Services.Any(service =>
                _agentCoordination.ServiceActivity(projectId, service.Id).Any(activity =>
                    !activity.IsStale && (activity.DeclaredUse || activity.HoldsReservation || activity.IsActing))))
            throw new InvalidOperationException("Wait for this project's active agents and reservations to finish before connecting Git.");
    }

    private static void RequireGitConnectQueueIdle(string projectId)
    {
        var store = new ProjectTaskStore();
        var data = store.Load();
        if (!store.CanSave)
            throw new InvalidOperationException("Queue state could not be verified. Open Notes & queue and resolve its storage error before connecting Git.");
        var queue = data.Queues.FirstOrDefault(item => item.ProjectId == projectId);
        if (queue?.Enabled == true || queue?.AutomaticLoopEnabled == true ||
            queue is { RecoveryState: not ProjectQueueRecoveryState.None } ||
            data.QueueItems.Any(item => item.ProjectId == projectId && item.State is
                ProjectQueueItemState.Starting or ProjectQueueItemState.Running or
                ProjectQueueItemState.Recovering or ProjectQueueItemState.NeedsAttention) ||
            data.Receipts.Any(receipt => receipt.Snapshot.ProjectId == projectId && receipt.FinishedAt is null))
            throw new InvalidOperationException("Pause this project's queue and automatic loop, and resolve active attempts before connecting Git. Saved notes and queue assignments are retained.");
    }

    private void ValidateGitConnectRelocation(ProjectProfile project, string source)
    {
        foreach (var service in project.Services)
        {
            _ = GitConnectRelativePath(source, _store.ResolveWorkingDirectory(project, service), $"{service.Name} working folder");
            foreach (var command in new[] { service.StartCommand, service.CleanCommand, service.SetupCommand,
                         service.ApiConfiguration?.LaunchCommand ?? "" })
                if (command.Replace("\\\\", "\\", StringComparison.Ordinal).Replace('/', '\\')
                    .Contains(source.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{service.Name} has a command referring to the original project path. Change it to a relative command before copying into another repository.");

            if (!service.Kind.Equals("API", StringComparison.OrdinalIgnoreCase) || service.IsCommandApi) continue;
            var folder = _store.ResolveWorkingDirectory(project, service);
            if (!Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Any()) continue;
            var secrets = new ApiSecretStore(folder);
            if (!secrets.HasUserSecretsReference &&
                (secrets.Load(false).HasProtectedStore || secrets.Load(true).HasProtectedStore))
                throw new InvalidOperationException($"{service.Name} has encrypted API settings tied to its current folder. Connect an empty repository in place, or explicitly migrate that API configuration before relocating the project.");
        }
        if (!string.IsNullOrWhiteSpace(project.SQLiteDatabasePath))
        {
            _ = GitConnectRelativePath(source, Path.GetFullPath(project.SQLiteDatabasePath, source), "SQLite database");
            if (new[] { ".db", ".sqlite", ".sqlite3" }.Contains(Path.GetExtension(project.SQLiteDatabasePath), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This project has a configured SQLite database that the source copy excludes. Connect an empty repository in place to keep using that database, or explicitly move and reconfigure its data before copying into another repository. The original project and database are retained.");
        }
        if (!string.IsNullOrWhiteSpace(project.AutoRestartWatchPath))
            _ = GitConnectRelativePath(source, Path.GetFullPath(project.AutoRestartWatchPath, source), "Saved watch folder");
    }

    private async Task ApplyGitProjectConnectAsync(ProjectViewModel project, GitProjectConnectResult result)
    {
        var lease = _gitProjectConnectionLease;
        if (lease is null || !ReferenceEquals(lease.Project, project) || !SameGitConnectPath(lease.Source, result.SourceFolder))
            throw new InvalidOperationException("The Git connection no longer matches the selected project. Project settings were retained.");
        await RequireGitConnectIdleAsync(lease);
        if (lease.Request.Mode == GitProjectConnectMode.EmptyRemoteInPlace)
        {
            if (!SameGitConnectPath(result.ProjectFolder, lease.Source))
                throw new InvalidOperationException("Git returned a different project folder. Project settings were retained.");
            Notice = result.Message;
            return;
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.ProjectFolder));
        var expectedRepository = Path.GetFullPath(lease.Request.DestinationFolder!);
        var expectedProject = Path.GetFullPath(lease.Request.RepositorySubfolder!, expectedRepository);
        if (!SameGitConnectPath(result.RepositoryRoot, expectedRepository) ||
            !SameGitConnectPath(destination, expectedProject) ||
            SameGitConnectPath(destination, lease.Source) || !Directory.Exists(destination))
            throw new InvalidOperationException("The copied project folder could not be verified. Project settings were retained.");
        var saved = false;
        var replacements = new List<ServiceViewModel>();
        var preparedSources = new List<ServiceRunner>();
        try
        {
            await Task.Run(() => ValidateGitConnectRelocation(project.Profile, lease.Source));
            RequireGitConnectCurrent(lease);
            var candidateSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            var candidate = candidateSettings.Projects.Single(item => item.Id == project.Profile.Id);
            candidate.RootPath = destination;
            foreach (var service in candidate.Services)
            {
                var original = project.Profile.Services.Single(item => item.Id == service.Id);
                service.WorkingDirectory = GitConnectRelativePath(lease.Source,
                    _store.ResolveWorkingDirectory(project.Profile, original), $"{service.Name} working folder");
                if (!Directory.Exists(_store.ResolveWorkingDirectory(candidate, service)))
                    throw new InvalidOperationException($"The copied folder for {service.Name} is missing. Project settings were retained.");
            }
            if (!string.IsNullOrWhiteSpace(candidate.SQLiteDatabasePath))
            {
                candidate.SQLiteDatabasePath = GitConnectRelativePath(lease.Source,
                    Path.GetFullPath(candidate.SQLiteDatabasePath, lease.Source), "SQLite database");
                if (!File.Exists(Path.GetFullPath(candidate.SQLiteDatabasePath, destination)))
                    throw new InvalidOperationException("The configured SQLite database was not included in the copy. Original project settings were retained.");
            }
            if (!string.IsNullOrWhiteSpace(candidate.AutoRestartWatchPath))
                candidate.AutoRestartWatchPath = GitConnectRelativePath(lease.Source,
                    Path.GetFullPath(candidate.AutoRestartWatchPath, lease.Source), "Saved watch folder");

            // Inspect destination without adopting recovery identities or jobs. A
            // rejected destination must never acquire processes that cleanup can stop.
            await RequireGitConnectDestinationIdleAsync(candidate);
            RequireGitConnectCurrent(lease);
            foreach (var service in candidate.Services) replacements.Add(CreateServiceViewModel(candidate, service));
            foreach (var service in lease.Services)
            {
                preparedSources.Add(service.Runner);
                await service.Runner.PrepareKeepRunningAsync();
            }
            await RequireGitConnectIdleAsync(lease);
            await RequireGitConnectDestinationIdleAsync(candidate);

            // No await between this final identity check, detached atomic save, and
            // swapping the dashboard's profiles. Notes and queue stores are untouched.
            RequireGitConnectCurrent(lease);
            var currentSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            currentSettings.Projects[currentSettings.Projects.FindIndex(item => item.Id == candidate.Id)] = candidate;
            _store.Save(currentSettings);
            saved = true;
            FlushConsoleOutput();
            foreach (var replacement in replacements)
                foreach (var line in lease.Services.Single(old => old.Profile.Id == replacement.Profile.Id).ConsoleLines)
                    replacement.AppendConsoleLine(line);
            _settings.Projects[_settings.Projects.IndexOf(project.Profile)] = candidate;
            Projects[Projects.IndexOf(project.Profile)] = candidate;
            _runners[candidate.Id] = replacements;
            _refreshingProjectList = true;
            try
            {
                ProjectItems[ProjectItems.IndexOf(project)] = new(candidate, replacements)
                { IsDetailsExpanded = project.IsDetailsExpanded };
            }
            finally { _refreshingProjectList = false; }
            await Task.Run(() =>
            {
                foreach (var service in lease.Services) service.Runner.DisposeKeepingServicesRunning();
            });
            await RefreshProjectListAsync(candidate, saveSelection: false);
            RefreshServiceConsoleWindows();
            SynchronizeProjectGitRegistrations();
            Notice = $"Connected {candidate.Name}. Launcher now uses {destination}. Original files remain at {lease.Source}. Saved queue folder assignments retain their original paths.";
        }
        catch (Exception ex)
        {
            if (!saved)
            {
                string? recoveryWarning = null;
                foreach (var runner in preparedSources)
                    try { await runner.CancelKeepRunningPreparationAsync(); }
                    catch (Exception recovery)
                    {
                        recoveryWarning ??= " Service retention could not fully reset: " + SensitiveDataProtection.Redact(recovery.Message);
                    }
                // These runners were only constructed: never inspected, started, or
                // registered with the dashboard, so they own no processes or jobs.
                await Task.Run(() => { foreach (var service in replacements) service.Runner.Dispose(); });
                throw new InvalidOperationException($"Project copied to {destination}, but Launcher settings were not changed. Original files remain at {lease.Source}. {SensitiveDataProtection.Redact(ex.Message)}{recoveryWarning}", ex);
            }
            // Saved configuration is authoritative even when a later dashboard refresh
            // fails. Do not describe a successful save as rolled back.
            Notice = $"Project settings now use {destination}, but dashboard refresh could not finish. Reopen Launcher to reload the saved settings. {SensitiveDataProtection.Redact(ex.Message)}";
        }
    }

    private Task RequireGitConnectDestinationIdleAsync(ProjectProfile candidate) => Task.Run(async () =>
    {
        var inventory = await ProcessInspector.ReadAsync(fresh: true,
            includePorts: candidate.Services.Any(service => !service.IsConsole)).ConfigureAwait(false);
        if (inventory.InspectionError is not null)
            throw new InvalidOperationException("Destination processes could not be verified. Original project settings were retained.");
        foreach (var service in candidate.Services)
        {
            var folder = _store.ResolveWorkingDirectory(candidate, service);
            var previous = ServiceRuntimeStore.Load(service.Id, folder);
            if (previous is not null && (previous.Processes.Length > 0 || previous.ConsoleJobs.Length > 0))
                throw new InvalidOperationException($"{service.Name} has existing process recovery data for the destination. Choose a fresh clone folder; no process was stopped.");
            if (inventory.Processes.Any(process => ProcessInspector.BelongsToDirectory(process, folder)) ||
                !service.IsConsole && ServicePortConfiguration.GetPort(service) is { } port &&
                inventory.ListeningPorts.Any(listener => listener.Port == port && !ProcessInspector.IsDefinitelyStopped(listener.ProcessId)))
                throw new InvalidOperationException($"The destination for {service.Name} has active or unverified processes. Original project settings were retained.");
        }
    });

    private static bool SameGitConnectPath(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static string GitConnectRelativePath(string root, string path, string label)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} lies outside the project root. Keep all configured project paths inside the root before copying into another repository.");
        return relative;
    }

    private sealed class GitProjectConnectionLease(MainWindow owner, ProjectViewModel project,
        GitProjectConnectRequest request, string source, string profileSnapshot, ServiceViewModel[] services) : IDisposable
    {
        internal ProjectViewModel Project { get; } = project;
        internal GitProjectConnectRequest Request { get; } = request;
        internal string Source { get; } = source;
        internal string ProfileSnapshot { get; } = profileSnapshot;
        internal ServiceViewModel[] Services { get; } = services;

        public void Dispose()
        {
            owner.Dispatcher.VerifyAccess();
            if (!ReferenceEquals(owner._gitProjectConnectionLease, this)) return;
            owner._gitProjectConnectionLease = null;
            owner.SetSavingProjectEdits(false);
            if (owner._closeRequested || owner._closing || owner._closed) return;
            try { owner._queueDashboardPermit?.SetEnabled(owner.LongRunningTaskEnabled); }
            catch (Exception ex)
            {
                owner.Notice = $"Queue starts remain paused: {SensitiveDataProtection.Redact(ex.Message)}";
            }
        }
    }
}
