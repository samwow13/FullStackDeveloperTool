using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void StatisticsToggle_Click(object sender, RoutedEventArgs e)
    {
        // Keep the statistics button independent of the enclosing service expander.
        e.Handled = true;
    }

    private async void StatisticsPopup_Opened(object sender, System.EventArgs e)
    {
        if (sender is Popup { Child: UIElement content })
            content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        if (sender is Popup { DataContext: ServiceViewModel { ShowApiDatabaseLabel: true } service } &&
            (!service.ApiEndpoints.HasLoaded || service.ApiEndpoints.NeedsBranch))
            await CountApiEndpointsAsync(service);
    }

    private async void SelectStatisticsBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closed || _closeRequested || IsEditing ||
            ServiceFrom(sender) is not { ShowApiDatabaseLabel: true } service) return;
        var project = Projects.FirstOrDefault(candidate =>
            _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
        if (project is null) return;
        var popup = (sender as FrameworkElement)?.Tag as Popup;
        try
        {
            popup?.SetCurrentValue(Popup.IsOpenProperty, false);
            new GitWorkspaceWindow(project.Name, [new GitWorkspaceFolder(service.Name, service.Directory)])
                { Owner = this }.ShowDialog();
            await RefreshProjectBranchesAsync();
            await RefreshNextCommitAsync();
            if (!_closing && !_closed && !_closeRequested && ReferenceEquals(SelectedProject, project))
                popup?.SetCurrentValue(Popup.IsOpenProperty, true);
            await CountApiEndpointsAsync(service);
        }
        catch (Exception)
        {
            service.ApiEndpoints.Fail("Git could not open. Check the API folder and retry.");
        }
        e.Handled = true;
    }

    private void CloseStatistics_Click(object sender, RoutedEventArgs e)
    {
        CloseStatistics(sender);
        e.Handled = true;
    }

    private void StatisticsPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseStatistics(sender);
        e.Handled = true;
    }

    private static void CloseStatistics(object sender)
    {
        if (sender is not FrameworkElement { Tag: Popup popup }) return;
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        popup.PlacementTarget?.Focus();
    }
}
