using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void ServiceFunctionsSubmenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || _closed || _closeRequested ||
            sender is not Button { CommandParameter: Popup functions, Tag: Popup submenu } ||
            functions.PlacementTarget is not FrameworkElement { DataContext: ServiceViewModel { ShowFunctionsMenu: true } service } anchor ||
            !ReferenceEquals(submenu.DataContext, service)) return;

        // Close the parent first so the two dismissible popups never compete for capture.
        functions.SetCurrentValue(Popup.IsOpenProperty, false);
        submenu.SetCurrentValue(Popup.PlacementTargetProperty, anchor);
        submenu.SetCurrentValue(Popup.IsOpenProperty, true);
    }

    private void ServiceFunctionsOpenWith_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || _closed || _closeRequested ||
            sender is not Button { CommandParameter: Popup functions } ||
            functions.PlacementTarget is not FrameworkElement { DataContext: ServiceViewModel { ShowFunctionsMenu: true } } anchor) return;

        functions.SetCurrentValue(Popup.IsOpenProperty, false);
        anchor.Focus();
        OpenServiceTool_Click(anchor, e);
    }
}
