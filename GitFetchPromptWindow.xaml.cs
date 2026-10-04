using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitFetchPromptWindow : Window
{
    public GitFetchPromptWindow(GitRemoteInfo remote)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        RemoteName.Text = remote.Name;
        RemoteTarget.Text = SensitiveDataProtection.Redact(remote.FetchUrl);
        RemoteTarget.ToolTip = RemoteTarget.Text;
        Loaded += (_, _) => NoButton.Focus();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
