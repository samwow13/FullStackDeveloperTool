using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public sealed record GitWorkspaceFolder(string Label, string Directory)
{
    public string Display => $"{Label} · {Directory}";
}

public partial class GitWorkspaceWindow : Window, INotifyPropertyChanged
{
    private GitRepositorySnapshot? _snapshot;
    private CancellationTokenSource? _operation;
    private bool _ready;
    private bool _busy;
    private bool _applyingSnapshot;
    private bool _remoteDraftDirty;
    private string? _displayedFolder;
    private string? _preferredRemote;
    private readonly Dictionary<string, FormDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private string _status = "Choose a folder to read its Git status.";
    private string _latestErrorDetails = "";
    private string _commitDraft = "";
    private GitRemoteComparison? _comparison;
    private string? _comparisonError;

    public GitWorkspaceWindow(string projectName, IReadOnlyList<GitWorkspaceFolder> folders,
        bool scanChildRepositories = true, string? preferredRepositoryRoot = null)
    {
        ProjectTitle = $"Git · {projectName}";
        _configuredFolders = folders.ToArray();
        _scanProjectRootChildren = scanChildRepositories;
        _preferredRepositoryRoot = preferredRepositoryRoot;
        Folders = _configuredFolders;
        InitializeComponent();
        // Keep the footer reachable on smaller displays; the workspace pages scroll.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        DataContext = this;
        Activated += (_, _) => ScheduleMissingReleaseBranchPrompt();
    }

    public string ProjectTitle { get; }
    public IReadOnlyList<GitWorkspaceFolder> Folders { get; private set; }
    public string? SelectedRepositoryRoot => Root ?? RepositoryChoices.FirstOrDefault(repository =>
        string.Equals(repository.Directory, SelectedFolder?.Directory, StringComparison.OrdinalIgnoreCase))?.Directory;
    public IReadOnlyList<GitChange> Changes => _snapshot?.Changes ?? [];
    public IReadOnlyList<GitBranchInfo> Branches => _snapshot?.Branches ?? [];
    public IReadOnlyList<GitRemoteInfo> Remotes => _snapshot?.Remotes ?? [];
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy && _unconfirmedGitCommand == null;
    public bool IsRepository => _snapshot?.IsRepository == true;
    public bool CanInitialize => _snapshot is { IsRepository: false };
    public bool HasRemote => IsRepository && SelectedRemote != null;
    public bool CanSync => HasRemote && _powerReadError == null && !HasSyncRecovery && _snapshot is { IsDetached: false, IsUnborn: false, OperationState: null };
    public bool CanChangeBranch => _powerReadError == null && !HasSyncRecovery && _snapshot is { IsRepository: true, IsUnborn: false, OperationState: null } && Changes.Count == 0;
    public bool CanSwitchBranch => _powerReadError == null && !HasSyncRecovery && _snapshot is { IsRepository: true, OperationState: null } && Changes.Count == 0;
    public bool CanCommitAllPush => CanFinishSync || (IsIdle && HasRemote && _powerDetails != null && !HasSyncRecovery
        && _snapshot is { IsRepository: true, IsDetached: false, OperationState: null }
        && (!_snapshot.IsUnborn || Changes.Count > 0) && !Changes.Any(change => change.IsConflict));
    public bool CanPull => CanSync && Changes.Count == 0;
    public string StatusText => _status;
    public string LatestErrorDetails => _latestErrorDetails;
    public Visibility LatestErrorVisibility => string.IsNullOrEmpty(_latestErrorDetails) ? Visibility.Collapsed : Visibility.Visible;
    private string? Root => _snapshot?.RepositoryRoot;
    private GitRemoteInfo? SelectedRemote => RemotePicker?.SelectedItem as GitRemoteInfo;
    private GitWorkspaceFolder? SelectedFolder => FolderPicker?.SelectedItem as GitWorkspaceFolder;

    public string RepositoryLabel => _snapshot == null ? "Repository unavailable · refresh to retry."
        : IsRepository ? Root! : $"No Git repository · {SelectedFolder?.Directory}";
    public string BranchLabel => _snapshot is null ? "Branch unavailable"
        : !IsRepository ? "No repository" : _snapshot.IsDetached
        ? $"Detached HEAD · {_snapshot.Branch}" : $"{_snapshot.Branch}{(_snapshot.IsUnborn ? " · no commits yet" : "")}";
    public string OperationLabel => _snapshot?.OperationState is { } operation
        ? _powerDetails?.ReadyToFinish == true ? "Merge ready locally. Use Commit all & push when you are ready."
            : _powerDetails?.PendingSync != null && operation == "Merge in progress"
                ? "Merge in progress. Resolve and stage any conflicts in Git or your editor, then refresh. Follow the recovery details below."
                : $"{operation}. Finish or abort it in Git or your editor, then refresh." : "";
    public string RemoteSummary => SelectedRemote is { } remote
        ? remote.FetchUrl == remote.PushUrl ? remote.FetchUrl : $"Fetch: {remote.FetchUrl}\nPush: {remote.PushUrl}"
        : "Choose Config to connect a repository.";
    public string CommitAvailabilityText => !IsRepository ? "Initialize or select a repository to begin."
        : _powerReadError != null ? _powerReadError
        : _powerDetails?.ReadyToFinish == true ? "Local merge is ready. Review the destination branch, commit all changes and push."
        : HasSyncRecovery ? "Review the local merge below. Resolve and stage any conflicts, then refresh before committing and pushing."
        : _snapshot!.OperationState != null ? "Finish the active Git operation, then refresh."
        : Changes.Any(change => change.IsConflict) ? "Resolve conflicts in your editor, then refresh."
        : _snapshot.IsDetached ? "Switch to a local branch before committing."
        : !HasRemote ? "Choose Config to connect a repository."
        : Changes.Count == 0 ? "Review the destination branch and push existing commits. No empty commit will be created."
        : $"Review the destination branch, then commit all {Changes.Count:N0} changed files with an automatic message and push.";
    private string ProposedBranchName => PublishBranchDraft.Trim();
    private GitBranchInfo? ProposedLocalBranch => Branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == ProposedBranchName);
    private GitBranchInfo? ProposedOnlineBranch => Branches.FirstOrDefault(branch => branch.IsRemote
        && (branch.Name == $"{SelectedRemote?.Name}/{ProposedBranchName}" || branch.Name == ProposedBranchName));
    public bool CanCreateNamedBranch => IsIdle && IsEditingPublishBranch && CanChangeBranch && ProposedBranchName.Length > 0
        && ProposedLocalBranch == null && ProposedOnlineBranch == null;
    public bool CanSwitchNamedBranch => IsIdle && IsEditingPublishBranch && CanSwitchBranch
        && (ProposedLocalBranch is { IsCurrent: false } || (ProposedLocalBranch == null && ProposedOnlineBranch != null));
    public string BranchActionText => !IsRepository ? "Select a repository first."
        : _powerReadError != null ? "Refresh to read the Git workflow state before changing branches."
        : HasSyncRecovery ? "Commit or review the pending local merge before changing branches."
        : _snapshot!.OperationState != null ? "Finish the active Git operation first."
        : Changes.Count > 0 ? "Commit or stash local changes before switching or merging."
        : ProposedBranchName.Length == 0 ? "Enter a branch name."
        : ProposedLocalBranch is { IsCurrent: true } ? "Current branch."
        : ProposedOnlineBranch != null && ProposedLocalBranch == null ? "Switch creates a local tracking branch from the last fetched version."
        : _snapshot.IsUnborn ? "Fetch an existing branch or create your first commit."
        : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void SetStatus(string text)
    {
        _status = SensitiveDataProtection.Redact(text);
        Changed(nameof(StatusText));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await LoadGitWorkspaceAsync(verifyConnection: false);
    }

    private async void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !IsIdle || _applyingRepositorySelection || _selectingRepository) return;
        EndPublishBranchEdit();
        var keepConfigOpen = _configOpen;
        ResetPublishFeedback();
        if (_displayedFolder != null) _drafts[_displayedFolder] = CaptureDraft();
        _displayedFolder = SelectedFolder?.Directory;
        _snapshot = null;
        _preferredRemote = null;
        ResetBranchInspection();
        _comparison = null;
        _powerDetails = null;
        _powerReadError = null;
        RestoreDraft(_displayedFolder != null ? _drafts.GetValueOrDefault(_displayedFolder) : null);
        if (keepConfigOpen) _setupOpen = true;
        Changed();
        await LoadGitWorkspaceAsync(verifyConnection: false);
        if (keepConfigOpen && !_gitLoadFailed && !_setupOpen)
        {
            _configOpen = true;
            OpenSetup(IsRepository && Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice);
        }
    }

    private void ApplySnapshot(GitRepositorySnapshot snapshot)
    {
        var selectedRemoteName = _preferredRemote ?? SelectedRemote?.Name;
        _applyingSnapshot = true;
        try
        {
            _snapshot = snapshot;
            _comparison = null;
            _comparisonError = null;
            Changed();
            RemotePicker.SelectedItem = Remotes.FirstOrDefault(remote => remote.Name == selectedRemoteName);
            _preferredRemote = null;
        }
        finally { _applyingSnapshot = false; }
        SynchronizePublishBranchDraft();
        if (!_remoteDraftDirty) UpdateRemoteSelection();
        UpdateBranchView();
        Changed();
    }

    private async Task RefreshAsync(bool fetchRemote = false, bool showLoading = true, CancellationToken cancellationToken = default)
    {
        if (!IsIdle) return;
        if (SelectedFolder is not { } folder)
        {
            FailGitLoad("No project folder is configured. Close this window and choose a project folder first.");
            return;
        }
        if (showLoading) BeginGitLoad();
        else _gitLoadFailed = false;
        EndPublishBranchEdit();
        _busy = true;
        Changed();
        if (!await DrainBranchInspectionAsync())
        {
            _busy = false;
            if (showLoading) EndGitLoad();
            Changed();
            return;
        }
        ClearErrorDetails();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _comparison = null;
        Changed();
        SetStatus("Reading local repository status…");
        try
        {
            var snapshot = await Task.Run(() => GitRepositoryService.ReadAsync(folder.Directory, _operation.Token));
            _trustRoot = null;
            _localSetupError = null;
            if (_preferredRemote == null)
                await RestoreAgentConnectionSelectionAsync(snapshot, _operation.Token);
            ApplySnapshot(snapshot);
            if (fetchRemote && snapshot.IsRepository && SelectedRemote is { } remote)
            {
                await FetchForGitLoadAsync(snapshot, remote, _operation.Token);
                // Fetch can update/prune refs, including before an ordinary failure. Read them
                // again before displaying branches or comparing against remote history.
                snapshot = await GitRepositoryService.ReadAsync(folder.Directory, _operation.Token);
                await RestoreAgentConnectionSelectionAsync(snapshot, _operation.Token);
                ApplySnapshot(snapshot);
            }
            if (!snapshot.IsRepository && !_remoteDraftDirty && _setupStep != SetupStep.Local) OpenSetup(SetupStep.Choice);
            await RefreshComparisonAsync(_operation.Token);
            await RefreshPowerDetailsAsync(_operation.Token);
            CheckGitReadFailures();
            if (snapshot.IsRepository && SelectedRemote == null && !_setupOpen && !HasSyncRecovery)
                OpenSetup(Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice);
            SetStatus($"Refreshed {DateTime.Now:T} · " + (_comparison is { IsLocalComparison: true }
                ? "line totals use the last local commit." : "remote comparison uses last fetched history."));
        }
        catch (Exception ex)
        {
            if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
            _snapshot = null;
            _powerDetails = null;
            _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
            _localSetupError = ex is GitRepositoryTrustException
                ? "Git blocked this repository because another Windows account owns it. Review the folder below before trusting it."
                : "Git could not read this folder. Check that the folder exists and Git for Windows is installed, then retry. Details are below.";
            FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again."
                : ex is OperationCanceledException ? "Git loading was canceled or timed out. Try again."
                : _trustRoot != null ? "This repository needs your trust before Git can load it." : "Git could not load this folder. Check the folder and Git installation, then try again.");
            var detail = SafeError(ex);
            if (fetchRemote && ex is OperationCanceledException)
                detail += " Remote refs may be partly refreshed. Retry loading or use Fetch to confirm them.";
            ReportError(detail);
            SetStatus(ex is OperationCanceledException
                ? "Refresh canceled or timed out. Status is unavailable until the next refresh."
                    + (fetchRemote ? " Remote refs may be partly refreshed. Retry loading or use Fetch to confirm them." : "")
                : detail);
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            if (showLoading) EndGitLoad();
            Changed();
        }
    }

    // Every action captures its folder and inputs before entering the worker. Changing
    // selection and closing are disabled until the outcome and fresh status are known.
    private async Task<bool> ExecuteAsync(string title, Func<CancellationToken, Task<string>> action,
        bool refresh = true, Action<string>? completed = null, string? successMessage = null, bool useResultAsStatus = false,
        GitRemoteInfo? pushedRemote = null, string? cancellationContext = null)
    {
        if (!IsIdle || SelectedFolder is not { } folder) return false;
        var expected = _snapshot;
        EndPublishBranchEdit();
        _busy = true;
        Changed();
        if (!await DrainBranchInspectionAsync())
        {
            _busy = false;
            Changed();
            return false;
        }
        ClearErrorDetails();
        _operation = new();
        Changed();
        SetStatus($"{title}…");
        var success = false;
        var outcome = "";
        try
        {
            var result = await Task.Run(() => expected is { IsRepository: true }
                ? GitRepositoryService.RunWithSnapshotAsync(expected, action, _operation.Token)
                : action(_operation.Token));
            success = true;
            outcome = useResultAsStatus && !string.IsNullOrWhiteSpace(result) ? result : successMessage ?? $"{title} completed.";
            completed?.Invoke(result);
            if (pushedRemote != null)
            {
                CompletePublishFeedback(true, outcome);
                await OpenPostPushLinkAsync(pushedRemote);
            }
        }
        catch (Exception ex)
        {
            if (ex is GitCommandExitUnconfirmedException unconfirmed)
            {
                _unconfirmedGitCommand = unconfirmed;
                FailGitLoad("Git may still be running. Wait, then Retry to check again.");
            }
            if (ex is GitCommitPushException { CommitCreated: true }) { _commitDraft = ""; _syncMessageDraft = ""; }
            outcome = ex is OperationCanceledException
                ? SafeError(ex) + (cancellationContext ?? " Changes may already have taken effect; review local and remote status before retrying.")
                : SafeError(ex);
            ReportError(outcome);
        }
        finally
        {
            if (refresh && _unconfirmedGitCommand == null)
            {
                BeginGitLoad();
                SetStatus($"{outcome} Refreshing local status…");
                _operation.Dispose();
                _operation = new();
                try
                {
                    var refreshed = await Task.Run(() => GitRepositoryService.ReadAsync(folder.Directory, _operation.Token));
                    if (_preferredRemote == null)
                        await RestoreAgentConnectionSelectionAsync(refreshed, _operation.Token);
                    ApplySnapshot(refreshed);
                    _trustRoot = null;
                    _localSetupError = null;
                    await RefreshComparisonAsync(_operation.Token);
                    await RefreshPowerDetailsAsync(_operation.Token);
                    CheckGitReadFailures();
                }
                catch (Exception ex)
                {
                    if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
                    _snapshot = null;
                    _powerDetails = null;
                    _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
                    _localSetupError = "The operation ended, but local status could not be read. Retry before continuing.";
                    FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again."
                        : "The operation ended, but Git status could not reload. Retry loading before continuing.");
                    _comparison = null;
                    outcome += " Local refresh failed; refresh before another repository action.";
                    ReportError(SafeError(ex));
                }
                finally { EndGitLoad(); }
                if (_gitLoadFailed && !string.IsNullOrWhiteSpace(outcome))
                {
                    ReportError(outcome);
                    if (_unconfirmedGitCommand == null)
                        FailGitLoad((success ? $"{title} completed. " : $"Review the {title} outcome. ") + "Git status could not reload. Retry loading before continuing.");
                }
            }
            _operation.Dispose();
            _operation = null;
            _busy = false;
            SetStatus(outcome);
            Changed();
        }
        return success;
    }

    private static string SafeError(Exception ex) => SensitiveDataProtection.Redact(ex.Message);

    private void ReportError(string detail)
    {
        var safe = SensitiveDataProtection.Redact(detail);
        if (string.IsNullOrWhiteSpace(safe)) return;
        _latestErrorDetails = string.IsNullOrEmpty(_latestErrorDetails) ? safe : _latestErrorDetails + "\n\n" + safe;
        Changed(nameof(LatestErrorDetails));
        Changed(nameof(LatestErrorVisibility));
        Changed(nameof(GitErrorVisibility));
    }

    private void ClearErrorDetails()
    {
        _latestErrorDetails = "";
        Changed(nameof(LatestErrorDetails));
        Changed(nameof(LatestErrorVisibility));
        Changed(nameof(GitErrorVisibility));
    }

    private async void Remote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSnapshot || !IsIdle) return;
        ResetBranchInspection();
        UpdateBranchView();
        var keepConfigOpen = _configOpen;
        SynchronizePublishBranchDraft();
        UpdateRemoteSelection();
        _comparison = null;
        _comparisonError = null;
        _powerDetails = null;
        _powerReadError = IsRepository ? "Refresh to read workflow settings for this connection." : null;
        Changed();
        if (_ready && !_busy)
        {
            if (Root is { } root && SelectedRemote is { } remote && !await SaveAgentConnectionSelectionAsync(root, remote))
            {
                _powerReadError = "The active Git connection could not be saved. Refresh or select it again before committing or merging.";
                Changed();
                return;
            }
            await LoadGitWorkspaceAsync();
            if (keepConfigOpen && !_gitLoadFailed && !_setupOpen)
            {
                _configOpen = true;
                OpenSetup(IsRepository && Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice);
            }
        }
    }

    private Task<bool> SaveAgentConnectionSelectionAsync(string root, GitRemoteInfo remote) =>
        ExecuteAsync("Select active Git connection", async token =>
        {
            await AgentGitChangeStore.SelectConnectionAsync(root, remote.Name, AgentGitChangeStore.ConnectionId(remote), token);
            return $"{remote.Name} is now the active connection for agent change summaries in this checkout.";
        }, refresh: false, useResultAsStatus: true);

    private async Task RestoreAgentConnectionSelectionAsync(GitRepositorySnapshot snapshot, CancellationToken token)
    {
        if (snapshot.RepositoryRoot is not { } root) return;
        _preferredRemote = "";
        try
        {
            var selection = await AgentGitChangeStore.ReadSelectionAsync(root, token);
            var eligible = snapshot.Remotes.Where(remote => remote.UrlCanCopy).ToArray();
            var selected = selection != null
                ? eligible.SingleOrDefault(remote => remote.Name == selection.RemoteName && AgentGitChangeStore.ConnectionId(remote) == selection.ConnectionId)
                : eligible.SingleOrDefault(remote => snapshot.Upstream?.StartsWith(remote.Name + "/", StringComparison.Ordinal) == true)
                    ?? (snapshot.Upstream == null && snapshot.Remotes.Count == 1 && eligible.Length == 1 ? eligible[0] : null);
            _preferredRemote = selected?.Name ?? "";
            if (selected == null && snapshot.Remotes.Count > 0)
                ReportError(selection != null
                    ? "The saved connection changed or was removed. Choose Config, select a connection, then choose Use selected connection."
                    : "The current branch does not identify one active connection. Choose Config, select a connection, then choose Use selected connection.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError(SafeError(exception));
        }
    }

    private void UpdateRemoteSelection()
    {
        if (_applyingSnapshot || RemoteName == null || _remoteDraftDirty) return;
        if (SelectedRemote is { } remote)
        {
            _applyingSnapshot = true;
            RemoteName.Text = remote.Name;
            RemoteUrl.Text = remote.UrlCanCopy ? remote.FetchUrl : "";
            _applyingSnapshot = false;
            _remoteDraftDirty = false;
        }
        Changed();
    }

    private void RemoteDraft_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _applyingSnapshot) return;
        _remoteDraftDirty = true;
        _cloneReview = null;
        InvalidateConnectionCheck();
        Changed();
    }

    private FormDraft CaptureDraft() => new(_commitDraft, RemoteName.Text, RemoteUrl.Text,
        _remoteDraftDirty, InitialBranch.Text, _setupProvider, _setupStep, _setupOpen, _setupRoute, _publishBranchDraft,
        _syncMessageDraft, _commitDraftScope, _publishBranchDraftScope);

    private void RestoreDraft(FormDraft? draft)
    {
        _applyingSnapshot = true;
        try
        {
            _commitDraft = draft?.Commit ?? "";
            RestorePublishBranchDraft(draft?.PublishBranch ?? "", draft?.PublishScope);
            _syncMessageDraft = draft?.SyncMessage ?? "";
            _commitDraftScope = draft?.CommitScope;
            RemoteName.Text = draft?.Remote ?? "origin";
            RemoteUrl.Text = draft?.Url ?? "";
            InitialBranch.Text = draft?.InitialBranch ?? "main";
            _setupProvider = draft?.Provider;
            _setupRoute = draft?.Route ?? SetupRoute.Existing;
            _cloneReview = null;
            _setupStep = draft?.Step ?? SetupStep.Choice;
            if (_setupStep == SetupStep.CloneReview) _setupStep = SetupStep.Destination;
            if (_setupStep is SetupStep.Review or SetupStep.Complete) _setupStep = SetupStep.Authentication;
            _setupOpen = draft?.SetupOpen ?? true;
            _trustRoot = null;
            _localSetupError = null;
            InvalidateConnectionCheck();
            _remoteDraftDirty = draft?.RemoteDirty ?? false;
            // Connection form drafts belong to the configured folder. The active connection
            // belongs to the resolved checkout and is restored independently from its ledger.
            _preferredRemote = null;
        }
        finally { _applyingSnapshot = false; }
    }

    private sealed record FormDraft(string Commit, string Remote, string Url, bool RemoteDirty,
        string InitialBranch, GitHostingProvider? Provider, SetupStep Step, bool SetupOpen, SetupRoute Route, string PublishBranch,
        string SyncMessage, CommitDraftScope? CommitScope, PublishBranchScope? PublishScope);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadGitWorkspaceAsync(verifyConnection: false);
    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (Root is not { } root || SelectedRemote is not { } remote) return;
        await FetchRemoteAsync(root, remote);
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPull || Root is not { } root || SelectedRemote is not { } remote) return;
        var branch = _snapshot!.Branch;
        await ExecuteAsync($"Pull {remote.Name}/{branch}", token => GitRepositoryService.PullAsync(root, remote.Name, branch, token),
            successMessage: "Pull complete.");
    }

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSync || Root is not { } root || SelectedRemote is not { } remote) return;
        var branch = _snapshot!.Branch;
        if (!Confirm("Push commits", $"Repository: {root}\nBranch: {branch}\nDestination: {remote.Name}/{branch}\n{remote.PushUrl}\n\nPush all existing commits on this branch?")) return;
        BeginPublishFeedback(branch, remote.Name);
        SetPublishPhase("Pushing commits");
        var pushed = await ExecuteAsync($"Push {remote.Name}/{branch}", token => GitRepositoryService.PushAsync(root, remote.Name, branch, token),
            useResultAsStatus: true, pushedRemote: remote);
        CompletePublishFeedback(pushed, StatusText);
    }

    private async void CommitAllPush_Click(object sender, RoutedEventArgs e) => await ReviewCommitAllPushAsync();

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCreateNamedBranch || Root is not { } root) return;
        var name = ProposedBranchName;
        await ExecuteAsync("Create and switch branch", token => GitRepositoryService.CreateBranchAsync(root, name, token));
    }

    private async void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSwitchNamedBranch || Root is not { } root) return;
        var name = ProposedBranchName;
        if (!Branches.Any(branch => !branch.IsRemote && branch.Name == name) && SelectedRemote is { } remote
            && Branches.Any(branch => branch.IsRemote && branch.Name == $"{remote.Name}/{name}")) name = $"{remote.Name}/{name}";
        await ExecuteAsync("Switch branch", token => GitRepositoryService.SwitchBranchAsync(root, name, token));
    }

    private bool Confirm(string title, string text) => IsIdle && MessageBox.Show(this,
        SensitiveDataProtection.Redact(text), title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        SetStatus(_discoveringRepositories ? "Repository discovery canceled. Waiting for folder checks to stop…"
            : "Cancel requested. Waiting for the command to finish and report its outcome…");
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsIdle && _branchInspectionTasks.Count > 0)
        {
            e.Cancel = true;
            _busy = true;
            Changed();
            var stopped = await DrainBranchInspectionAsync();
            _busy = false;
            Changed();
            if (stopped) Close();
            return;
        }
        if (IsIdle)
        {
            if (HasSetupDrafts && !Confirm("Close Git setup", "Unsaved connection details will be discarded. Connections already saved in Git will remain. Close the workspace?")) e.Cancel = true;
            if (!e.Cancel) ResetBranchInspection();
            return;
        }
        e.Cancel = true;
        SetStatus(_unconfirmedGitCommand != null ? "Git process exit is not confirmed. Wait, then Retry to check before closing."
            : "A Git operation is still running. Wait for it or use Cancel operation before closing.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (IsIdle) Close(); }
}
