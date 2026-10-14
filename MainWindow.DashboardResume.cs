using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher;

public partial class MainWindow
{
    private const string DashboardResumeSection = "dashboard-session";
    private const string ProjectEditResumeSection = "project-editor";
    private string? _ownedInlineProjectResumeIdentity;
    private readonly HashSet<string> _unrestoredResumeSections = new(StringComparer.Ordinal);

    private void SaveDashboardDraftCheckpoint()
    {
        RequireReadableResumeCheckpoints();
        SaveStartAgentDashboardResume();
        _projectTasksWindow?.SaveReplacementResumeState(_dashboardResumeStore);
        CodexCrewPanel.SaveReplyReplacementResumeState(_dashboardResumeStore);
        SaveInlineProjectResume();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _dashboardResumeStore.Save(DashboardResumeSection, new DashboardSessionResume(
            SelectedProject?.Id, bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
    }

    private void SaveInlineProjectResume()
    {
        if (!IsEditing || SelectedProject is null)
        {
            // Unmatched drafts belong to their original project/service folders.
            // An idle editor must not silently delete that recovery state.
            return;
        }
        var state = new ProjectEditorResume(
            SelectedProject.Id, _store.ResolveRoot(SelectedProject), DraftProjectName,
            Services.Select(service => new ServiceEditorResume(service.Profile.Id, service.Directory,
                service.DraftName, service.DraftPort)).ToArray());
        var identity = InlineResumeIdentity(state);
        var saved = LoadInlineProjectResumes().Where(existing => InlineResumeIdentity(existing) != identity).ToList();
        saved.Add(state);
        _dashboardResumeStore.Save(ProjectEditResumeSection, new ProjectEditorCheckpoint(1, saved));
        _ownedInlineProjectResumeIdentity = identity;
    }

    private async Task RestoreDashboardDraftCheckpointAsync()
    {
        var failures = new List<string>();
        try
        {
            var state = _dashboardResumeStore.Load<DashboardSessionResume>(DashboardResumeSection);
            if (state?.ProjectId is { } projectId && Projects.FirstOrDefault(project => project.Id == projectId && !project.IsArchived) is { } project)
                await ShowSelectedProjectAsync(project, saveSelection: false);
            if (state is not null) RestoreDashboardBounds(state);
        }
        catch (Exception)
        {
            _unrestoredResumeSections.Add(DashboardResumeSection);
            failures.Add("dashboard position");
        }
        RestoreResumeSurface("start-agent", "Start Agent", RestoreStartAgentDashboardResume, failures);
        RestoreResumeSurface("crew-reply-drafts", "Codex replies", () =>
            CodexCrewPanel.RestoreReplyReplacementResumeState(_dashboardResumeStore), failures);
        RestoreResumeSurface("notes-workspace", "Notes workspace", () =>
        {
            var notes = ProjectTasksWindow.LoadReplacementResumeState(_dashboardResumeStore);
            if (notes is not null)
            {
                var noteProject = Projects.FirstOrDefault(project => project.Id == notes.ProjectId);
                var newlyCreated = _projectTasksWindow is null;
                _projectTasksWindow ??= CreateResumeNotesWindow();
                try
                {
                    _projectTasksWindow.ShowProject(noteProject, noteProject is null ? "" : _store.ResolveRoot(noteProject));
                    _projectTasksWindow.RestoreReplacementResumeState(_dashboardResumeStore);
                    _projectTasksWindow.Show();
                }
                catch
                {
                    if (newlyCreated) _projectTasksWindow = null;
                    throw;
                }
            }
        }, failures);
        RestoreResumeSurface(ProjectEditResumeSection, "project edits", RestoreInlineProjectResume, failures);
        if (!_unrestoredResumeSections.Contains(DashboardResumeSection))
            RestoreResumeSurface(DashboardResumeSection, "dashboard recovery", () =>
                _dashboardResumeStore.Remove(DashboardResumeSection), failures);
            // Restoration opens editing surfaces only. Existing submit/save actions remain explicit.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (_closed || _closing || _closeRequested) return;
                try
                {
                    ResumeStartAgentDashboardWindow();
                    CodexCrewPanel.ResumeReplyWindowAfterReplacement();
                    ResumeServiceEditors();
                }
                catch (Exception)
                {
                    Notice = "Launcher restarted. A saved editor could not reopen; its draft checkpoint was retained.";
                }
            }));
        if (failures.Count > 0)
            Notice = "Launcher restarted. These drafts could not be restored: " + string.Join(", ", failures) + ". Checkpoint files were preserved; check local storage access.";
    }

    private void RestoreResumeSurface(string section, string name, Action restore, List<string> failures)
    {
        try { restore(); }
        catch (Exception)
        {
            _unrestoredResumeSections.Add(section);
            failures.Add(name);
        }
    }

    private void RequireReadableResumeCheckpoints()
    {
        if (_unrestoredResumeSections.Count > 0)
            throw new IOException("Saved Launcher drafts need recovery before replacement. Existing checkpoint files and current drafts were preserved; resolve the reported restoration failure first.");
    }

    private ProjectTasksWindow CreateResumeNotesWindow()
    {
        var window = new ProjectTasksWindow(CaptureProjectApplicationStates) { Owner = this };
        window.Closed += (_, _) => { if (ReferenceEquals(_projectTasksWindow, window)) _projectTasksWindow = null; };
        return window;
    }

    private void RestoreInlineProjectResume()
    {
        var drafts = LoadInlineProjectResumes();
        if (drafts.Count == 0) return;
        var state = drafts.LastOrDefault(state => SelectedProject?.Id == state.ProjectId &&
            _store.ResolveRoot(SelectedProject!).Equals(state.Root, StringComparison.OrdinalIgnoreCase) &&
            state.Services.Length == Services.Count && state.Services.All(saved => Services.Any(service =>
                service.Profile.Id == saved.Id && service.Directory.Equals(saved.Directory, StringComparison.OrdinalIgnoreCase))));
        if (state is null || !CanEdit)
        {
            Notice = "Project draft retained. Its project changed or cannot be edited yet.";
            return;
        }
        BeginProjectEditing();
        if (!IsEditing) return;
        DraftProjectName = state.Name;
        foreach (var saved in state.Services)
        {
            var service = Services.Single(service => service.Profile.Id == saved.Id);
            service.DraftName = saved.Name;
            service.DraftPort = saved.Port;
        }
        Notice = "Project edit draft restored. Save changes when ready.";
        _ownedInlineProjectResumeIdentity = InlineResumeIdentity(state);
    }

    private List<ProjectEditorResume> LoadInlineProjectResumes()
    {
        var checkpoint = _dashboardResumeStore.Load<ProjectEditorCheckpoint>(ProjectEditResumeSection);
        if (checkpoint is null) return [];
        if (checkpoint.Version != 1 || checkpoint.Drafts is null || checkpoint.Drafts.Any(state =>
                state is null || string.IsNullOrWhiteSpace(state.ProjectId) || string.IsNullOrWhiteSpace(state.Root) ||
                state.Name is null || state.Services is null || state.Services.Any(service => service is null ||
                    string.IsNullOrWhiteSpace(service.Id) || string.IsNullOrWhiteSpace(service.Directory) ||
                    service.Name is null || service.Port is null)))
            throw new IOException("Saved project drafts could not be read. Existing checkpoint was preserved.");
        return checkpoint.Drafts;
    }

    private static string InlineResumeIdentity(ProjectEditorResume state) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            state.ProjectId, Root = state.Root.ToUpperInvariant(),
            Services = state.Services.OrderBy(service => service.Id, StringComparer.Ordinal)
                .Select(service => new { service.Id, Directory = service.Directory.ToUpperInvariant() })
        })));

    private bool ForgetOwnedInlineProjectResume()
    {
        if (_ownedInlineProjectResumeIdentity is not { } identity) return true;
        try
        {
            var retained = LoadInlineProjectResumes().Where(state => InlineResumeIdentity(state) != identity).ToList();
            if (retained.Count == 0) _dashboardResumeStore.Remove(ProjectEditResumeSection);
            else _dashboardResumeStore.Save(ProjectEditResumeSection, new ProjectEditorCheckpoint(1, retained));
            _ownedInlineProjectResumeIdentity = null;
            return true;
        }
        catch (Exception)
        {
            EditError = "Project draft checkpoint could not be cleared. Current draft and saved files are kept; check local storage access.";
            return false;
        }
    }

    private void RestoreDashboardBounds(DashboardSessionResume state)
    {
        if (!double.IsFinite(state.Width) || !double.IsFinite(state.Height) ||
            !double.IsFinite(state.Left) || !double.IsFinite(state.Top)) return;
        if (state.Width >= MinWidth) Width = Math.Max(MinWidth, Math.Min(state.Width, SystemParameters.VirtualScreenWidth));
        if (state.Height >= MinHeight) Height = Math.Max(MinHeight, Math.Min(state.Height, SystemParameters.VirtualScreenHeight));
        if (state.Left + Width > SystemParameters.VirtualScreenLeft && state.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            state.Top + Height > SystemParameters.VirtualScreenTop && state.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = state.Left;
            Top = state.Top;
        }
        if (state.Maximized) WindowState = WindowState.Maximized;
    }

    private sealed record DashboardSessionResume(string? ProjectId, double Left, double Top, double Width, double Height, bool Maximized);
    private sealed record ProjectEditorResume(string ProjectId, string Root, string Name, ServiceEditorResume[] Services);
    private sealed record ProjectEditorCheckpoint(int Version, List<ProjectEditorResume> Drafts);
    private sealed record ServiceEditorResume(string Id, string Directory, string Name, string Port);
}
