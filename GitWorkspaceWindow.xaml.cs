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

public partial class GitWorkspaceWindow : Window, INotifyPropertyChanged, IDashboardReplacementWindow
{
    private bool _replacementClosePending;
    public bool IsDashboardReplacementReady => IsIdle;
    public bool IsDashboardReplacementClosePending => _replacementClosePending;

    private GitRepositorySnapshot? _snapshot;
    private GitActiveConnectionInfo? _activeConnection;
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
    private readonly ProjectGitService _gitService;
    private readonly string _projectId;
    private bool _cacheRefreshFailed;
    private bool _sharedRefreshing;
    private bool _closed;
    private bool _sharedUpdatePending;

    public GitWorkspaceWindow(string projectName, IReadOnlyList<GitWorkspaceFolder> folders,
        bool scanChildRepositories = true, string? preferredRepositoryRoot = null,
        string? projectId = null, ProjectGitService? gitService = null,
        Func<GitProjectConnectRequest, Task<IDisposable>>? prepareProjectConnect = null,
        Func<GitProjectConnectResult, Task>? applyProjectConnect = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        ProjectTitle = $"Git · {projectName}";
        _configuredFolders = folders.ToArray();
        _scanProjectRootChildren = scanChildRepositories;
        _preferredRepositoryRoot = preferredRepositoryRoot;
        _gitService = gitService ?? ProjectGitService.Shared;
        _projectId = projectId ?? string.Join("|", folders.Select(folder => folder.Directory));
        _prepareProjectConnect = prepareProjectConnect;
        _applyProjectConnect = applyProjectConnect;
        Folders = _configuredFolders;
        InitializeComponent();
        // These controls move between windows; keep their workflow bindings attached.
        GitDialogPanels.DataContext = this;
        // Keep access-key label targets when the setup controls move to their host.
        var setupNames = new NameScope();
        foreach (var control in new FrameworkElement[] { FolderPicker, RemotePicker, InitialBranch, RemoteUrl, RemoteName, ConnectionPicker })
            setupNames.RegisterName(control.Name, control);
        NameScope.SetNameScope(GitDialogPanels, setupNames);
        // Keep the footer reachable on smaller displays; the workspace pages scroll.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        DataContext = this;
        Activated += (_, _) => ScheduleMissingReleaseBranchPrompt();
        Closed += (_, _) => { _closed = true; _gitService.Changed -= SharedGit_Changed; };
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
    public bool CanInitialize => !_cacheRefreshFailed && !_sharedRefreshing && _snapshot is { IsRepository: false };
    public bool HasRemote => !_cacheRefreshFailed && !_sharedRefreshing && IsRepository && SelectedRemote != null;
    public bool CanSync => HasRemote && _powerReadError == null && !HasSyncRecovery && _snapshot is { IsDetached: false, IsUnborn: false, OperationState: null };
    public bool CanChangeBranch => !_cacheRefreshFailed && !_sharedRefreshing && _powerReadError == null && !HasSyncRecovery && _snapshot is { IsRepository: true, IsUnborn: false, OperationState: null } && Changes.Count == 0;
    public bool CanSwitchBranch => !_cacheRefreshFailed && !_sharedRefreshing && _powerReadError == null && !HasSyncRecovery && _snapshot is { IsRepository: true, OperationState: null } && Changes.Count == 0;
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
    public string ActiveConnectionLabel => "Active: " + (_activeConnection?.Label ?? "Reading connection…");
    public string ActiveConnectionColor => _activeConnection?.Foreground ?? "#B8C6D9";
    public string ActiveConnectionDetail => _activeConnection?.Detail ?? "Reading the selected repository's active Git connection.";
    public string CommitAvailabilityText => !IsRepository ? "Initialize or select a repository to begin."
        : _sharedRefreshing ? "Background Git refresh is running. Wait for it to finish before committing."
        : _cacheRefreshFailed ? "Force refresh to confirm current Git state before committing."
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
        : _sharedRefreshing ? "Background Git refresh is running. Wait for it to finish before changing branches."
        : _cacheRefreshFailed ? "Force refresh to confirm current Git state before changing branches."
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
        await InitializeGitDialogAsync();
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
        _activeConnection = null;
        _cacheRefreshFailed = false;
        _sharedRefreshing = false;
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
            RemotePicker.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
            RemotePicker.SelectedItem = Remotes.FirstOrDefault(remote => remote.Name == selectedRemoteName);
            _preferredRemote = null;
        }
        finally { _applyingSnapshot = false; }
        SynchronizePublishBranchDraft();
        if (!_remoteDraftDirty) UpdateRemoteSelection();
        UpdateBranchView();
        Changed();
    }

    private void ApplyRepositoryState(GitRepositoryState state)
    {
        _activeConnection = state.ActiveConnection;
        if (state.Snapshot is not { } snapshot)
            throw state.Failure ?? new InvalidOperationException(state.Error ?? "Git status is unavailable. Force refresh to retry.");
        if (state.Failure is GitCommandExitUnconfirmedException unconfirmed) throw unconfirmed;
        _cacheRefreshFailed = state.Error != null || state.ComparisonError != null || state.PowerError != null;
        _sharedRefreshing = state.IsRefreshing;
        _gitLoadFailed = false;
        _preferredRemote ??= SelectedRemote?.Name ?? state.ActiveRemote?.Name ?? "";
        ApplySnapshot(snapshot);
        _comparison = state.Comparison;
        _comparisonError = state.ComparisonError;
        _powerDetails = state.PowerDetails;
        _powerReadError = state.PowerError;
        _trustRoot = (state.Failure as GitRepositoryTrustException)?.RepositoryRoot;
        _localSetupError = state.Error;
        foreach (var error in new[] { state.Error, state.SelectionError, state.ComparisonError, state.PowerError, state.FetchError, state.LedgerError })
            if (!string.IsNullOrWhiteSpace(error) && !_latestErrorDetails.Contains(error, StringComparison.Ordinal)) ReportError(error);
        if (_cacheRefreshFailed)
            SetStatus("Git refresh failed. Previous status remains visible. Force refresh before another repository action.");
        Changed();
    }

    private void SharedGit_Changed(object? sender, ProjectGitChangedEventArgs e)
    {
        if (_closed) return;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_closed || !_ready || _selectingRepository || SelectedFolder is not { } folder) return;
            if (e.RepositoryRoot != null && !string.Equals(e.RepositoryRoot, Root ?? folder.Directory, StringComparison.OrdinalIgnoreCase)) return;
            if (e.ProjectId != null && e.ProjectId != _projectId) return;
            if (!IsIdle || _gitLoadDepth > 0) { _sharedUpdatePending = true; return; }
            try
            {
                var state = await _gitService.GetRepositoryAsync(folder.Directory, SelectedRemote?.Name ?? "");
                if (_closed || !IsIdle || !string.Equals(folder.Directory, SelectedFolder?.Directory, StringComparison.OrdinalIgnoreCase))
                { _sharedUpdatePending = true; return; }
                ApplyRepositoryState(state);
                ScheduleMissingReleaseBranchPrompt();
            }
            catch (Exception exception)
            {
                _activeConnection = new();
                if (exception is GitCommandExitUnconfirmedException command) HoldUnconfirmedBranchCommand(command);
                else { _cacheRefreshFailed = true; ReportError(SafeError(exception)); Changed(); }
            }
        }));
    }

    private async Task ConsumeSharedUpdateAsync()
    {
        if (!_sharedUpdatePending || _closed || !IsIdle || SelectedFolder is not { } folder) return;
        _sharedUpdatePending = false;
        try { ApplyRepositoryState(await _gitService.GetRepositoryAsync(folder.Directory, SelectedRemote?.Name ?? "")); }
        catch (Exception exception)
        {
            _activeConnection = new();
            if (exception is GitCommandExitUnconfirmedException command) HoldUnconfirmedBranchCommand(command);
            else { _cacheRefreshFailed = true; ReportError(SafeError(exception)); Changed(); }
        }
    }

    private async Task RefreshAsync(bool fetchRemote = false, bool showLoading = true, CancellationToken cancellationToken = default,
        bool forceRefresh = true)
    {
        if (!IsIdle) return;
        if (SelectedFolder is not { } folder)
        {
            FailGitLoad("No project folder is configured. Close this window and choose a project folder first.");
            return;
        }
        var loading = showLoading && _snapshot == null;
        if (loading) BeginGitLoad();
        else _gitLoadFailed = false;
        EndPublishBranchEdit();
        _busy = true;
        Changed();
        if (!await DrainBranchInspectionAsync())
        {
            _busy = false;
            if (loading) EndGitLoad();
            Changed();
            return;
        }
        ClearErrorDetails();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Changed();
        SetStatus(forceRefresh ? "Refreshing Git status…" : "Reading shared Git status…");
        try
        {
            var remoteName = _preferredRemote ?? (_snapshot != null ? SelectedRemote?.Name ?? "" : null);
            var state = forceRefresh
                ? await _gitService.RefreshRepositoryAsync(folder.Directory, remoteName, fetchRemote, _operation.Token)
                : await _gitService.GetRepositoryAsync(folder.Directory, remoteName, _operation.Token);
            ApplyRepositoryState(state);
            var snapshot = state.Snapshot!;
            if (!snapshot.IsRepository && !_remoteDraftDirty && _setupStep != SetupStep.Local) OpenSetup(SetupStep.Choice);
            CheckGitReadFailures();
            if (snapshot.IsRepository && SelectedRemote == null && _configOpen && !_setupOpen && !HasSyncRecovery)
                OpenSetup(Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice);
            if (!_cacheRefreshFailed) SetStatus($"Git status {state.ReadAt.LocalDateTime:T} · " + (_comparison is { IsLocalComparison: true }
                ? "line totals use the last local commit." : "remote comparison uses last fetched history."));
        }
        catch (Exception ex)
        {
            _activeConnection = new();
            if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
            _cacheRefreshFailed = true;
            _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
            _localSetupError = ex is GitRepositoryTrustException
                ? "Git blocked this repository because another Windows account owns it. Review the folder below before trusting it."
                : "Git could not read this folder. Check that the folder exists and Git for Windows is installed, then retry. Details are below.";
            if (_snapshot == null || _unconfirmedGitCommand != null)
                FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again."
                    : ex is OperationCanceledException ? "Git loading was canceled or timed out. Try again."
                    : _trustRoot != null ? "This repository needs your trust before Git can load it." : "Git could not load this folder. Check the folder and Git installation, then try again.");
            var detail = SafeError(ex);
            if (fetchRemote && ex is OperationCanceledException)
                detail += " Remote refs may be partly refreshed. Retry loading or use Fetch to confirm them.";
            ReportError(detail);
            SetStatus(ex is OperationCanceledException
                ? "Refresh canceled or timed out. Force refresh before another repository action."
                    + (fetchRemote ? " Remote refs may be partly refreshed. Retry loading or use Fetch to confirm them." : "")
                : detail);
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            if (loading) EndGitLoad();
            Changed();
            await ConsumeSharedUpdateAsync();
        }
    }

    // Every action captures its folder and inputs before entering the worker. Changing
    // selection and closing are disabled until the outcome and fresh status are known.
    private async Task<bool> ExecuteAsync(string title, Func<CancellationToken, Task<string>> action,
        bool refresh = true, Action<string>? completed = null, string? successMessage = null, bool useResultAsStatus = false,
        GitRemoteInfo? pushedRemote = null, string? cancellationContext = null, GitRepositorySnapshot? reviewedSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdle || SelectedFolder is not { } folder) return false;
        // A background update during a review must never replace the state the user approved.
        var expected = reviewedSnapshot ?? _snapshot;
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
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            var commandFailure = ex;
            while (commandFailure is not GitCommandExitUnconfirmedException && commandFailure.InnerException is { } inner)
                commandFailure = inner;
            if (commandFailure is GitCommandExitUnconfirmedException unconfirmed)
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
                SetStatus($"{outcome} Refreshing local status…");
                _operation.Dispose();
                _operation = new();
                try
                {
                    // A command updates only its checkout. Every Git surface then consumes
                    // this shared result instead of loading and routing its own workspace.
                    _gitService.InvalidateRepository(Root ?? folder.Directory);
                    var refreshed = await _gitService.RefreshRepositoryAsync(folder.Directory,
                        _preferredRemote ?? SelectedRemote?.Name, fetchRemote: false, _operation.Token);
                    ApplyRepositoryState(refreshed);
                    CheckGitReadFailures();
                    if (_cacheRefreshFailed)
                        outcome += " Local refresh failed; force refresh before another repository action.";
                }
                catch (Exception ex)
                {
                    if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
                    _cacheRefreshFailed = true;
                    _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
                    _localSetupError = "The operation ended, but local status could not be read. Retry before continuing.";
                    if (_snapshot == null || _unconfirmedGitCommand != null)
                        FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again."
                            : "The operation ended, but Git status could not reload. Retry loading before continuing.");
                    outcome += " Local refresh failed; refresh before another repository action.";
                    ReportError(SafeError(ex));
                }
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
            await ConsumeSharedUpdateAsync();
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
        _activeConnection = null;
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
            await _gitService.RefreshLedgerAsync(root, token);
            return $"{remote.Name} is now the active connection for agent change summaries in this checkout.";
        }, refresh: false, useResultAsStatus: true);

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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadGitWorkspaceAsync(verifyConnection: false, forceRefresh: true);
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
        var snapshot = _snapshot!;
        var branch = snapshot.Branch;
        if (!Confirm("Push commits", $"Repository: {root}\nBranch: {branch}\nDestination: {remote.Name}/{branch}\n{remote.PushUrl}\n\nPush all existing commits on this branch?")) return;
        BeginPublishFeedback(branch, remote.Name);
        SetPublishPhase("Pushing commits");
        var pushed = await ExecuteAsync($"Push {remote.Name}/{branch}", token => GitRepositoryService.PushAsync(root, remote.Name, branch, token),
            useResultAsStatus: true, pushedRemote: remote, reviewedSnapshot: snapshot);
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

    private bool Confirm(string title, string text) => IsIdle && MessageBox.Show(DialogOwner,
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
            _replacementClosePending = true;
            _busy = true;
            var closePosted = false;
            Changed();
            try
            {
                var stopped = await DrainBranchInspectionAsync();
                if (stopped)
                {
                    // A completed inspection can drain synchronously inside Closing.
                    // Post the next close so WPF finishes the canceled close first.
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (!_closed) (sender as Window ?? this).Close(); }
                        finally { _replacementClosePending = false; }
                    }));
                    closePosted = true;
                }
            }
            finally
            {
                _busy = false;
                if (!closePosted) _replacementClosePending = false;
                Changed();
            }
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

    private void Close_Click(object sender, RoutedEventArgs e) { if (IsIdle) CloseActiveGitDialog(); }
}
