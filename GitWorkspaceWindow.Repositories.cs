using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private readonly IReadOnlyList<GitWorkspaceFolder> _configuredFolders;
    private readonly bool _scanProjectRootChildren;
    private readonly string? _preferredRepositoryRoot;
    private bool _repositoryDiscoveryComplete;
    private bool _discoveringRepositories;
    private bool _selectingRepository;
    private bool _applyingRepositorySelection;
    private string? _repositoryDiscoveryWarning;

    public IReadOnlyList<GitRepositoryChoice> RepositoryChoices { get; private set; } = [];
    public string GitLoadingText => _discoveringRepositories ? "Finding repositories…" : "Loading Git…";
    public Visibility RepositorySelectionVisibility => !IsGitLoading && !_gitLoadFailed && _selectingRepository
        ? Visibility.Visible : Visibility.Collapsed;
    public bool CanOpenSelectedRepository => IsIdle && _selectingRepository && RepositoryPicker?.SelectedItem is GitRepositoryChoice;
    public Visibility RepositoryDiscoveryWarningVisibility => !string.IsNullOrEmpty(_repositoryDiscoveryWarning)
        ? Visibility.Visible : Visibility.Collapsed;
    public string RepositoryDiscoveryWarning => _repositoryDiscoveryWarning ?? "";

    private async Task<bool> EnsureRepositorySelectedAsync()
    {
        if (_repositoryDiscoveryComplete) return !_selectingRepository;
        BeginGitLoad();
        _busy = true;
        _discoveringRepositories = true;
        _operation = new();
        ClearErrorDetails();
        Changed();
        try
        {
            var discovery = await GitRepositoryDiscovery.DiscoverAsync(_configuredFolders.Select((folder, index) =>
                new GitRepositoryDiscoveryFolder(folder.Label, folder.Directory, _scanProjectRootChildren && index == 0)).ToArray(),
                _operation.Token);
            _repositoryDiscoveryWarning = discovery.Warning;
            Folders = discovery.Folders.Select(folder => new GitWorkspaceFolder(folder.Label, folder.Directory)).ToArray();
            RepositoryChoices = discovery.Repositories;
            _selectingRepository = RepositoryChoices.Count > 1;
            _applyingRepositorySelection = true;
            try
            {
                Changed();
                FolderPicker.SelectedItem = _selectingRepository ? null
                    : RepositoryChoices.FirstOrDefault() is { } repository
                        ? Folders.FirstOrDefault(folder => string.Equals(folder.Directory, repository.Directory, StringComparison.OrdinalIgnoreCase))
                        : Folders.FirstOrDefault();
                RepositoryPicker.SelectedItem = RepositoryChoices.FirstOrDefault(repository =>
                    string.Equals(repository.Directory, _preferredRepositoryRoot, StringComparison.OrdinalIgnoreCase));
                _displayedFolder = SelectedFolder?.Directory;
            }
            finally { _applyingRepositorySelection = false; }
            _repositoryDiscoveryComplete = true;
            if (discovery.Warning != null) ReportError(discovery.Warning);
            SetStatus(_selectingRepository ? "" : "Repository folders checked.");
            return !_selectingRepository;
        }
        catch (Exception exception)
        {
            ReportError(SafeError(exception));
            FailGitLoad(exception is OperationCanceledException
                ? "Repository discovery was canceled. Try again."
                : "Repository folders could not be checked. Try again.");
            return false;
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            _discoveringRepositories = false;
            EndGitLoad();
            Changed();
            if (_selectingRepository && !_gitLoadFailed)
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_selectingRepository && IsIdle) RepositoryPicker.Focus();
                }));
        }
    }

    private void RepositorySelection_Changed(object sender, SelectionChangedEventArgs e) => Changed(nameof(CanOpenSelectedRepository));

    private async void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenSelectedRepository || RepositoryPicker.SelectedItem is not GitRepositoryChoice repository) return;
        _applyingRepositorySelection = true;
        try
        {
            FolderPicker.SelectedItem = Folders.FirstOrDefault(folder =>
                string.Equals(folder.Directory, repository.Directory, StringComparison.OrdinalIgnoreCase));
            _displayedFolder = SelectedFolder?.Directory;
            _selectingRepository = false;
        }
        finally { _applyingRepositorySelection = false; }
        Changed();
        await LoadGitWorkspaceAsync(verifyConnection: false);
    }
}
