using System.Windows;

namespace FullStackLauncher;

public partial class RepositoryServicesWindow : Window
{
    public string? SelectedFolder { get; private set; }

    public RepositoryServicesWindow(string projectName, IReadOnlyList<string> repositories)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        ProjectText.Text = $"Repositories are saved in {projectName}'s workspace. Add their services now or finish and add them later.";
        RepositoryBox.ItemsSource = repositories;
        RepositoryBox.SelectedIndex = repositories.Count > 0 ? 0 : -1;
        Loaded += (_, _) => RepositoryBox.Focus();
    }

    private void AddService_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoryBox.SelectedItem is not string folder) return;
        SelectedFolder = folder;
        DialogResult = true;
    }
}
