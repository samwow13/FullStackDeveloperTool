using System.ComponentModel;
using System.Windows;

namespace FullStackLauncher.ProjectTasks;

public partial class CodexConnectionWindow : Window
{
    private readonly CodexConnectionViewModel _model;

    public CodexConnectionWindow(string projectId, string projectName, string folder)
    {
        InitializeComponent();
        _model = new(projectId, projectName, folder);
        DataContext = _model;
        Closing += OnClosing;
        Closed += (_, _) => _model.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_model.IsBusy) return;
        // Retain the window until stop/timeout has persisted the final or uncertain
        // result. A process crash leaves the pre-submission receipt for review.
        e.Cancel = true;
        await _model.StopAsync();
    }
}
