using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    public string ChangedFilesLabel => !IsRepository ? "—"
        : _comparison is { Available: true } comparison
            ? $"{comparison.ChangedFiles + comparison.UntrackedFiles:N0}" : $"{Changes.Count:N0}";
    public string AddedLinesLabel => _comparison is { Available: true } comparison ? $"+{comparison.AddedLines:N0}" : "—";
    public string RemovedLinesLabel => _comparison is { Available: true } comparison ? $"−{comparison.DeletedLines:N0}" : "—";
    public string ComparisonLabel => _comparison is { Available: true } comparison
        ? $"Compared with {comparison.Remote}/{comparison.Branch} · last fetched"
        : "Local changes · remote comparison unavailable";
    public string ComparisonDetail
    {
        get
        {
            if (!IsRepository) return "Select or initialize a repository.";
            if (_comparison is not { Available: true } comparison)
                return _comparisonError ?? _comparison?.UnavailableReason
                    ?? (SelectedRemote == null ? "Connect a remote to compare lines." : "Refresh or fetch to load the remote comparison.");
            var detail = $"{Changes.Count:N0} uncommitted files · {comparison.Ahead:N0} commits ahead / {comparison.Behind:N0} behind";
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
        catch (Exception ex)
        {
            _comparisonError = ex is OperationCanceledException ? "Comparison canceled or timed out. Refresh to retry."
                : "Could not read remote comparison. See Activity for details.";
            AddActivity("Read comparison", snapshot.RepositoryRoot!, SafeError(ex));
        }
        Changed();
    }
}
