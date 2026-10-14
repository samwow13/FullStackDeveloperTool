using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private GitSetupWindow? _setupWindow;
    private bool _openingSetupWindow;
    private bool _openWorkspaceRequested;
    private Window DialogOwner => _setupWindow ?? (IsVisible ? this : Owner ?? this);

    // Cached local state chooses the first window. A cold read uses the compact
    // loading host, keeping cancellation and close guards visible while it runs.
    public async Task ShowGitDialogAsync(Window owner, bool openConnections = false)
    {
        Owner = owner;
        _configOpen = openConnections;
        try
        {
            var initialLoad = InitializeGitDialogAsync();
            var showedLoadingHost = !initialLoad.IsCompleted;
            if (showedLoadingHost) ShowSetupDialog(initialLoad);
            await initialLoad;
            if (!owner.IsVisible || _closed) return;
            if (showedLoadingHost)
            {
                if (!_openWorkspaceRequested || ConnectedProject != null) return;
            }
            else if (_setupOpen || _selectingRepository || _gitLoadFailed)
            {
                ShowSetupDialog();
                if (!_openWorkspaceRequested || ConnectedProject != null) return;
            }
            if (!_closed && owner.IsVisible) ShowDialog();
        }
        finally
        {
            if (!_closed) Close();
        }
    }

    private async Task InitializeGitDialogAsync()
    {
        if (_ready) return;
        _ready = true;
        _gitService.Changed += SharedGit_Changed;
        await LoadGitWorkspaceAsync(verifyConnection: false);
    }

    private void ShowSetupDialog(Task? initialLoad = null)
    {
        if (_setupWindow != null || _openingSetupWindow || _closed) return;
        _openingSetupWindow = true;
        var parent = (Panel)GitDialogPanels.Parent;
        var index = parent.Children.IndexOf(GitDialogPanels);
        parent.Children.Remove(GitDialogPanels);
        try
        {
            _openWorkspaceRequested = false;
            _setupWindow = new GitSetupWindow(this, GitDialogPanels)
                { Owner = IsVisible ? this : Owner, Title = IsRepository ? "Git config" : "Git setup" };
            if (initialLoad != null)
            {
                var loadingWindow = _setupWindow;
                loadingWindow.Loaded += async (_, _) =>
                {
                    try
                    {
                        await initialLoad;
                        if (loadingWindow.IsVisible && !_setupOpen && IsRepository && !_gitLoadFailed)
                            ShowLocalWorkspace();
                    }
                    catch (Exception exception)
                    {
                        ReportError(SafeError(exception));
                        FailGitLoad("Git could not finish loading. Try again.");
                    }
                };
            }
            Changed();
            _setupWindow.ShowDialog();
        }
        finally
        {
            _setupWindow?.DetachContent();
            _setupWindow = null;
            parent.Children.Insert(index, GitDialogPanels);
            _openingSetupWindow = false;
            if (IsVisible && IsRepository && !_gitLoadFailed)
            {
                _setupOpen = false;
                _configOpen = false;
            }
            Changed();
            ScheduleMissingReleaseBranchPrompt();
            // Config can switch to a folder without a usable repository. Closing
            // that setup ends this workspace instead of rendering setup inside it.
            if (IsVisible && (!IsRepository || _gitLoadFailed)) Close();
        }
    }

    private void ScheduleSetupDialog()
    {
        if (!IsVisible || _setupWindow != null || _openingSetupWindow || _closed) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible && _setupOpen && GitContentAvailable && _setupWindow == null && !_closed)
                ShowSetupDialog();
        }));
    }

    internal void SetupWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Returning to the repository keeps this workflow and its drafts alive.
        if (IsIdle && (_openWorkspaceRequested || IsVisible && IsRepository && !_gitLoadFailed)) return;
        Window_Closing(sender, e);
        if (!e.Cancel)
        {
            _remoteDraftDirty = false;
            _drafts.Clear();
        }
    }

    private void CloseActiveGitDialog() => DialogOwner.Close();
    internal void SetSetupViewport(double height) => SetupScroll.MaxHeight = height;
    internal void RefreshSetup() => Refresh_Click(this, new RoutedEventArgs());
    internal void CancelSetupOperation() => Cancel_Click(this, new RoutedEventArgs());
    internal void TrustSetupFolder() => TrustFolder_Click(this, new RoutedEventArgs());
}
