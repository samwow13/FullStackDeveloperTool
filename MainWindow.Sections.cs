using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void InitializeSectionsMenu()
    {
        Deactivated += (_, _) => SectionsMenuToggle.IsChecked = false;
        LocationChanged += (_, _) => SectionsMenuToggle.IsChecked = false;
        SizeChanged += (_, _) => RepositionSectionsMenu();
        Closed += (_, _) => SectionsMenuToggle.IsChecked = false;
    }

    private void SectionsMenu_Opened(object sender, EventArgs e) => ProjectsToggle.Focus();

    private void SectionsMenuToggle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down && e.Key != Key.Enter) return;
        SectionsMenuToggle.IsChecked = true;
        e.Handled = true;
    }

    private void SectionsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SectionsMenuToggle.IsChecked = false;
        SectionsMenuToggle.Focus();
        e.Handled = true;
    }

    private void RepositionSectionsMenu()
    {
        if (!SectionsMenu.IsOpen) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!SectionsMenu.IsOpen || _closed) return;
            // Reevaluate popup placement after the sidebar or commit panel moves.
            var offset = SectionsMenu.HorizontalOffset;
            SectionsMenu.HorizontalOffset = offset + 1;
            SectionsMenu.HorizontalOffset = offset;
        }));
    }
}
