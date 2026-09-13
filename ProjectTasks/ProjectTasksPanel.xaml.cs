using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectTasksPanel : UserControl
{
    public ProjectTasksPanel() => InitializeComponent();

    private void CodexConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || model.ProjectId is not { } projectId) return;
        new CodexConnectionWindow(projectId, model.ProjectName, model.AssignedFolder)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
        // The diagnostic receipt writer may have changed the shared task store.
        // Reload already preserves every unsaved editor draft.
        model.ReloadCommand.Execute(null);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || model.SelectedNote is not { } note || !model.DeleteNoteCommand.CanExecute(null)) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete the note ‘{note.Name}’ and remove it from the queue? Previous execution receipts will be retained.",
                "Delete note", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            model.DeleteNoteCommand.Execute(null);
    }
}

public sealed class EmptyCountVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
