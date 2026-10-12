using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly ProjectGitService _projectGitService = ProjectGitService.Shared;
    private readonly CancellationTokenSource _gitWarmupLifetime = new();
    private Task? _gitWarmupTask;
    private bool _projectGitForceRefreshing;
    private bool _projectGitNotificationPending;

    public bool CanForceProjectGitRefresh => !_projectGitForceRefreshing && SelectedProject is not null
        && !IsEditing && !_closeRequested && !_closing && !_closed;

    private GitRepositoryDiscoveryFolder[] ProjectGitFolders(FullStackLauncher.ViewModels.ProjectViewModel project) =>
        BranchFolders(project).Select(folder => new GitRepositoryDiscoveryFolder(folder.Label, folder.Directory, folder.IsRoot)).ToArray();

    private ProjectGitRegistration[] SynchronizeProjectGitRegistrations()
    {
        var registrations = new List<ProjectGitRegistration>();
        foreach (var project in ProjectItems)
        {
            try { registrations.Add(new(project.Profile.Id, ProjectGitFolders(project))); }
            catch (Exception) { /* Invalid configured folders remain available for correction. */ }
        }
        var captured = registrations.ToArray();
        _projectGitService.SynchronizeProjects(captured);
        return captured;
    }

    private void InitializeProjectGitService()
    {
        _projectGitService.Changed += ProjectGitState_Changed;
        Closed += (_, _) => _projectGitService.Changed -= ProjectGitState_Changed;
    }

    private void ProjectGitState_Changed(object? sender, ProjectGitChangedEventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_closeRequested || _closing || _closed) return;
            foreach (var watch in _dashboardGitWatches.Values.Where(watch =>
                (e.ProjectId is null || watch.Project.Id == e.ProjectId)
                && (e.RepositoryRoot is null || string.Equals(watch.Scope.RepositoryRoot, e.RepositoryRoot, StringComparison.OrdinalIgnoreCase))))
                watch.NextLocalRead = DateTimeOffset.MinValue;
            if (_projectGitNotificationPending) return;
            _projectGitNotificationPending = true;
            _ = PresentSharedProjectGitAsync();
        });
    }

    private async Task PresentSharedProjectGitAsync()
    {
        try { await Task.WhenAll(RefreshProjectBranchesAsync(), RefreshNextCommitAsync(), PollDashboardGitAsync()); }
        finally { _projectGitNotificationPending = false; }
    }

    private async void ForceProjectGitRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CanForceProjectGitRefresh || SelectedProjectItem is not { } project) return;
        _projectGitForceRefreshing = true;
        Changed(nameof(CanForceProjectGitRefresh));
        Notice = $"Refreshing Git for {project.Name}…";
        try
        {
            var state = await _projectGitService.RefreshProjectAsync(project.Profile.Id, ProjectGitFolders(project),
                fetchRemote: true, token: _dashboardGitLifetime.Token);
            await PresentSharedProjectGitAsync();
            if (ReferenceEquals(SelectedProjectItem, project))
                Notice = state.Error ?? state.Repositories.Values.Select(repository => repository.Error ?? repository.FetchError
                    ?? repository.ComparisonError ?? repository.PowerError ?? repository.LedgerError).FirstOrDefault(error => error is not null)
                    ?? $"Git refreshed for {project.Name}. Background refresh runs every 10 minutes.";
        }
        catch (OperationCanceledException) when (_closeRequested || _closing || _closed || _dashboardGitLifetime.IsCancellationRequested) { }
        catch (Exception ex) { Notice = $"Git refresh could not finish: {SensitiveDataProtection.Redact(ex.Message)}"; }
        finally { _projectGitForceRefreshing = false; Changed(nameof(CanForceProjectGitRefresh)); }
    }

    private void StartGitConnectionWarmup()
    {
        if (_gitWarmupTask is not null || _closeRequested || _closing || _closed) return;
        var registrations = SynchronizeProjectGitRegistrations()
            .OrderByDescending(project => project.ProjectId == _settings.SelectedProjectId).ToArray();
        _gitWarmupTask = ObserveGitConnectionWarmupAsync(registrations);
        _projectGitService.StartBackgroundRefresh();
    }

    private async Task ObserveGitConnectionWarmupAsync(ProjectGitRegistration[] projects)
    {
        try
        {
            await Parallel.ForEachAsync(projects,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = _gitWarmupLifetime.Token }, async (project, token) =>
                {
                    var state = await _projectGitService.GetProjectAsync(project.ProjectId, project.Folders, token).ConfigureAwait(false);
                    foreach (var repository in state.Repositories.Values)
                        foreach (var remote in repository.Snapshot?.Remotes.Where(remote => remote.UrlCanCopy) ?? [])
                        {
                            if ((remote.Provider ?? GitConnectionService.InferProvider(remote.FetchUrl)) is not { } provider) continue;
                            try { await GitConnectionWarmup.GetOrCheckAsync(repository.Snapshot!.RepositoryRoot!, provider, remote.FetchUrl, token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception) { /* Explicit Git setup retains connection failure feedback. */ }
                        }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_gitWarmupLifetime.IsCancellationRequested) { }
        catch (Exception) { /* Background failures are handled by the explicit Git setup flow. */ }
    }

    private async Task StopGitConnectionWarmupAsync()
    {
        _gitWarmupLifetime.Cancel();
        // The command runner cancels and cleans up its own processes. Do not block dashboard closure
        // indefinitely on a disconnected filesystem or an inaccessible child process.
        try
        {
            await Task.WhenAll(_gitWarmupTask ?? Task.CompletedTask, GitConnectionWarmup.CancelAndDrainPendingAsync(), _projectGitService.StopAsync())
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }

    private async Task OpenGitWorkspaceAsync(string? preferredRepositoryRoot)
    {
        if (SelectedProjectItem is not { } project || IsEditing || _closeRequested || _closing) return;
        try
        {
            var folders = BranchFolders(project)
                .GroupBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => new GitWorkspaceFolder(string.Join(" / ", group.Select(folder => folder.Label)), group.Key))
                .ToArray();
            var workspace = new GitWorkspaceWindow(project.Name, folders,
                preferredRepositoryRoot: preferredRepositoryRoot, projectId: project.Profile.Id,
                gitService: _projectGitService) { Owner = this };
            workspace.ShowDialog();
            await RefreshNextCommitAsync();
            if (ReferenceEquals(SelectedProjectItem, project) && workspace.SelectedRepositoryRoot is { } root)
                SelectedNextCommitRepository = NextCommitRepositories.FirstOrDefault(repository =>
                    string.Equals(repository.RepositoryRoot, root, StringComparison.OrdinalIgnoreCase))
                    ?? SelectedNextCommitRepository;
        }
        catch (Exception)
        {
            Notice = "The Git workspace could not open. Check the configured project folders and try again.";
        }
    }
}
