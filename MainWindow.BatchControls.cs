using System.Windows;
using System.Windows.Controls;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void BatchControlsLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid layout || BatchStatusGroup is null || BatchActionsGroup is null) return;

        // Keep the status centered without overlapping actions when the workspace narrows.
        var stacked = layout.ActualWidth < 860;
        Grid.SetRow(BatchStatusGroup, stacked ? 1 : 0);
        BatchStatusGroup.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        Grid.SetRow(BatchActionsGroup, stacked ? 2 : 0);
        Grid.SetColumn(BatchActionsGroup, stacked ? 0 : 2);
        Grid.SetColumnSpan(BatchActionsGroup, stacked ? 3 : 1);
        BatchActionsGroup.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }
}
