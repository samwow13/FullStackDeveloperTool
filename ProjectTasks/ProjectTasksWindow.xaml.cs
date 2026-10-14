using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectTasksWindow : Window, IDashboardReplacementWindow
{
    private readonly ProjectTasksViewModel _model;
    private readonly DispatcherTimer _ownerStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _pollingOwner;
    private bool _replacementCheckpointPrepared;
    private bool _replacementCloseAccepted;
    private bool _replacementLargeEditorOpen;
    private DashboardResumeStore? _resumeStore;
    private bool _replacementOwnerPollingSuspended;
    private bool _resumeOwnerPollingAfterReplacement;
    public bool IsDashboardReplacementReady => _model.CanPrepareForDashboardReplacement;
    public bool IsDashboardReplacementClosePending => false;

    public ProjectTasksWindow(Func<string, IReadOnlyList<ProjectApplicationState>>? applicationStates = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
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
        Title = project == null ? "Project notes & queue — Full Stack Launcher"
            : $"{project.Name} · Notes & queue — Full Stack Launcher";
        _ = RefreshOwnerStatusAsync();
    }

    private async Task RefreshOwnerStatusAsync()
    {
        if (_pollingOwner || _replacementOwnerPollingSuspended) return;
        _pollingOwner = true;
        try { await _model.RefreshOwnerStatusAsync(); }
        finally { _pollingOwner = false; }
    }

    internal async Task SuspendOwnerPollingForReplacementAsync(CancellationToken cancellationToken)
    {
        _resumeOwnerPollingAfterReplacement = _ownerStatusTimer.IsEnabled;
        _replacementOwnerPollingSuspended = true;
        _ownerStatusTimer.Stop();
        _model.SuspendOwnerRequestsForReplacement();
        while (_pollingOwner || _model.HasOwnerRequestInProgress)
            await Task.Delay(50, cancellationToken);
    }

    internal static NotesWorkspaceResumeState? LoadReplacementResumeState(DashboardResumeStore store) =>
        store.Load<NotesWorkspaceResumeState>(ProjectTasksViewModel.ReplacementResumeSection);

    internal void SaveReplacementResumeState(DashboardResumeStore store)
    {
        _resumeStore = store;
        var editor = OwnedWindows.Cast<Window>().OfType<ProjectNoteEditorWindow>().FirstOrDefault(window => window.IsVisible);
        editor?.CaptureReplacementResumeState();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var state = _model.CaptureReplacementResumeState() with
        {
            Panel = (Content as ProjectTasksPanel)?.CaptureReplacementResumeState(),
            Width = bounds.Width, Height = bounds.Height, Left = bounds.Left, Top = bounds.Top,
            Maximized = WindowState == WindowState.Maximized,
            LargeEditorOpen = editor != null || _replacementCheckpointPrepared && _replacementLargeEditorOpen
        };
        store.Save(ProjectTasksViewModel.ReplacementResumeSection, state);
        // Only a successful durable save can allow the replacement close path.
        _replacementCheckpointPrepared = true;
        _replacementLargeEditorOpen = state.LargeEditorOpen;
    }

    internal void RestoreReplacementResumeState(DashboardResumeStore store)
    {
        _resumeStore = store;
        var state = LoadReplacementResumeState(store);
        if (state == null) return;
        _model.RestoreReplacementResumeState(state);
        if (double.IsFinite(state.Width) && state.Width >= MinWidth) Width = state.Width;
        if (double.IsFinite(state.Height) && state.Height >= MinHeight) Height = state.Height;
        if (state.Left is { } left && state.Top is { } top && double.IsFinite(left) && double.IsFinite(top) &&
            left + Width > SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + Height > SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (state.Maximized) WindowState = WindowState.Maximized;
        if (Content is ProjectTasksPanel panel)
        {
            if (state.Panel != null) panel.RestoreReplacementResumeState(state.Panel);
            if (state.LargeEditorOpen) panel.ResumeLargeEditor();
        }
    }

    public void CancelReplacementPreparation()
    {
        _replacementCheckpointPrepared = false;
        _replacementCloseAccepted = false;
        _replacementLargeEditorOpen = false;
        _replacementOwnerPollingSuspended = false;
        _model.ResumeOwnerRequestsAfterReplacement();
        if (_resumeOwnerPollingAfterReplacement) _ownerStatusTimer.Start();
        _resumeOwnerPollingAfterReplacement = false;
    }

    public bool PrepareToClose(bool automaticReplacement = false)
    {
        if (automaticReplacement && _replacementCheckpointPrepared)
        {
            _replacementCloseAccepted = true;
            return true;
        }
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_replacementCloseAccepted) return;
        e.Cancel = !PrepareToClose();
        if (e.Cancel || _resumeStore == null) return;
        try { _resumeStore.Remove(ProjectTasksViewModel.ReplacementResumeSection); }
        catch (System.IO.IOException)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                "The saved workspace checkpoint could not be cleared. Check local storage access, then close again. Existing checkpoint and saved notes were preserved.",
                "Close incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
