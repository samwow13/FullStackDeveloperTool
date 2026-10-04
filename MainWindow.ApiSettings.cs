using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void ServiceMenuToggle_Click(object sender, RoutedEventArgs e) => e.Handled = true;

    private void ServiceMenuPopup_Opened(object sender, System.EventArgs e)
    {
        if (sender is Popup { Child: UIElement content })
            content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ServiceMenuPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseServiceMenu(sender);
        e.Handled = true;
    }

    private void CloseServiceMenu_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        e.Handled = true;
    }

    private void ApiSettingsLaunchProfile_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        EditApiLaunchProfile_Click(sender, e);
        e.Handled = true;
    }

    private void ApiSettingsSecrets_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        ApiSecrets_Click(sender, e);
        e.Handled = true;
    }

    private void ApiSettingsUseLocal_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        UseApiConfiguration_Click(sender, e);
        e.Handled = true;
    }

    private void ApiSettingsClean_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        CleanService_Click(sender, e);
        e.Handled = true;
    }

    private void ApiSettingsSetup_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        SetupService_Click(sender, e);
        e.Handled = true;
    }

    private void ConsoleMenuExpand_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        ExpandServiceConsole_Click(sender, e);
        e.Handled = true;
    }

    private void ConsoleMenuCopy_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        CopyServiceConsole_Click(sender, e);
        e.Handled = true;
    }

    private void ConsoleMenuClear_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        ClearServiceConsole(sender, e);
        e.Handled = true;
    }

    private void ConsoleMenuCopyErrors_Click(object sender, RoutedEventArgs e)
    {
        var anchor = (sender as Button)?.CommandParameter is Popup popup ? popup.PlacementTarget : null;
        CloseServiceMenu(sender);
        // Feedback must use the visible header after the menu's content is hidden.
        CopyServiceErrors_Click(anchor ?? sender, e);
        e.Handled = true;
    }

    private static void CloseServiceMenu(object sender)
    {
        var popup = sender switch
        {
            Button { CommandParameter: Popup actionPopup } => actionPopup,
            FrameworkElement { Tag: Popup taggedPopup } => taggedPopup,
            _ => null
        };
        if (popup is null) return;
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        popup.PlacementTarget?.Focus();
    }
}
