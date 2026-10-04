using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void FirstTimeSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _closing || _resettingCodex) return;
        try
        {
            var content = FirstTimeSetupContent.Create(_store.SettingsPath,
                SelectedProject is null ? null : _store.ResolveRoot(SelectedProject));
            new FirstTimeSetupWindow(content) { Owner = this }.ShowDialog();
        }
        catch (Exception)
        {
            Notice = "First Time Setup could not open. Check that the launcher is running from an existing application folder.";
        }
    }
}
