using System.Windows;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<ServiceViewModel, ApiEndpointsWindow> _apiEndpointWindows = [];

    private void ApiEndpoints_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closeRequested || ServiceFrom(sender) is not { ShowApiDatabaseLabel: true } service) return;
        if (_apiEndpointWindows.TryGetValue(service, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var project = Projects.FirstOrDefault(candidate =>
            _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
        if (project is null) return;
        var window = new ApiEndpointsWindow(project.Name, service.Name, service.Directory) { Owner = this };
        window.InventoryChanged += inventory =>
        {
            if (!_closed && Projects.Contains(project) &&
                _runners.GetValueOrDefault(project.Id)?.Contains(service) == true &&
                service.ShowApiDatabaseLabel &&
                string.Equals(service.Directory, window.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                service.ApiEndpoints.SetInventory(inventory);
        };
        window.Closed += (_, _) => _apiEndpointWindows.Remove(service);
        _apiEndpointWindows.Add(service, window);
        window.Show();
    }

    private void RefreshApiEndpointWindows()
    {
        foreach (var (service, window) in _apiEndpointWindows.ToArray())
        {
            var project = Projects.FirstOrDefault(candidate =>
                _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
            if (project is null || !service.ShowApiDatabaseLabel ||
                !string.Equals(service.Directory, window.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                service.ApiEndpoints.SetInventory(null);
                window.Close();
            }
            else window.UpdateContext(project.Name, service.Name);
        }
    }
}
