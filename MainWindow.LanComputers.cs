using System.Windows;

namespace FullStackLauncher;

public partial class MainWindow
{
    private LanComputersWindow? _lanComputersWindow;

    private void LanComputers_Click(object sender, RoutedEventArgs e)
    {
        SectionsMenuToggle.IsChecked = false;
        if (_closed || _closing || _closeRequested) return;

        if (_lanComputersWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new LanComputersWindow { Owner = this };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_lanComputersWindow, window)) _lanComputersWindow = null;
            if (!_closed && !_closing && !_closeRequested) SectionsMenuToggle.Focus();
        };
        _lanComputersWindow = window;
        window.Show();
    }

    private void CloseLanComputersWorkspace()
    {
        var window = _lanComputersWindow;
        _lanComputersWindow = null;
        if (window?.IsVisible == true) window.Close();
    }
}
