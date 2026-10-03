using System.Windows;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectNoteEditorWindow : Window
{
    public ProjectNoteEditorWindow(ProjectTasksViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) => Editor.FocusPrompt();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
