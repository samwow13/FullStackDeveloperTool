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
    private string _activity = "";
    private string _commitDraft = "";
    private GitRemoteComparison? _comparison;
    private string? _comparisonError;
    private IReadOnlyList<GitBranchInfo> _mergeBranches = [];
    private string? _remoteCheck;

    public GitWorkspaceWindow(string projectName, IReadOnlyList<GitWorkspaceFolder> folders)
    {
        ProjectTitle = $"Git · {projectName}";
        Folders = folders;
        InitializeComponent();
        // Keep the footer reachable on smaller displays; the workspace pages scroll.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        DataContext = this;
        FolderPicker.SelectedIndex = folders.Count > 0 ? 0 : -1;
    }

    public string ProjectTitle { get; }
    public IReadOnlyList<GitWorkspaceFolder> Folders { get; }
    public IReadOnlyList<GitChange> Changes => _snapshot?.Changes ?? [];
    public IReadOnlyList<GitBranchInfo> Branches => _snapshot?.Branches ?? [];
    public IReadOnlyList<GitRemoteInfo> Remotes => _snapshot?.Remotes ?? [];
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy;
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
    public IReadOnlyList<GitBranchInfo> MergeBranches => _mergeBranches;
    public bool CanMerge => CanChangeBranch && _snapshot is { IsDetached: false }
        && MergePicker?.SelectedItem is GitBranchInfo;
    public string StatusText => _status;
    public string ActivityText => _activity;
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
    private string ProposedBranchName => BranchName?.Text.Trim() ?? "";
    private GitBranchInfo? ProposedLocalBranch => Branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == ProposedBranchName);
    private GitBranchInfo? ProposedOnlineBranch => Branches.FirstOrDefault(branch => branch.IsRemote
        && (branch.Name == $"{SelectedRemote?.Name}/{ProposedBranchName}" || branch.Name == ProposedBranchName));
    public bool CanCreateNamedBranch => CanChangeBranch && ProposedBranchName.Length > 0 && ProposedLocalBranch == null;
    public bool CanSwitchNamedBranch => CanSwitchBranch && (ProposedLocalBranch is { IsCurrent: false } || (ProposedLocalBranch == null && ProposedOnlineBranch != null));
    public string BranchActionText => !IsRepository ? "Select a repository first."
        : _powerReadError != null ? "Refresh to read the Git workflow state before changing branches."
        : HasSyncRecovery ? "Commit or review the pending local merge before changing branches."
        : _snapshot!.OperationState != null ? "Finish the active Git operation first."
        : Changes.Count > 0 ? "Commit or stash local changes before switching or merging."
        : ProposedBranchName.Length == 0 ? "Enter a branch name or select one below."
        : ProposedLocalBranch is { IsCurrent: true } ? "Current branch."
        : ProposedOnlineBranch != null && ProposedLocalBranch == null ? "Switch creates a local tracking branch from the last fetched version."
        : _snapshot.IsUnborn ? "Fetch an existing branch or create your first commit."
        : "";
    public string BranchPresence => !IsRepository || ProposedBranchName.Length == 0 ? ""
        : $"Local: {(ProposedLocalBranch != null ? "exists" : "not found")} · "
            + (SelectedRemote == null ? "No remote selected" : $"{SelectedRemote.Name}: {(ProposedOnlineBranch != null ? "last fetched" : "not cached")}")
            + (_remoteCheck == null ? "" : $"\n{_remoteCheck}");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void SetStatus(string text)
    {
        _status = text.Length > 360 ? text[..340] + "… See Activity for details." : text;
        Changed(nameof(StatusText));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        _displayedFolder = SelectedFolder?.Directory;
        await RefreshAsync();
        await EnsureSetupAsync();
    }

    private async void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        var keepConfigOpen = _setupOpen;
        ResetPublishFeedback();
        if (_displayedFolder != null) _drafts[_displayedFolder] = CaptureDraft();
        _displayedFolder = SelectedFolder?.Directory;
        _snapshot = null;
        _preferredRemote = null;
        _remoteCheck = null;
        _comparison = null;
        _powerDetails = null;
        _powerReadError = null;
        RestoreDraft(_displayedFolder != null ? _drafts.GetValueOrDefault(_displayedFolder) : null);
        if (keepConfigOpen) _setupOpen = true;
        Changed();
        await RefreshAsync();
        await EnsureSetupAsync();
        if (keepConfigOpen && !_setupOpen) OpenSetup(IsRepository ? SetupStep.Connections : SetupStep.Local);
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
            var mergeSelection = MergePicker.SelectedItem as GitBranchInfo;
            _mergeBranches = snapshot.Branches.Where(branch => !branch.IsCurrent).ToArray();
            Changed();
            RemotePicker.SelectedItem = Remotes.FirstOrDefault(remote => remote.Name == selectedRemoteName);
            MergePicker.SelectedItem = _mergeBranches.FirstOrDefault(branch => mergeSelection != null && branch.Name == mergeSelection.Name && branch.IsRemote == mergeSelection.IsRemote)
                ?? _mergeBranches.FirstOrDefault(branch => branch.IsRemote && branch.Name == $"{SelectedRemote?.Name}/{snapshot.Branch}");
            _preferredRemote = null;
            if (BranchName.Text.Length == 0 && !snapshot.IsDetached) BranchName.Text = snapshot.Branch;
        }
        finally { _applyingSnapshot = false; }
        SynchronizePublishBranchDraft();
        if (!_remoteDraftDirty) UpdateRemoteSelection();
        Changed();
    }

    private async Task RefreshAsync()
    {
        if (_busy || SelectedFolder is not { } folder) return;
        _busy = true;
        _operation = new();
        _remoteCheck = null;
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
            if (!snapshot.IsRepository) OpenSetup(SetupStep.Local);
            await RefreshComparisonAsync(_operation.Token);
            await RefreshPowerDetailsAsync(_operation.Token);
            if (snapshot.IsRepository && SelectedRemote == null && !_setupOpen && !HasSyncRecovery)
                OpenSetup(Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Provider);
            SetStatus($"Refreshed {DateTime.Now:T} · remote comparison uses last fetched history.");
        }
        catch (Exception ex)
        {
            _snapshot = null;
            _powerDetails = null;
            _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
            _localSetupError = ex is GitRepositoryTrustException
                ? "Git blocked this repository because another Windows account owns it. Review the folder below before trusting it."
                : "Git could not read this folder. Check that the folder exists and Git for Windows is installed, then retry. Details are below.";
            OpenSetup(SetupStep.Local);
            var detail = SafeError(ex);
            AddActivity("Refresh", folder.Directory, detail);
            SetStatus(ex is OperationCanceledException ? "Refresh canceled or timed out. Status is unavailable until the next refresh." : detail);
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            Changed();
        }
    }

    // Every action captures its folder and inputs before entering the worker. Changing
    // selection and closing are disabled until the outcome and fresh status are known.
    private async Task<bool> ExecuteAsync(string title, Func<CancellationToken, Task<string>> action,
        bool refresh = true, Action<string>? completed = null, string? successMessage = null, bool useResultAsStatus = false,
        GitRemoteInfo? pushedRemote = null, string? cancellationContext = null)
    {
        if (_busy || SelectedFolder is not { } folder) return false;
        var expected = _snapshot;
        _busy = true;
        _operation = new();
        _remoteCheck = null;
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
            AddActivity(title, Root ?? folder.Directory, string.IsNullOrWhiteSpace(result) ? outcome : result);
            completed?.Invoke(result);
            if (pushedRemote != null)
            {
                CompletePublishFeedback(true, outcome);
                await OpenPostPushLinkAsync(pushedRemote);
            }
        }
        catch (Exception ex)
        {
            if (ex is GitCommitPushException { CommitCreated: true }) { _commitDraft = ""; _syncMessageDraft = ""; }
            outcome = ex is OperationCanceledException
                ? SafeError(ex) + (cancellationContext ?? " Changes may already have taken effect; review local and remote status before retrying.")
                : SafeError(ex);
            AddActivity(title, Root ?? folder.Directory, outcome);
        }
        finally
        {
            if (refresh)
            {
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
                }
                catch (Exception ex)
                {
                    _snapshot = null;
                    _powerDetails = null;
                    _trustRoot = (ex as GitRepositoryTrustException)?.RepositoryRoot;
                    _localSetupError = "The operation ended, but local status could not be read. Retry before continuing.";
                    OpenSetup(SetupStep.Local);
                    _comparison = null;
                    outcome += " Local refresh failed; refresh before another repository action.";
                    AddActivity("Refresh after action", folder.Directory, SafeError(ex));
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

    private void AddActivity(string title, string folder, string detail)
    {
        var safe = SensitiveDataProtection.Redact($"[{DateTime.Now:T}] {title}\n{folder}\n{detail}\n\n");
        _activity = safe + _activity;
        if (_activity.Length > 60000) _activity = _activity[..60000] + "\n[Older activity omitted]";
        Changed(nameof(ActivityText));
    }

    private async void Remote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSnapshot) return;
        var keepConfigOpen = _setupOpen;
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
            await RefreshAsync();
            await EnsureSetupAsync();
            if (keepConfigOpen && !_setupOpen) OpenSetup(IsRepository ? SetupStep.Connections : SetupStep.Local);
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
                AddActivity("Select active Git connection", root, selection != null
                    ? "The saved connection changed or was removed. Choose Config, select a connection, then choose Use selected connection."
                    : "The current branch does not identify one active connection. Choose Config, select a connection, then choose Use selected connection.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddActivity("Read active Git connection", root, SafeError(exception));
        }
    }

    private void UpdateRemoteSelection()
    {
        if (_applyingSnapshot || RemoteName == null || _remoteDraftDirty) return;
        _remoteCheck = null;
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
        InvalidateConnectionCheck();
        Changed();
    }

    private FormDraft CaptureDraft() => new(_commitDraft, BranchName.Text, RemoteName.Text, RemoteUrl.Text,
        _remoteDraftDirty, InitialBranch.Text, _setupProvider, _setupStep, _setupOpen, _publishBranchDraft,
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
            BranchName.Text = draft?.Branch ?? "";
            RemoteName.Text = draft?.Remote ?? "origin";
            RemoteUrl.Text = draft?.Url ?? "";
            InitialBranch.Text = draft?.InitialBranch ?? "main";
            _setupProvider = draft?.Provider;
            _setupStep = draft?.Step ?? SetupStep.Local;
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

    private sealed record FormDraft(string Commit, string Branch, string Remote, string Url, bool RemoteDirty,
        string InitialBranch, GitHostingProvider? Provider, SetupStep Step, bool SetupOpen, string PublishBranch,
        string SyncMessage, CommitDraftScope? CommitScope, PublishBranchScope? PublishScope);

    private void BranchName_Changed(object sender, TextChangedEventArgs e)
    {
        _remoteCheck = null;
        Changed(nameof(BranchPresence));
        Changed(nameof(BranchActionText));
        Changed(nameof(CanCreateNamedBranch));
        Changed(nameof(CanSwitchNamedBranch));
    }

    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSnapshot || BranchesGrid.SelectedItem is not GitBranchInfo branch) return;
        if (branch.IsRemote)
        {
            var remote = Remotes.OrderByDescending(remote => remote.Name.Length)
                .FirstOrDefault(remote => branch.Name.StartsWith(remote.Name + "/", StringComparison.Ordinal));
            if (remote != null)
            {
                RemotePicker.SelectedItem = remote;
                BranchName.Text = branch.Name[(remote.Name.Length + 1)..];
                return;
            }
        }
        BranchName.Text = branch.Name;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (Root is not { } root || SelectedRemote is not { } remote) return;
        await ExecuteAsync($"Fetch {remote.Name}", token => GitRepositoryService.FetchAsync(root, remote.Name, token),
            successMessage: "Fetch complete. Remote comparisons refreshed.");
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

    private void MergeSelection_Changed(object sender, SelectionChangedEventArgs e) => Changed(nameof(CanMerge));

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMerge || Root is not { } root || MergePicker.SelectedItem is not GitBranchInfo branch) return;
        var current = _snapshot!.Branch;
        if (!Confirm("Merge branch", $"Repository: {root}\n\nMerge {branch.Name} into {current}?\n\nUses the {(branch.IsRemote ? "last fetched" : "local")} branch. Conflicts, if any, stay open for resolution in your editor.")) return;
        await ExecuteAsync($"Merge {branch.Name} into {current}", token => GitRepositoryService.MergeBranchAsync(root, branch.Name, current, token, sourceIsRemote: branch.IsRemote));
    }

    private async void CheckBranch_Click(object sender, RoutedEventArgs e)
    {
        if (Root is not { } root || SelectedRemote is not { } remote) return;
        var name = BranchName.Text.Trim();
        await ExecuteAsync("Check remote branch", token => GitRepositoryService.CheckRemoteBranchAsync(root, remote.Name, name, token), false,
            result => { _remoteCheck = $"Checked {DateTime.Now:T}: {result}"; Changed(nameof(BranchPresence)); });
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCreateNamedBranch || Root is not { } root) return;
        var name = BranchName.Text.Trim();
        await ExecuteAsync("Create and switch branch", token => GitRepositoryService.CreateBranchAsync(root, name, token));
    }

    private async void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSwitchNamedBranch || Root is not { } root) return;
        var name = BranchName.Text.Trim();
        if (!Branches.Any(branch => !branch.IsRemote && branch.Name == name) && SelectedRemote is { } remote
            && Branches.Any(branch => branch.IsRemote && branch.Name == $"{remote.Name}/{name}")) name = $"{remote.Name}/{name}";
        await ExecuteAsync("Switch branch", token => GitRepositoryService.SwitchBranchAsync(root, name, token));
    }

    private bool Confirm(string title, string text) => !_busy && MessageBox.Show(this,
        SensitiveDataProtection.Redact(text), title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        SetStatus("Cancel requested. Waiting for the command to finish and report its outcome…");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy)
        {
            if (HasSetupDrafts && !Confirm("Close Git setup", "Unsaved connection details will be discarded. Connections already saved in Git will remain. Close the workspace?")) e.Cancel = true;
            return;
        }
        e.Cancel = true;
        SetStatus("A Git operation is still running. Wait for it or use Cancel operation before closing.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
}
