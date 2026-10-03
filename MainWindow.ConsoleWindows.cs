using System.Windows;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<ServiceViewModel, ServiceConsoleWindow> _serviceConsoleWindows = [];

    private void RefreshServiceConsoleWindows()
    {
        foreach (var (service, window) in _serviceConsoleWindows.ToArray())
        {
            var project = Projects.FirstOrDefault(candidate =>
                _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
            if (project is null) window.Close();
            else window.UpdateContext(project.Name, service.Name);
        }
    }

    private void ExpandServiceConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closeRequested || ServiceFrom(sender) is not { } service) return;
        if (_serviceConsoleWindows.TryGetValue(service, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var project = Projects.FirstOrDefault(candidate =>
            _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
        if (project is null) return;
        var window = new ServiceConsoleWindow(project.Name, service.Name, service.ConsoleLines) { Owner = this };
        window.ClearRequested += (_, _) => ClearServiceOutput(service);
        window.Closed += (_, _) => _serviceConsoleWindows.Remove(service);
        _serviceConsoleWindows.Add(service, window);
        window.Show();
    }
}
