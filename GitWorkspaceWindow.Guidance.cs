using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    public string ChangedFilesLabel => _comparison is { LineTotalsAvailable: true } comparison
        ? $"{comparison.ChangedFiles:N0}" : "—";
    public string AddedLinesLabel => _comparison is { LineTotalsAvailable: true } comparison ? $"+{comparison.AddedLines:N0}" : "—";
    public string RemovedLinesLabel => _comparison is { LineTotalsAvailable: true } comparison ? $"−{comparison.DeletedLines:N0}" : "—";
    public string LineTotalsTooltip => "Net tracked changes against the last fetched release branch, including local commits and edits. Changes already matching release do not count. Fetch after online integration to update totals. Untracked files and binary contents are excluded from line totals.";
    public string ComparisonLabel => _comparison is { Available: true } comparison
        ? $"Release comparison · {comparison.Remote}/{comparison.Branch} · last fetched"
        : "Release comparison";
    public string ComparisonDetail
    {
        get
        {
            if (!IsRepository) return "Select or initialize a repository.";
            if (_comparison is not { LineTotalsAvailable: true } comparison)
                return _comparisonError ?? _comparison?.UnavailableReason
                    ?? (SelectedRemote == null ? "Connect a remote to compare lines." : "Choose a release branch in Config, then fetch to compare changes.");
            var detail = $"{Changes.Count:N0} uncommitted files";
            if (comparison.UntrackedFiles > 0) detail += $" · {comparison.UntrackedFiles:N0} untracked (lines excluded)";
            if (comparison.BinaryFiles > 0) detail += $" · {comparison.BinaryFiles:N0} binary (lines excluded)";
            return detail;
        }
    }
}
