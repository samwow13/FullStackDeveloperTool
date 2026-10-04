using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitMergeCheckWindow : Window
{
    private readonly string _root;

    public GitMergeCheckWindow(string root, GitMergeCheckResult result, bool reviewLocalMerge = false)
    {
        _root = root;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Heading.Text = result.HasConflicts
            ? result.ConflictedPaths.Count > 0 ? $"Git detects conflicts in {result.ConflictedPaths.Count:N0} file(s)" : "Git detects merge conflicts"
            : "No Git conflicts detected between these commits";
        Heading.Foreground = result.HasConflicts
            ? new SolidColorBrush(Color.FromRgb(255, 188, 139)) : (Brush)FindResource("AccentBrush");
        if (reviewLocalMerge)
        {
            Title = "Local merge comparison";
            if (result.IncomingCommits == 0)
            {
                Heading.Text = $"{result.CurrentBranch} is up to date with {result.Remote}/{result.SourceBranch}";
                Introduction.Text = "Your current branch already includes every commit from this remote source. No merge or checkpoint is needed. Return to Git and use Commit all & push when ready. Existing local edits are preserved.";
            }
            else
            {
                Introduction.Text = $"The remote source has {result.IncomingCommits:N0} commit(s) missing from your current branch. "
                    + (result.HasConflicts ? "Git found conflicts. Review the local merge only if you are ready to resolve them."
                        : "Git found no conflicts between the committed branches.")
                    + " Uncommitted edits are excluded. No merge has started. Review the next step before changing local files.";
                ReviewMerge.Visibility = Visibility.Visible;
                ReviewMerge.IsDefault = true;
                BackToGit.IsDefault = false;
            }
        }
        var paths = result.ConflictedPaths.Count > 0
            ? "\n\nConflicting paths (relative to this repository):\n" + string.Join("\n", result.ConflictedPaths.Select(path => "• " + DisplayPath(path))) : "";
        var diagnostics = string.IsNullOrWhiteSpace(result.GitDiagnostics) ? "" : "\n\nGit details:\n" + result.GitDiagnostics;
        var scope = reviewLocalMerge && result.IncomingCommits == 0
            ? "\n\nUncommitted edits are preserved. The current branch already includes the selected source commit; this action does not checkpoint or merge those edits. Later remote updates require another comparison."
            : result.HasUncommittedChanges
            ? "\n\nThis repository has uncommitted changes. They were excluded. An actual merge checkpoints those changes first and may produce different conflicts."
            : "\n\nThis result covers only the exact commits above. Later local edits or release updates can change the outcome.";
        Details.Text = SensitiveDataProtection.Redact($"Repository:\n{root}\n\nCurrent branch: {result.CurrentBranch}\nCurrent commit: {result.CurrentCommit}\nRemote source: {result.Remote}/{result.SourceBranch}\nSource commit: {result.SourceCommit}\nIncoming commits: {result.IncomingCommits:N0}\nChecked: {result.CheckedAt.ToLocalTime():g}\n\n{result.Message}{paths}{diagnostics}{scope}\n\nOnly Git history was checked. No application build or tests ran. Submodule contents were not checked.");
    }

    private void ReviewMerge_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static string DisplayPath(string path) => path.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(_root);
            Process.Start(start);
        }
        catch
        {
            MessageBox.Show(this, "The repository folder could not open. Copy the details to use its path in your editor.", "Open repository", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Details.Text); }
        catch { MessageBox.Show(this, "The clipboard is busy. Try again.", "Copy details", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
