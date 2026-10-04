using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private IReadOnlyList<GitBranchInfo> _visibleBranches = [];
    private int _branchViewIndex;
    private bool _branchesContentReady;

    public IReadOnlyList<GitBranchInfo> VisibleBranches => _visibleBranches;
    private bool ShowingRemoteBranches => _branchViewIndex == 0;
    public int BranchViewIndex
    {
        get => _branchViewIndex;
        set
        {
            if (_branchViewIndex == value || value is not (0 or 1)) return;
            _branchViewIndex = value;
            ResetBranchInspection();
            UpdateBranchView();
            Changed();
        }
    }

    public Visibility BranchesContentVisibility => _branchesContentReady ? Visibility.Visible : Visibility.Collapsed;
    public string BranchListCaption => ShowingRemoteBranches
        ? SelectedRemote is { } remote ? $"{remote.Name} · last fetched" : "No active remote"
        : "Local branches";
    public string BranchListTooltip => ShowingRemoteBranches
        ? SelectedRemote is { } remote
            ? SensitiveDataProtection.Redact($"Branches from {remote.Name}\n{remote.FetchUrl}\nRemote history uses cached refs. Fetch refreshes this list.")
            : "Choose an active remote in Config."
        : "Branches stored in this checkout. Select one to compare committed history with the current local branch.";
    public string BranchHistoryTooltip => ShowingRemoteBranches
        ? "Show paginated commits from the selected remote branch's last fetched history."
        : "Show paginated commits from both local branches, including commits present on only one side.";
    public Visibility BranchListEmptyVisibility => VisibleBranches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string BranchListEmptyMessage => ShowingRemoteBranches
        ? SelectedRemote is { } remote ? $"No cached branches for {remote.Name}. Fetch to refresh." : "Choose an active remote in Config."
        : "No local branches with commits.";

    private void UpdateBranchView()
    {
        // Filtering is display-only. Workflow guards still inspect the complete snapshot.
        _visibleBranches = ShowingRemoteBranches
            ? SelectedRemote is { } remote
                ? Branches.Where(branch => branch.IsRemote && branch.Name.StartsWith(remote.Name + "/", StringComparison.Ordinal)).ToArray()
                : []
            : Branches.Where(branch => !branch.IsRemote).ToArray();
        if (BranchCurrentColumn != null) BranchCurrentColumn.Visibility = ShowingRemoteBranches ? Visibility.Collapsed : Visibility.Visible;
        if (BranchUpstreamColumn != null) BranchUpstreamColumn.Visibility = ShowingRemoteBranches ? Visibility.Collapsed : Visibility.Visible;
        Changed(nameof(VisibleBranches));
        Changed(nameof(BranchListCaption));
        Changed(nameof(BranchListTooltip));
        Changed(nameof(BranchHistoryTooltip));
        Changed(nameof(BranchListEmptyVisibility));
        Changed(nameof(BranchListEmptyMessage));
    }

    private void WorkspaceTab_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Nested grid and dropdown selections bubble through TabControl too.
        if (!ReferenceEquals(e.Source, WorkspaceTabs)) return;
        if (!ReferenceEquals(WorkspaceTabs.SelectedItem, BranchesTab))
        {
            _branchesContentReady = false;
            ResetBranchInspection();
            Changed(nameof(BranchesContentVisibility));
            return;
        }
        if (!_ready || !IsIdle || !GitContentAvailable) return;
        BranchViewIndex = 0;
        ResetBranchInspection();
        UpdateBranchView();
        _branchesContentReady = true;
        Changed(nameof(BranchesContentVisibility));
    }

    private Task<bool> FetchRemoteAsync(string root, GitRemoteInfo remote) => ExecuteAsync(
        $"Fetch {remote.Name}", token => GitRepositoryService.FetchAsync(root, remote.Name, token),
        successMessage: $"Fetch complete. Branches for {remote.Name} refreshed.",
        cancellationContext: " Remote refs may be partly refreshed. Fetch again to confirm the remote branch list.");
}
