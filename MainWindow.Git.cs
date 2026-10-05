using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CancellationTokenSource _gitWarmupLifetime = new();
    private Task? _gitWarmupTask;

    private void StartGitConnectionWarmup()
    {
        if (_gitWarmupTask is not null || _closeRequested || _closing || _closed) return;
        // Capture settings-owned folders on the dispatcher; all Git/network work runs in the background.
        var folders = new List<string>();
        foreach (var project in ProjectItems.OrderByDescending(project => project.Profile.Id == _settings.SelectedProjectId))
        {
            try { folders.AddRange(BranchFolders(project).Select(folder => folder.Directory)); }
            catch (Exception) { /* Invalid saved folders remain available for explicit correction. */ }
        }
        _gitWarmupTask = ObserveGitConnectionWarmupAsync(folders.ToArray());
    }

    private async Task ObserveGitConnectionWarmupAsync(string[] folders)
    {
        try { await GitConnectionWarmup.WarmConfiguredFoldersAsync(folders, _gitWarmupLifetime.Token).ConfigureAwait(false); }
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
            await Task.WhenAll(_gitWarmupTask ?? Task.CompletedTask, GitConnectionWarmup.CancelAndDrainPendingAsync())
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
                preferredRepositoryRoot: preferredRepositoryRoot) { Owner = this };
            workspace.ShowDialog();
            await RefreshProjectBranchesAsync();
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
