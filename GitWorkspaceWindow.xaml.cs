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
    public bool CanCopyUrl => SelectedRemote is { UrlCanCopy: true };
    public bool CanSync => HasRemote && _snapshot is { IsDetached: false, IsUnborn: false, OperationState: null };
    public bool CanChangeBranch => _snapshot is { IsRepository: true, IsUnborn: false, OperationState: null } && Changes.Count == 0;
    public bool CanSwitchBranch => _snapshot is { IsRepository: true, OperationState: null } && Changes.Count == 0;
    public bool CanCommitAllPush => HasRemote && _snapshot is { IsRepository: true, IsDetached: false, OperationState: null }
        && Changes.Count > 0 && !Changes.Any(change => change.IsConflict);
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
        ? $"{operation}. Finish or abort it in Git or your editor, then refresh." : "";
    public string RemoteSummary => SelectedRemote is { } remote
        ? remote.FetchUrl == remote.PushUrl ? remote.FetchUrl : $"Fetch: {remote.FetchUrl}\nPush: {remote.PushUrl}"
        : "Connect a remote in Repository setup.";
    public string CommitAvailabilityText => !IsRepository ? "Initialize or select a repository to begin."
        : _snapshot!.OperationState != null ? "Finish the active Git operation, then refresh."
        : Changes.Any(change => change.IsConflict) ? "Resolve conflicts in your editor, then refresh."
        : _snapshot.IsDetached ? "Switch to a local branch before committing."
        : !HasRemote ? "Connect a remote in Repository setup."
        : Changes.Count == 0 ? "No uncommitted changes. Use Push to upload existing commits."
        : $"Stage all {Changes.Count:N0} changed files, commit, and push to {SelectedRemote!.Name}/{_snapshot.Branch}.";
    private string ProposedBranchName => BranchName?.Text.Trim() ?? "";
    private GitBranchInfo? ProposedLocalBranch => Branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == ProposedBranchName);
    private GitBranchInfo? ProposedOnlineBranch => Branches.FirstOrDefault(branch => branch.IsRemote
        && (branch.Name == $"{SelectedRemote?.Name}/{ProposedBranchName}" || branch.Name == ProposedBranchName));
    public bool CanCreateNamedBranch => CanChangeBranch && ProposedBranchName.Length > 0 && ProposedLocalBranch == null;
    public bool CanSwitchNamedBranch => CanSwitchBranch && (ProposedLocalBranch is { IsCurrent: false } || (ProposedLocalBranch == null && ProposedOnlineBranch != null));
    public string BranchActionText => !IsRepository ? "Select a repository first."
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
    }

    private async void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _busy) return;
        if (_displayedFolder != null) _drafts[_displayedFolder] = CaptureDraft();
        _displayedFolder = SelectedFolder?.Directory;
        _snapshot = null;
        _preferredRemote = null;
        _remoteCheck = null;
        _comparison = null;
        RestoreDraft(_displayedFolder != null ? _drafts.GetValueOrDefault(_displayedFolder) : null);
        Changed();
        await RefreshAsync();
    }

    private void ApplySnapshot(GitRepositorySnapshot snapshot)
    {
        var selectedRemoteName = _preferredRemote ?? SelectedRemote?.Name ?? RemoteName.Text.Trim();
        _applyingSnapshot = true;
        try
        {
            _snapshot = snapshot;
            _comparison = null;
            _comparisonError = null;
            var mergeSelection = MergePicker.SelectedItem as GitBranchInfo;
            _mergeBranches = snapshot.Branches.Where(branch => !branch.IsCurrent).ToArray();
            Changed();
            RemotePicker.SelectedItem = Remotes.FirstOrDefault(remote => remote.Name == selectedRemoteName)
                ?? Remotes.FirstOrDefault(remote => remote.Name == "origin") ?? Remotes.FirstOrDefault();
            MergePicker.SelectedItem = _mergeBranches.FirstOrDefault(branch => mergeSelection != null && branch.Name == mergeSelection.Name && branch.IsRemote == mergeSelection.IsRemote)
                ?? _mergeBranches.FirstOrDefault(branch => branch.IsRemote && branch.Name == $"{SelectedRemote?.Name}/{snapshot.Branch}");
            _preferredRemote = null;
            if (BranchName.Text.Length == 0 && !snapshot.IsDetached) BranchName.Text = snapshot.Branch;
            if (!snapshot.IsRepository) WorkspaceTabs.SelectedItem = SetupTab;
        }
        finally { _applyingSnapshot = false; }
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
            ApplySnapshot(snapshot);
            await RefreshComparisonAsync(_operation.Token);
            SetStatus($"Refreshed {DateTime.Now:T} · remote comparison uses last fetched history.");
        }
        catch (Exception ex)
        {
            _snapshot = null;
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
        bool refresh = true, Action<string>? completed = null, string? successMessage = null, bool useResultAsStatus = false)
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
        }
        catch (Exception ex)
        {
            if (ex is GitCommitPushException { CommitCreated: true }) _commitDraft = "";
            outcome = ex is OperationCanceledException
                ? SafeError(ex) + " Changes may already have taken effect; review local and remote status before retrying."
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
                    ApplySnapshot(await Task.Run(() => GitRepositoryService.ReadAsync(folder.Directory, _operation.Token)));
                    await RefreshComparisonAsync(_operation.Token);
                }
                catch (Exception ex)
                {
                    _snapshot = null;
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
        UpdateRemoteSelection();
        _comparison = null;
        _comparisonError = null;
        if (_ready && !_busy) await RefreshAsync();
    }

    private void UpdateRemoteSelection()
    {
        if (_applyingSnapshot || RemoteName == null) return;
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
        if (_ready && !_applyingSnapshot) _remoteDraftDirty = true;
    }

    private FormDraft CaptureDraft() => new(_commitDraft, BranchName.Text, RemoteName.Text, RemoteUrl.Text,
        _remoteDraftDirty, InitialBranch.Text, AzureOrganization.Text, AzureProject.Text, AzureRepository.Text);

    private void RestoreDraft(FormDraft? draft)
    {
        _applyingSnapshot = true;
        try
        {
            _commitDraft = draft?.Commit ?? "";
            BranchName.Text = draft?.Branch ?? "";
            RemoteName.Text = draft?.Remote ?? "origin";
            RemoteUrl.Text = draft?.Url ?? "";
            InitialBranch.Text = draft?.InitialBranch ?? "main";
            AzureOrganization.Text = draft?.Organization ?? "";
            AzureProject.Text = draft?.Project ?? "";
            AzureRepository.Text = draft?.Repository ?? "";
            _remoteDraftDirty = draft?.RemoteDirty ?? false;
        }
        finally { _applyingSnapshot = false; }
    }

    private sealed record FormDraft(string Commit, string Branch, string Remote, string Url, bool RemoteDirty,
        string InitialBranch, string Organization, string Project, string Repository);

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
        await ExecuteAsync($"Push {remote.Name}/{branch}", token => GitRepositoryService.PushAsync(root, remote.Name, branch, token),
            useResultAsStatus: true);
    }

    private async void CommitAllPush_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanCommitAllPush || Root is not { } root || SelectedRemote is not { } remote) return;
        var branch = _snapshot!.Branch;
        var prompt = new GitCommitWindow(root, branch, remote.Name, remote.PushUrl, Changes.Count, _commitDraft) { Owner = this };
        var accepted = prompt.ShowDialog() == true;
        _commitDraft = prompt.Message;
        if (!accepted) return;
        var message = _commitDraft.Trim();
        await ExecuteAsync("Commit all & push", token => GitRepositoryService.StageAllCommitAndPushAsync(root, message, remote.Name, branch, token),
            completed: _ => _commitDraft = "", useResultAsStatus: true);
    }

    private void MergeSelection_Changed(object sender, SelectionChangedEventArgs e) => Changed(nameof(CanMerge));

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMerge || Root is not { } root || MergePicker.SelectedItem is not GitBranchInfo branch) return;
        var current = _snapshot!.Branch;
        if (!Confirm("Merge branch", $"Repository: {root}\n\nMerge {branch.Name} into {current}?\n\nUses the {(branch.IsRemote ? "last fetched" : "local")} branch. Conflicts, if any, stay open for resolution in your editor.")) return;
        await ExecuteAsync($"Merge {branch.Name} into {current}", token => GitRepositoryService.MergeBranchAsync(root, branch.Name, current, token, sourceIsRemote: branch.IsRemote));
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInitialize || SelectedFolder is not { } folder) return;
        var branch = InitialBranch.Text.Trim();
        await ExecuteAsync("Initialize repository", token => GitRepositoryService.InitializeAsync(folder.Directory, branch, token),
            successMessage: "Repository initialized. Connect a remote to get started.");
    }

    private async void SaveRemote_Click(object sender, RoutedEventArgs e)
    {
        if (Root is not { } root) return;
        var name = RemoteName.Text.Trim();
        var url = RemoteUrl.Text.Trim();
        var previous = Remotes.FirstOrDefault(remote => remote.Name == name);
        if (previous != null && !Confirm("Replace remote URL", $"Repository: {root}\nRemote: {name}\nCurrent fetch: {previous.FetchUrl}\nCurrent push: {previous.PushUrl}\n\nReplace this remote with the URL entered in the form?")) return;
        await ExecuteAsync("Save remote", token => GitRepositoryService.SaveRemoteAsync(root, name, url, previous != null, token),
            completed: _ => { _remoteDraftDirty = false; _preferredRemote = name; },
            successMessage: "Remote saved.");
    }

    private async void AzureLookup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder) return;
        var org = AzureOrganization.Text.Trim();
        var project = AzureProject.Text.Trim();
        var name = AzureRepository.Text.Trim();
        if (await ExecuteAsync("Look up Azure repository", token => GitRepositoryService.LookupAzureRemoteAsync(folder.Directory, org, project, name, token),
            false, url => RemoteUrl.Text = url.Trim())) SetStatus("Repository URL loaded. Choose Save remote to connect it.");
    }

    private async void AzureCreate_Click(object sender, RoutedEventArgs e)
    {
        if (Root is not { } root) return;
        var remote = RemoteName.Text.Trim();
        var org = AzureOrganization.Text.Trim();
        var project = AzureProject.Text.Trim();
        var name = AzureRepository.Text.Trim();
        if (Remotes.Any(item => item.Name == remote))
        {
            SetStatus("That remote name already exists. Enter a new name before creating a repository.");
            return;
        }
        if (org.Length == 0 || project.Length == 0 || name.Length == 0 || remote.Length == 0)
        {
            SetStatus("Enter the Azure organization URL, project, repository name and local remote name first.");
            return;
        }
        if (!Confirm("Create Azure DevOps repository", $"Organization: {org}\nProject: {project}\nRepository: {name}\n\nConnect to local repository: {root}\nRemote name: {remote}\n\nCreate an empty hosted repository using this project's access permissions? Push commits separately after creation.")) return;
        await ExecuteAsync("Create and connect Azure repository", token => GitRepositoryService.CreateAzureRemoteAsync(root, remote, org, project, name, token),
            completed: _ => { _remoteDraftDirty = false; _preferredRemote = remote; });
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

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRemote is not { UrlCanCopy: true } remote) return;
        try { Clipboard.SetText(remote.FetchUrl); SetStatus("Repository clone link copied."); }
        catch (Exception) { SetStatus("The clipboard is busy. Try Copy repository link again."); }
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
        if (!_busy) return;
        e.Cancel = true;
        SetStatus("A Git operation is still running. Wait for it or use Cancel operation before closing.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
}
