using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectTasksWindow : Window
{
    private readonly ProjectTasksViewModel _model;
    private readonly DispatcherTimer _ownerStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _pollingOwner;

    public ProjectTasksWindow(Func<string, IReadOnlyList<ProjectApplicationState>>? applicationStates = null)
    {
        _model = new ProjectTasksViewModel(applicationStates);
        InitializeComponent();
        DataContext = _model;
        _ownerStatusTimer.Tick += async (_, _) => await RefreshOwnerStatusAsync();
        Loaded += async (_, _) =>
        {
            _ownerStatusTimer.Start();
            await Task.WhenAll(RefreshOwnerStatusAsync(), _model.LoadModelsAsync());
        };
        Closed += (_, _) => { _ownerStatusTimer.Stop(); _model.Dispose(); };
    }

    public void ShowProject(ProjectProfile? project, string folder)
    {
        _model.ShowProject(project, folder);
        _ = RefreshOwnerStatusAsync();
    }

    private async Task RefreshOwnerStatusAsync()
    {
        if (_pollingOwner) return;
        _pollingOwner = true;
        try { await _model.RefreshOwnerStatusAsync(); }
        finally { _pollingOwner = false; }
    }

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
