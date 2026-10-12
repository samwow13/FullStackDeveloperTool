using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class LanComputersWindow
{
    private void NetworkSettings_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkSettingsPanel.Visibility != Visibility.Visible)
        {
            ShowNetworkSettings();
            return;
        }

        NetworkSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkSettingsButton.Content = "Network ▾";
        NetworkSettingsButton.Focus();
    }

    private void ShowNetworkSettings()
    {
        NetworkSettingsPanel.Visibility = Visibility.Visible;
        NetworkSettingsButton.Content = "Network ▴";
        if (!_busy) NetworkBox.Focus();
    }

    private void NetworkSettings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || NetworkBox.IsDropDownOpen) return;
        NetworkSettings_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void AddComputerToggle_Click(object sender, RoutedEventArgs e) =>
        SetAddComputerVisible(AddComputerPanel.Visibility != Visibility.Visible);

    private void SetAddComputerVisible(bool visible)
    {
        AddComputerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddComputerToggle.Content = visible ? "Close" : "+ Add PC";
        if (visible)
        {
            CompanionSetupExpander.IsExpanded = false;
            HostBox.Focus();
        }
        else AddComputerToggle.Focus();
    }

    private void CompanionSetup_Expanded(object sender, RoutedEventArgs e)
    {
        // Keep room for the list at small window sizes without discarding the address draft.
        if (!_ready || AddComputerPanel.Visibility != Visibility.Visible) return;
        SetAddComputerVisible(false);
        CompanionSetupExpander.Focus();
    }

    private void AddComputer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SetAddComputerVisible(false);
        e.Handled = true;
    }

    private void CompanionOptions_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionOptionsButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = CompanionOptionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateRemotePresentation()
    {
        var computer = Selected;
        SelectionEmptyPanel.Visibility = computer == null ? Visibility.Visible : Visibility.Collapsed;
        SelectedComputerPanel.Visibility = computer != null ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        AddComputerToggle.IsEnabled = !_busy;
        LoginFields.Visibility = _session == null ? Visibility.Visible : Visibility.Collapsed;
        SystemInfoButton.Visibility = ProcessesButton.Visibility = _session != null ? Visibility.Visible : Visibility.Collapsed;

        var network = NetworkBox.SelectedItem as LanNetwork;
        NetworkSummaryText.Text = network == null ? "Choose a network" : $"{network.Name} · {network.LocalAddress}";
        NetworkSummaryText.ToolTip = network?.DisplayName;
        DesktopButton.ToolTip = computer?.IsLocal == true ? "Select another PC to open Remote Desktop."
            : computer?.RdpAvailable != true ? "Remote Desktop port 3389 was not detected. Enable Remote Desktop on that PC, then Refresh."
            : "Open Windows Remote Desktop. Windows prompts for credentials separately.";
        SharesButton.ToolTip = computer?.IsLocal == true ? "Select another PC to open shared folders."
            : computer?.SmbAvailable != true ? "File sharing port 445 was not detected. Check sharing on that PC, then Refresh."
            : "Open this PC in File Explorer. Windows handles share permissions and credentials.";
    }

    private void SetComputerResult(string title, string text, bool reveal = false)
    {
        ResultTitleText.Text = title;
        ResultBox.Text = text;
        if (!reveal) return;
        ResultsExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed && ResultsExpander.IsExpanded) ResultsExpander.BringIntoView();
        }));
    }
}
