using FullStackLauncher.Services;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    public string ChangedFilesLabel => !IsRepository ? "—"
        : _comparison is { LineTotalsAvailable: true } comparison
            ? $"{comparison.ChangedFiles + comparison.UntrackedFiles:N0}" : $"{Changes.Count:N0}";
    public string AddedLinesLabel => _comparison is { LineTotalsAvailable: true } comparison ? $"+{comparison.AddedLines:N0}" : "—";
    public string RemovedLinesLabel => _comparison is { LineTotalsAvailable: true } comparison ? $"−{comparison.DeletedLines:N0}" : "—";
    public string LineTotalsTooltip => _comparison is { IsLocalComparison: true }
        ? "Tracked text compared with the last local commit (HEAD). Includes staged and unstaged edits; excludes untracked and binary file contents."
        : "Tracked text in your working tree compared with the last fetched remote branch. Includes local commits and edits; excludes untracked and binary file contents.";
    public string ComparisonLabel => _comparison is { IsLocalComparison: true }
        ? "Local changes · since last local commit"
        : _comparison is { Available: true } comparison
        ? $"Compared with {comparison.Remote}/{comparison.Branch} · last fetched"
        : "Local changes · remote comparison unavailable";
    public string ComparisonDetail
    {
        get
        {
            if (!IsRepository) return "Select or initialize a repository.";
            if (_comparison is not { LineTotalsAvailable: true } comparison)
                return _comparisonError ?? _comparison?.UnavailableReason
                    ?? (SelectedRemote == null ? "Connect a remote to compare lines." : "Refresh or fetch to load the remote comparison.");
            var detail = $"{Changes.Count:N0} uncommitted files";
            detail += comparison.Available
                ? $" · {comparison.Ahead:N0} commits ahead / {comparison.Behind:N0} behind"
                : " · remote branch unavailable";
            if (comparison.UntrackedFiles > 0) detail += $" · {comparison.UntrackedFiles:N0} untracked (lines excluded)";
            if (comparison.BinaryFiles > 0) detail += $" · {comparison.BinaryFiles:N0} binary (lines excluded)";
            return detail;
        }
    }

    private async Task RefreshComparisonAsync(CancellationToken token)
    {
        _comparison = null;
        _comparisonError = null;
        if (_snapshot is not { IsRepository: true, IsDetached: false } snapshot || SelectedRemote is not { } remote)
        {
            Changed();
            return;
        }
        try
        {
            _comparison = await GitRepositoryService.ReadComparisonAsync(snapshot.RepositoryRoot!, remote.Name, snapshot.Branch, token);
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (Exception ex)
        {
            _comparisonError = ex is OperationCanceledException ? "Comparison canceled or timed out. Refresh to retry."
                : "Could not read remote comparison. Review error details below.";
            ReportError(SafeError(ex));
        }
        Changed();
    }
}
