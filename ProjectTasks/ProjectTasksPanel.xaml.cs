using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectTasksPanel : UserControl
{
    private Window? _ownerWindow;
    private Popup? _openTaskActionsPopup;

    public ProjectTasksPanel()
    {
        InitializeComponent();
        QueueSettingsPopup.CustomPopupPlacementCallback = PlaceQueueSettingsPopup;
        Loaded += Panel_Loaded;
        Unloaded += Panel_Unloaded;
    }

    private static CustomPopupPlacement[] PlaceQueueSettingsPopup(Size popupSize, Size targetSize, Point offset) =>
    [
        new(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 7), PopupPrimaryAxis.Vertical),
        new(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 7), PopupPrimaryAxis.Vertical)
    ];

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow != null) return;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow == null) return;
        _ownerWindow.LocationChanged += OwnerWindow_LocationChanged;
        _ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
        _ownerWindow.PreviewMouseDown += OwnerWindow_PreviewMouseDown;
        _ownerWindow.Deactivated += OwnerWindow_Deactivated;
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        QueueSettingsToggle.IsChecked = false;
        CloseTaskActionsPopup();
        if (_ownerWindow == null) return;
        _ownerWindow.LocationChanged -= OwnerWindow_LocationChanged;
        _ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
        _ownerWindow.PreviewMouseDown -= OwnerWindow_PreviewMouseDown;
        _ownerWindow.Deactivated -= OwnerWindow_Deactivated;
        _ownerWindow = null;
    }

    private void OwnerWindow_LocationChanged(object? sender, EventArgs e)
    {
        QueueSettingsToggle.IsChecked = false;
        CloseTaskActionsPopup();
    }

    private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueSettingsToggle.IsChecked = false;
        CloseTaskActionsPopup();
    }

    private void OwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        QueueSettingsToggle.IsChecked = false;
        CloseTaskActionsPopup();
    }

    private void OwnerWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (QueueSettingsToggle.IsChecked == true && !QueueSettingsToggle.IsMouseOver && !QueueSettingsContent.IsMouseOver)
            QueueSettingsToggle.IsChecked = false;
    }

    private void PanelScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PanelScroller) && (e.VerticalChange != 0 || e.HorizontalChange != 0))
            QueueSettingsToggle.IsChecked = false;
        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
            CloseTaskActionsPopup();
    }

    private void QueueSettings_Opened(object? sender, EventArgs e)
    {
        CloseTaskActionsPopup();
        Keyboard.Focus(QueueSettingsContent);
    }

    private void TaskActions_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        QueueSettingsToggle.IsChecked = false;
        if (_openTaskActionsPopup != null && !ReferenceEquals(_openTaskActionsPopup, popup))
            _openTaskActionsPopup.IsOpen = false;
        _openTaskActionsPopup = popup;
        if (popup.Child is UIElement child) Keyboard.Focus(child);
    }

    private void TaskActions_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_openTaskActionsPopup, sender)) _openTaskActionsPopup = null;
    }

    private void TaskActions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var target = _openTaskActionsPopup?.PlacementTarget as UIElement;
        CloseTaskActionsPopup();
        target?.Focus();
        e.Handled = true;
    }

    private void CloseTaskActionsPopup()
    {
        if (_openTaskActionsPopup is not { } popup) return;
        _openTaskActionsPopup = null;
        popup.IsOpen = false;
    }

    private void CloseQueueSettings_Click(object sender, RoutedEventArgs e) => CloseQueueSettings();

    private void BrowseAutomaticLoopFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || !model.CanEditAutomaticLoopSettings) return;
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose this project's loop folder",
            Multiselect = false,
            InitialDirectory = System.IO.Directory.Exists(model.AutomaticLoopFolderText)
                ? model.AutomaticLoopFolderText : model.ProjectFolder
        };
        // Native dialog deactivates the workspace, so close the popup explicitly.
        CloseQueueSettings();
        if (picker.ShowDialog(Window.GetWindow(this)) == true) model.AutomaticLoopFolderText = picker.FolderName;
        QueueSettingsToggle.IsChecked = true;
    }

    private void QueueSettings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseQueueSettings();
        e.Handled = true;
    }

    private void CloseQueueSettings()
    {
        QueueSettingsToggle.IsChecked = false;
        QueueSettingsToggle.Focus();
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e) => OpenLargeEditor();

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || !model.NewNoteCommand.CanExecute(null)) return;
        model.NewNoteCommand.Execute(null);
        OpenLargeEditor();
    }

    private void Notes_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is ProjectTasksViewModel { SelectedNote: not null }) OpenLargeEditor();
    }

    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: QueueRow selected } list) return;
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(list.SelectedItem, selected)) list.ScrollIntoView(selected);
        }));
    }

    private void OpenLargeEditor()
    {
        if (DataContext is not ProjectTasksViewModel { CanEdit: true } model) return;
        new ProjectNoteEditorWindow(model) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void CodexConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || model.ProjectId is not { } projectId) return;
        QueueSettingsToggle.IsChecked = false;
        new CodexConnectionWindow(projectId, model.ProjectName, model.AssignedFolder)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
        // The diagnostic receipt writer may have changed the shared task store.
        // Reload already preserves every unsaved editor draft.
        model.ReloadCommand.Execute(null);
    }

    private void QueueAttempts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model) return;
        new ProjectQueueAttemptHistoryWindow(model) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void TaskHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel { HasProject: true } model) return;
        CloseTaskActionsPopup();
        new ProjectTaskHistoryWindow(model) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void ViewTaskChat_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId } }) return;
        new ProjectTaskChatWindow(model, attemptId) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void StopTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId } row } ||
            !model.CanStopTask(attemptId)) return;
        CloseTaskActionsPopup();
        if (MessageBox.Show(Window.GetWindow(this),
                $"Stop '{row.Name}'?\n\nAttempt: {attemptId}\n\n" +
                "The queue owner will pause future dispatch and request interruption of this exact task. " +
                "Wait for its terminal result before reviewing or re-attempting it.",
                "Stop task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await model.StopTaskAsync(attemptId);
    }

    private async void RetryTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId } row } ||
            !model.CanRetryTask(attemptId)) return;
        var receipt = model.Receipts.FirstOrDefault(candidate => candidate.Receipt.AttemptId == attemptId)?.Receipt;
        if (receipt is null) return;
        CloseTaskActionsPopup();
        if (MessageBox.Show(Window.GetWindow(this),
                $"Prepare a new attempt for '{row.Name}'?\n\n" +
                $"Previous attempt: {attemptId}\nTask: {receipt.ThreadId ?? "unknown"}\nTurn: {receipt.TurnId ?? "unknown"}\n\n" +
                "The prior receipt stays saved. The queue remains paused until Enable auto-run is used.",
                "Re-attempt task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await model.RetryTaskAsync(attemptId);
    }

    private void ReviewTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId } }) return;
        CloseTaskActionsPopup();
        new ProjectQueueAttemptHistoryWindow(model, attemptId) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void ArchiveTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId, CanMoveToHistory: true } }) return;
        CloseTaskActionsPopup();
        model.MoveTaskToHistory(attemptId);
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model ||
            model.ProjectId is not { } projectId ||
            sender is not FrameworkElement { DataContext: TaskActivityRow { AttemptId: { } attemptId, CanDelete: true } row }) return;
        CloseTaskActionsPopup();
        if (MessageBox.Show(Window.GetWindow(this),
                $"Delete '{row.Name}' from tracked task history?\n\n" +
                "If its outcome is unresolved, this abandons the queue hold and skips that attempt. " +
                "It does not stop Codex work that may still be running. Queues pause until you resume them. " +
                "The saved receipt and original outcome remain in All attempts.",
                "Delete tracked task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await model.DeleteTaskActivityAsync(projectId, attemptId);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || model.SelectedNote is not { } note || !model.DeleteNoteCommand.CanExecute(null)) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete the note ‘{note.Name}’ completely? Previous execution receipts will be retained." +
                model.GetNoteDeletionWarning(note.Id),
                "Delete note", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            model.DeleteNoteCommand.Execute(null);
    }

    private void DeleteQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || model.SelectedQueue is not { } item ||
            !model.DeleteQueueItemCommand.CanExecute(null)) return;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Delete ‘{item.Name}’ completely from Notes and Queue? Previous execution receipts will be retained." +
                model.GetNoteDeletionWarning(item.NoteId),
                "Delete queue item", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            model.DeleteQueueItemCommand.Execute(null);
    }

    private void RemoveAllAiPrompts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model || !model.RemoveAllAiPromptsCommand.CanExecute(null)) return;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Remove all {model.AiPromptCount} AI generated prompt(s) from '{model.ProjectName}'?\n\n" +
                "This deletes AI notes, their unsaved edits, and inactive queue entries. " +
                "Manual notes and execution receipts are retained. " +
                "AI notes used by active or unresolved queue attempts stay until those attempts finish or are reviewed.",
                "Remove all AI generated Prompts", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            model.RemoveAllAiPromptsCommand.Execute(null);
    }

    private async void RetrySelectedItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel { CanRetrySelectedItem: true } model ||
            model.SelectedQueue is not { } item || model.SelectedRetryReceipt is not { } receipt) return;
        var response = MessageBox.Show(Window.GetWindow(this),
            $"Retry '{item.Name}' as a new Codex attempt?\n\n" +
            $"Previous attempt: {receipt.AttemptId}\nTask: {receipt.ThreadId ?? "unknown"}\n" +
            $"Turn: {receipt.TurnId ?? "unknown"}\n\n" +
            "The previous receipt and its outcome stay saved. This action only resets the selected item to Pending. " +
            "The queue stays paused; use Enable Queue separately when ready.",
            "Retry queue item", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (response == MessageBoxResult.Yes)
            await model.RetrySelectedItemAsync(item.Id, receipt.AttemptId);
    }
}

public sealed class EmptyCountVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
