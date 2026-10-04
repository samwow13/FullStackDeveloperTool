using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private CancellationTokenSource? _branchInspectionCancellation;
    private long _branchInspectionVersion;
    private GitBranchComparison? _selectedBranchComparison;
    private bool _branchInspectionLoading;
    private readonly HashSet<Task<GitBranchComparison>> _branchInspectionTasks = [];
    private string _branchInspectionStatus = "";

    public bool CanShowBranchHistory => IsIdle && !_branchInspectionLoading && _selectedBranchComparison?.Available == true;
    public string SelectedBranchComparisonLabel => _branchInspectionLoading ? ShowingRemoteBranches ? "Reading branch history…" : "Comparing commits…"
        : _selectedBranchComparison is { Available: true } comparison
            ? comparison.SelectedBranchOnly ? $"{comparison.TotalCommits:N0} commits · last fetched"
                : $"Local: {comparison.LocalOnlyCommits:N0} ahead · {comparison.SelectedOnlyCommits:N0} behind"
            : _branchInspectionStatus.Length > 0 ? _branchInspectionStatus
                : ShowingRemoteBranches ? "Select a remote branch to view commit history." : "Select a local branch to compare commits.";
    public string SelectedBranchComparisonDetail => _selectedBranchComparison is { Available: true } comparison
        ? comparison.SelectedBranchOnly ? comparison.SelectedBranchName
            : $"{comparison.CurrentBranch} versus {comparison.SelectedBranchName} · local history"
        : ShowingRemoteBranches ? "Remote history uses last fetched branches." : "Committed history only; uncommitted changes are excluded.";

    private void ResetBranchInspection(bool clearSelection = true)
    {
        _branchInspectionVersion++;
        _branchInspectionCancellation?.Cancel();
        _branchInspectionCancellation = null;
        _selectedBranchComparison = null;
        _branchInspectionLoading = false;
        _branchInspectionStatus = "";
        if (clearSelection && BranchesGrid != null) BranchesGrid.SelectedItem = null;
        Changed(nameof(CanShowBranchHistory));
        Changed(nameof(SelectedBranchComparisonLabel));
        Changed(nameof(SelectedBranchComparisonDetail));
    }

    private async void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSnapshot || !IsIdle) return;
        ResetBranchInspection(clearSelection: false);
        if (BranchesGrid.SelectedItem is not GitBranchInfo selected || !VisibleBranches.Contains(selected)
            || _snapshot is not { } snapshot || Root is not { } root) return;
        ClearErrorDetails();
        var version = _branchInspectionVersion;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        _branchInspectionCancellation = cancellation;
        _branchInspectionLoading = true;
        Changed(nameof(CanShowBranchHistory));
        Changed(nameof(SelectedBranchComparisonLabel));
        Task<GitBranchComparison>? inspection = null;
        try
        {
            inspection = Task.Run(() => selected.IsRemote
                ? GitRepositoryService.ReadBranchHistoryAsync(root, selected, cancellation.Token)
                : GitRepositoryService.ReadBranchComparisonAsync(root, snapshot.Branch, snapshot.HeadCommit, selected, cancellation.Token));
            _branchInspectionTasks.Add(inspection);
            var comparison = await inspection;
            if (version != _branchInspectionVersion) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _selectedBranchComparison = comparison;
            _branchInspectionStatus = comparison.UnavailableReason ?? "Branch history unavailable.";
        }
        catch (Exception exception)
        {
            if (exception is GitCommandExitUnconfirmedException unconfirmed)
            {
                HoldUnconfirmedBranchCommand(unconfirmed);
            }
            if (version != _branchInspectionVersion) return;
            _branchInspectionStatus = "History unavailable. Refresh local status and select the branch again.";
            if (exception is not GitCommandExitUnconfirmedException) ReportError(SafeError(exception));
        }
        finally
        {
            if (inspection != null) _branchInspectionTasks.Remove(inspection);
            if (version == _branchInspectionVersion)
            {
                _branchInspectionCancellation = null;
                _branchInspectionLoading = false;
                Changed(nameof(CanShowBranchHistory));
                Changed(nameof(SelectedBranchComparisonLabel));
                Changed(nameof(SelectedBranchComparisonDetail));
            }
        }
    }

    private void BranchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!CanShowBranchHistory || _selectedBranchComparison is not { Available: true } comparison) return;
        var history = new GitBranchHistoryWindow(comparison) { Owner = this };
        history.CommandExitUnconfirmed += HoldUnconfirmedBranchCommand;
        history.ShowDialog();
    }

    private void HoldUnconfirmedBranchCommand(GitCommandExitUnconfirmedException command)
    {
        if (ReferenceEquals(_unconfirmedGitCommand, command)) return;
        _unconfirmedGitCommand = command;
        ResetBranchInspection();
        FailGitLoad("Git may still be running. Wait, then Retry to check again.");
        ReportError(SafeError(command));
        Changed();
    }

    private async Task<bool> DrainBranchInspectionAsync()
    {
        ResetBranchInspection();
        foreach (var inspection in _branchInspectionTasks.ToArray())
        {
            try { await inspection; }
            catch (GitCommandExitUnconfirmedException command) { HoldUnconfirmedBranchCommand(command); }
            catch { /* A canceled or failed read has already reported its active selection's outcome. */ }
            finally { _branchInspectionTasks.Remove(inspection); }
        }
        return _unconfirmedGitCommand == null;
    }
}
