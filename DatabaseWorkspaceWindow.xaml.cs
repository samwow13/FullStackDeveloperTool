using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class DatabaseWorkspaceWindow : Window
{
    private readonly ProjectProfile _project;
    private readonly ServiceViewModel _service;
    private readonly ProjectProfile[] _projects;
    private readonly SettingsStore _store;
    private readonly Func<ProjectProfile, DatabaseSelection, string?> _saveSelection;
    private bool _loaded;

    public DatabaseWorkspaceWindow(ProjectProfile project, ServiceViewModel service, ProjectProfile[] projects,
        SettingsStore store, Func<ProjectProfile, DatabaseSelection, string?> saveSelection)
    {
        _project = project;
        _service = service;
        _projects = projects;
        _store = store;
        _saveSelection = saveSelection;
        InitializeComponent();
        Title = $"{project.Name} · Database explorer";
        ContextHeading.Text = $"{project.Name} / {service.Name} · Database explorer";
        Browser.DatabaseSelected += DatabaseSelected;
        Browser.HideRequested += CloseRequested;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await Browser.ShowProjectAsync(_project, _projects, _store, _service);
    }

    private void DatabaseSelected(ProjectProfile project, DatabaseSelection selection)
    {
        var error = _saveSelection(project, selection);
        SaveNotice.Text = error ?? "";
        SaveNotice.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseRequested(object? sender, EventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        Browser.CancelPending();
        Browser.DatabaseSelected -= DatabaseSelected;
        Browser.HideRequested -= CloseRequested;
    }
}
