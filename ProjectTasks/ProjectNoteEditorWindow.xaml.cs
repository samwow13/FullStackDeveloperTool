using System.Windows;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectNoteEditorWindow : Window
{
    public ProjectNoteEditorWindow(ProjectTasksViewModel model)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) => Editor.FocusPrompt();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
