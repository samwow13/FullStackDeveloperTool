using System.ComponentModel;
using System.Windows;
using FullStackLauncher.Models;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectTasksWindow : Window
{
    private readonly ProjectTasksViewModel _model = new();

    public ProjectTasksWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
    }

    public void ShowProject(ProjectProfile? project, string folder) => _model.ShowProject(project, folder);

    public bool PrepareToClose()
    {
        if (!_model.HasDrafts) return true;
        var result = MessageBox.Show(this, "Save all unsaved note drafts before closing? Choose No to discard the drafts. Saved notes and queue items are retained.",
            "Unsaved project notes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return _model.SaveAllDrafts();
        _model.DiscardAllDrafts();
        return true;
    }

    public bool PrepareToRemoveProject(string projectId)
    {
        if (!_model.HasDraftsForProject(projectId)) return true;
        var result = MessageBox.Show(this, "Save this project's unsaved note drafts before removing its profile? Saved notes stay in local storage under the original project ID. Choose No to discard these drafts.",
            "Unsaved project notes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return _model.SaveProjectDrafts(projectId);
        _model.DiscardProjectDrafts(projectId);
        return true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = !PrepareToClose();
}
