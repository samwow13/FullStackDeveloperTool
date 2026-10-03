using System.Diagnostics;
using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitConflictWindow : Window
{
    private readonly string _root;
    public GitConflictWindow(string root, IReadOnlyList<string> paths, string recovery)
    {
        _root = root;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Heading.Text = paths.Count > 0 ? $"Resolve {paths.Count:N0} conflicted file(s) to complete the local merge" : "Review the local merge before continuing";
        var fileList = paths.Count > 0 ? "\n\nConflicted files (relative to the repository above):\n" + string.Join("\n", paths.Select(path => "• " + path.Replace("\r", "\\r").Replace("\n", "\\n"))) : "";
        var steps = paths.Count > 0 ? "\n\nOpen these files in your editor, resolve each conflict, save your edits and stage the resolved files with Git. Return to this workspace and refresh. The merge remains local. When you want to publish your work, use Commit all & push to review the final commit and destination. If the recovery notice requires it, finish or abort the merge with Git, then refresh." : "";
        Details.Text = SensitiveDataProtection.Redact($"Repository:\n{root}\n\n{recovery}{fileList}{steps}\n\nThis window does not resolve files or push anything. Local checkpoint commits remain available in Git history.");
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(_root);
            Process.Start(start);
        }
        catch { MessageBox.Show(this, "The repository folder could not open. Copy the details to use its path in your editor.", "Open repository", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Details.Text); }
        catch { MessageBox.Show(this, "The clipboard is busy. Try again.", "Copy details", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
