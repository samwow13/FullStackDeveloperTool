using System.IO;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class AzureDevOpsImportWindow
{
    private string _newWorkspaceParent = "";
    private string _selectedExistingWorkspace = "";

    private void RestoreDestinationFolder()
    {
        _updatingSelections = true;
        try { DestinationFolderBox.Text = UsesExistingWorkspace ? _selectedExistingWorkspace : _newWorkspaceParent; }
        finally { _updatingSelections = false; }
    }

    private void DestinationFolder_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        if (UsesExistingWorkspace) _selectedExistingWorkspace = DestinationFolderBox.Text;
        else _newWorkspaceParent = DestinationFolderBox.Text;
        ChangeDraft();
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || _existingWorkspaceFolder is not null) return;
        var initial = _newWorkspaceParent;
        try { initial = ResolveDestinationFolder(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException or IOException) { }
        if (!Directory.Exists(initial)) initial = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(initial)) initial = _settingsDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = UsesExistingWorkspace ? "Choose your existing workspace" : "Choose the location for your new workspace",
            InitialDirectory = initial,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) DestinationFolderBox.Text = dialog.FolderName;
    }

    private string ResolveDestinationFolder()
    {
        var folder = _existingWorkspaceFolder ?? DestinationFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Choose a workspace location.");
        if (!Path.IsPathFullyQualified(folder)) throw new InvalidOperationException("Enter a full folder path, or choose Browse.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    private bool DestinationMatchesReview(GitWorkspaceCloneReview review)
    {
        try { return string.Equals(ResolveDestinationFolder(), Path.TrimEndingDirectorySeparator(review.ParentFolder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException or IOException) { return false; }
    }

    private void UpdateDestinationControls(bool navigate)
    {
        var existing = UsesExistingWorkspace;
        DestinationFolderLabel.Content = existing ? "Workspace _folder" : "Parent _folder";
        DestinationFolderBox.IsEnabled = navigate && _existingWorkspaceFolder is null && _page == ImportPage.Workspace;
        BrowseDestinationButton.IsEnabled = DestinationFolderBox.IsEnabled;
        System.Windows.Automation.AutomationProperties.SetName(DestinationFolderBox,
            existing ? "Existing workspace folder" : "Parent folder for new workspace");
    }
}
