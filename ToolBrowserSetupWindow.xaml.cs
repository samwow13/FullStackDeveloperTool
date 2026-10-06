using System.Diagnostics;
using System.Windows;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher;

public partial class ToolBrowserSetupWindow : Window
{
    private ToolBrowserSetupWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        FolderText.Text = BrowserCaptureExtensionPackage.EnsureExtracted();
        PairingCodeText.Text = BrowserPageCaptureBroker.GetPairingCode();
        Closed += (_, _) => PairingCodeText.Clear();
    }

    public static void Show(Window owner) => new ToolBrowserSetupWindow { Owner = owner }.ShowDialog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(FolderText.Text) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { Report("The extension folder could not open. Copy its path instead."); }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(PairingCodeText.Text); Report("Pairing code copied."); }
        catch (Exception) { Report("Clipboard is unavailable. Select and copy the pairing code instead."); }
    }

    private void Report(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}
