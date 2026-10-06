using System.Windows;

namespace FullStackLauncher;

public partial class ServiceTypeWindow : Window
{
    public bool? IsApi { get; private set; }

    public ServiceTypeWindow(string workingDirectory)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        FolderText.Text = workingDirectory;
        FolderText.ToolTip = workingDirectory;
        Loaded += (_, _) => ConsoleAppButton.Focus();
    }

    private void ConsoleApp_Click(object sender, RoutedEventArgs e)
    {
        IsApi = false;
        DialogResult = true;
    }

    private void Api_Click(object sender, RoutedEventArgs e)
    {
        IsApi = true;
        DialogResult = true;
    }
}
