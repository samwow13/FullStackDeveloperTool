using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectNoteEditorWindow : Window, IDashboardReplacementWindow
{
    private readonly ProjectTasksViewModel _model;
    public bool IsDashboardReplacementReady => _model.CanPrepareForDashboardReplacement;
    public bool IsDashboardReplacementClosePending => false;

    public ProjectNoteEditorWindow(ProjectTasksViewModel model)
    {
        _model = model;
        FullStackLauncher.Services.WindowTheme.Attach(this);
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) => { Editor.FocusPrompt(); Editor.RestoreResumeState(); };
        Closing += (_, _) => Editor.CaptureResumeState();
    }

    internal void CaptureReplacementResumeState() => Editor.CaptureResumeState();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
