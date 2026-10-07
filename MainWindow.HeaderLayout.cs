using System.Windows;
using System.Windows.Controls;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void WorkspaceHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkspaceProjectTitle is null || WorkspaceHeaderActions is null) return;

        // Keep every menu reachable when the Git dock narrows the workspace.
        var stacked = WorkspaceHeader.ActualWidth < 720;
        Grid.SetColumnSpan(WorkspaceProjectTitle, stacked ? 2 : 1);
        Grid.SetRow(WorkspaceHeaderActions, stacked ? 1 : 0);
        Grid.SetColumn(WorkspaceHeaderActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(WorkspaceHeaderActions, stacked ? 2 : 1);
        WorkspaceHeaderActions.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        RepositionSectionsMenu();
    }
}
