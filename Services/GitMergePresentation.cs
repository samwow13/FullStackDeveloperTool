using System.Text;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static class GitMergePresentation
{
    public static string FormatComparisonSummary(GitMergeCheckResult result)
    {
        if (result.IncomingCommits == 0)
            return "0 incoming commits.\nThe selected source is already included. No merge or checkpoint is needed.";
        var paths = result.ConflictedPaths.Count == 0 ? ""
            : "\n\n" + string.Join("\n", result.ConflictedPaths.Select(DisplayPath));
        var diagnostics = string.IsNullOrWhiteSpace(result.GitDiagnostics) ? "" : "\n\n" + result.GitDiagnostics;
        return SensitiveDataProtection.Redact($"{result.IncomingCommits:N0} incoming commit(s).\n{result.Message}{paths}{diagnostics}\n\nCommitted history only. Uncommitted edits and submodule contents are excluded."
            + (result.HasUncommittedChanges ? " The local checkpoint may produce additional conflicts." : ""));
    }

    public static string FormatComparisonDetails(string root, GitMergeCheckResult result, bool reviewLocalMerge = true)
    {
        var paths = result.ConflictedPaths.Count > 0
            ? "\n\nConflicting paths (relative to this repository):\n" + string.Join("\n", result.ConflictedPaths.Select(path => "• " + DisplayPath(path))) : "";
        var diagnostics = string.IsNullOrWhiteSpace(result.GitDiagnostics) ? "" : "\n\nGit details:\n" + result.GitDiagnostics;
        var scope = reviewLocalMerge && result.IncomingCommits == 0
            ? "\n\nUncommitted edits are preserved. The current branch already includes the selected source commit; this action does not checkpoint or merge those edits. Later remote updates require another comparison."
            : result.HasUncommittedChanges
            ? "\n\nThis repository has uncommitted changes. They were excluded. An actual merge checkpoints those changes first and may produce different conflicts."
            : "\n\nThis result covers only the exact commits above. Later local edits or release updates can change the outcome.";
        return SensitiveDataProtection.Redact($"Repository:\n{root}\n\nCurrent branch: {result.CurrentBranch}\nCurrent commit: {result.CurrentCommit}\nRemote source: {result.Remote}/{result.SourceBranch}\nSource commit: {result.SourceCommit}\nIncoming commits: {result.IncomingCommits:N0}\nChecked: {result.CheckedAt.ToLocalTime():g}\n\n{result.Message}{paths}{diagnostics}{scope}\n\nOnly Git history was checked. No application build or tests ran. Submodule contents were not checked.");
    }

    public static string BuildResolutionPrompt(string root, IReadOnlyList<string> paths, string recovery,
        string? currentBranch = null, string? sourceBranch = null, string? sourceCommit = null)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Resolve the remaining Git merge conflicts in this local repository. Keep the result local for my review.");
        prompt.AppendLine();
        prompt.AppendLine($"Repository: {EscapeContext(root)}");
        if (!string.IsNullOrWhiteSpace(currentBranch)) prompt.AppendLine($"Current local branch: {EscapeContext(currentBranch)}");
        if (!string.IsNullOrWhiteSpace(sourceBranch)) prompt.AppendLine($"Incoming source branch: {EscapeContext(sourceBranch)}");
        if (!string.IsNullOrWhiteSpace(sourceCommit)) prompt.AppendLine($"Incoming source commit: {EscapeContext(sourceCommit)}");
        prompt.AppendLine();
        prompt.AppendLine("Conflicted paths captured by Full Stack Launcher, relative to the repository:");
        foreach (var path in paths) prompt.AppendLine($"- {EscapeContext(path)}");
        prompt.AppendLine();
        prompt.AppendLine("Launcher recovery details:");
        prompt.AppendLine(recovery);
        prompt.AppendLine();
        prompt.AppendLine("Read the repository instructions first. Inspect git status --short, git ls-files --unmerged, the current HEAD and MERGE_HEAD, and any existing Launcher merge recovery record before editing. The paths and recovery details above are captured context; confirm the actual current conflicts. If the repository, branch or merge source differs, or an operation is still running or uncertain, stop and explain before changing files.");
        prompt.AppendLine();
        prompt.AppendLine("Preserve unrelated edits, staged changes, untracked files and local checkpoint commits. Review both sides and the merge base for each conflict, then combine the intended behavior. Do not select ours or theirs wholesale. Resolve all actual conflicts, including rename/delete conflicts, and review the resulting diff. This request authorizes editing conflicted files and staging only the resolved paths; do not stage unrelated changes with git add --all.");
        prompt.AppendLine();
        prompt.AppendLine("Check that no unmerged entries or unresolved conflict markers remain. Run the appropriate focused builds or checks permitted by the repository instructions. Do not create or run automated tests when repository policy defers them. Do not commit, push, abort the merge, reset, clean, stash, switch branches or discard work without my explicit authorization. Keep the Launcher recovery records intact.");
        prompt.AppendLine();
        prompt.Append("Report the resolved paths, behavior preserved, checks and any remaining limitations or decisions. Leave the merge ready for local review, then tell me to refresh the Git workspace in Full Stack Launcher. Publication requires my later review.");
        return SensitiveDataProtection.Redact(prompt.ToString());
    }

    private static string DisplayPath(string path) => path.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    private static string EscapeContext(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
