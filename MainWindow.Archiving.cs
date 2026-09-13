using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _showArchivedProjects;
    private bool _refreshingProjectList;

    public bool ShowArchivedProjects => _showArchivedProjects;
    public string ArchivedProjectsLabel => $"Archived ({Projects.Count(project => project.IsArchived)})";
    public string ArchiveProjectLabel => SelectedProject?.IsArchived == true ? "Unarchive selected project" : "Archive selected project";
    public bool CanArchiveProject => !IsEditing && CanEdit;
    public string ProjectListHint
    {
        get
        {
            var visibleCount = Projects.Count(project => project.IsArchived == ShowArchivedProjects);
            if (ShowArchivedProjects)
                return visibleCount == 0 ? "No archived projects. Turn off Archived to return to active projects."
                    : "Archived projects stay saved. Select one to restore it or manage its commands.";
            var runningArchived = Projects.Count(project => project.IsArchived &&
                _runners.GetValueOrDefault(project.Id)?.Any(service => service.Runner.HasManagedProcess ||
                    service.Runner.Snapshot.ProcessIds.Count > 0) == true);
            if (runningArchived > 0)
                return $"{runningArchived} archived project(s) have tracked processes. Open Archived to view or stop them.";
            return visibleCount == 0 ? "No active projects. Add a project or open Archived to restore one." : "";
        }
    }

    private void UpdateArchiveActions()
    {
        Changed(nameof(ShowArchivedProjects));
        Changed(nameof(ArchivedProjectsLabel));
        Changed(nameof(ArchiveProjectLabel));
        Changed(nameof(CanArchiveProject));
        Changed(nameof(ProjectListHint));
    }

    private async void ArchiveView_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeProject) { Changed(nameof(ShowArchivedProjects)); return; }
        _showArchivedProjects = !_showArchivedProjects;
        await RefreshProjectListAsync();
    }

    private async Task RefreshProjectListAsync(ProjectProfile? preferredSelection = null, bool saveSelection = true)
    {
        var next = preferredSelection is not null && Projects.Contains(preferredSelection) &&
            preferredSelection.IsArchived == ShowArchivedProjects ? preferredSelection :
            Projects.FirstOrDefault(project => project.Id == _settings.SelectedProjectId && project.IsArchived == ShowArchivedProjects) ??
            Projects.FirstOrDefault(project => project.IsArchived == ShowArchivedProjects);
        // Refresh can briefly clear the WPF selection. Display one final selection so notes,
        // console and database state do not visit a transient project during archive changes.
        _refreshingProjectList = true;
        try
        {
            VisibleProjectItems.Refresh();
            ProjectList.SelectedValue = next;
        }
        finally { _refreshingProjectList = false; }
        UpdateArchiveActions();
        await ShowSelectedProjectAsync(next, saveSelection);
    }

    private async void ArchiveProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanArchiveProject || SelectedProject is not { } selected) return;
        var archive = !selected.IsArchived;
        var next = archive ? Projects.FirstOrDefault(project => !project.IsArchived && project.Id != selected.Id) : selected;
        try
        {
            // Save a detached candidate first. A conflict or failed write leaves the visible
            // project, its archive state, running commands and drafts exactly where they were.
            var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            candidate.Projects.Single(project => project.Id == selected.Id).IsArchived = archive;
            candidate.SelectedProjectId = next?.Id;
            _store.Save(candidate);
        }
        catch (Exception ex) { ShowSaveError(ex); return; }

        selected.IsArchived = archive;
        _settings.SelectedProjectId = next?.Id;
        ProjectItems.First(item => ReferenceEquals(item.Profile, selected)).RefreshArchiveState();
        // Restore returns directly to the active list with the restored project selected.
        _showArchivedProjects = false;
        await RefreshProjectListAsync(next, saveSelection: false);
        var hasProcesses = _runners.GetValueOrDefault(selected.Id)?.Any(service => service.Runner.HasManagedProcess ||
            service.Runner.Snapshot.ProcessIds.Count > 0) == true;
        Notice = archive
            ? hasProcesses
                ? $"Archived {selected.Name}. Its running commands keep running; open Archived to view or stop them."
                : $"Archived {selected.Name}. Open Archived to bring it back."
            : $"Restored {selected.Name} to active projects.";
    }
}
