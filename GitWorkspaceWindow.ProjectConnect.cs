using System.Windows;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private readonly Func<GitProjectConnectRequest, Task<IDisposable>>? _prepareProjectConnect;
    private readonly Func<GitProjectConnectResult, Task>? _applyProjectConnect;
    public GitProjectConnectResult? ConnectedProject { get; private set; }
    public Visibility ConnectProjectVisibility => GitContentAvailable && _snapshot is { IsRepository: false }
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CanConnectProject => IsIdle && CanInitialize;
    public string ExistingRepositoryChoiceLabel => IsRepository ? "Existing online repository" : "Clone into this empty folder…";
    public string ExistingRepositoryChoiceHint => IsRepository ? "Add a remote to this local repository." : "Download repository files and history.";

    private async void ConnectProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanConnectProject || SelectedFolder is not { } folder || !CanLeaveConnectionDraft()) return;
        _busy = true;
        Changed();
        GitProjectConnectWindow? dialog = null;
        try
        {
            dialog = new GitProjectConnectWindow(folder.Directory, _prepareProjectConnect, _applyProjectConnect)
                { Owner = DialogOwner };
            dialog.ShowDialog();
            if (dialog.Result is { } result)
            {
                _remoteDraftDirty = false;
                _preferredRemote = "origin";
                _cloneReview = null;
                _gitService.InvalidateRepository(result.RepositoryRoot);
                if (!string.Equals(result.SourceFolder, result.ProjectFolder, StringComparison.OrdinalIgnoreCase))
                    ConnectedProject = result;
            }
            if (dialog.RepositoryOperationAttempted)
                _repositoryDiscoveryComplete = false;
        }
        catch (Exception exception) { ReportError(SafeError(exception)); }
        finally
        {
            _busy = false;
            Changed();
        }
        if (ConnectedProject != null) CloseActiveGitDialog();
        else if (dialog?.RepositoryOperationAttempted == true)
        {
            await LoadGitWorkspaceAsync(verifyConnection: false, forceRefresh: true);
            SetStatus(dialog.Status);
            if (dialog.Result == null) ReportError(dialog.Status);
        }
    }
}
