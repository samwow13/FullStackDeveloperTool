using System.Windows;
using System.Windows.Controls;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void ServiceCardHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid header) return;
        var functions = header.Children.OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Name == "ServiceHeaderFunctionsGroup");
        var launch = header.Children.OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Name == "ServiceHeaderLaunchGroup");
        if (functions is null || launch is null) return;

        // Keep both launch controls together when the name and menus need the full row.
        var compact = header.ActualWidth < 520 && launch is Panel group &&
            group.Children.OfType<Button>().Any(button => button.Visibility == Visibility.Visible);
        if (launch is Panel launchGroup && launchGroup.Children.OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Name == "ServiceHeaderStatusGroup") is { } status)
            status.MaxWidth = Math.Max(160, Math.Min(360, compact ? header.ActualWidth - 160 : header.ActualWidth * 0.45));
        Grid.SetColumnSpan(functions, compact ? 2 : 1);
        Grid.SetRow(launch, compact ? 1 : 0);
        Grid.SetColumn(launch, compact ? 0 : 1);
        Grid.SetColumnSpan(launch, compact ? 2 : 1);
        launch.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0);
    }
}
