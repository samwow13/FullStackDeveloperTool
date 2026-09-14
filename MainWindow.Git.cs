using System.Windows;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async void GitWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProjectItem is not { } project || IsEditing || _closeRequested || _closing) return;
        try
        {
            var folders = BranchFolders(project)
                .GroupBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => new GitWorkspaceFolder(string.Join(" / ", group.Select(folder => folder.Label)), group.Key))
                .ToArray();
            new GitWorkspaceWindow(project.Name, folders) { Owner = this }.ShowDialog();
            await RefreshProjectBranchesAsync();
        }
        catch (Exception)
        {
            Notice = "The Git workspace could not open. Check the configured project folders and try again.";
        }
    }
}
