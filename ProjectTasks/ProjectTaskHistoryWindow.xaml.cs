using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Project-scoped display history; execution receipts stay in the task store.</summary>
public partial class ProjectTaskHistoryWindow : Window, INotifyPropertyChanged
{
    private readonly ProjectTasksViewModel _sourceModel;
    private TaskActivityRow? _selectedHistory;
    private string _statusMessage = "Select a task to view its saved attempt or remove it from History.";

    public ProjectTaskHistoryWindow(ProjectTasksViewModel sourceModel)
    {
        _sourceModel = sourceModel ?? throw new ArgumentNullException(nameof(sourceModel));
        if (string.IsNullOrWhiteSpace(_sourceModel.ProjectId))
            throw new InvalidOperationException("Select a project to view its task history.");

        InitializeComponent();
        DataContext = this;
        _sourceModel.PropertyChanged += SourceModel_PropertyChanged;
        HistoryItems.CollectionChanged += HistoryItems_CollectionChanged;
        SelectedHistory = HistoryItems.FirstOrDefault();
    }

    public ObservableCollection<TaskActivityRow> HistoryItems => _sourceModel.TaskHistory;
    public string HistorySummary => _sourceModel.HistorySummary;
    public Visibility EmptyVisibility => HistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanClearHistory => HistoryItems.Count > 0 && _sourceModel.CanManageTaskHistory;
    public TaskActivityRow? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (ReferenceEquals(_selectedHistory, value)) return;
            _selectedHistory = value;
            Changed();
            RefreshSelected();
        }
    }
    public string SelectedDetails => SelectedHistory is { } row
        ? $"Task: {row.Name}\nStatus: {row.Status}\nDetails: {row.Detail}\nAttempt: {row.AttemptId ?? "not recorded"}"
        : "Select a task in History.";
    public bool CanViewChat => !string.IsNullOrWhiteSpace(SelectedHistory?.AttemptId);
    public bool CanDeleteSelected => _sourceModel.CanManageTaskHistory &&
        SelectedHistory is { CanDelete: true } row &&
        !string.IsNullOrWhiteSpace(row.AttemptId);
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; Changed(); } }

    private void SourceModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ProjectTasksViewModel.HistorySummary))
            Changed(nameof(HistorySummary));
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ProjectTasksViewModel.CanManageTaskHistory))
        {
            Changed(nameof(CanClearHistory));
            Changed(nameof(CanDeleteSelected));
        }
    }

    private void HistoryItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedHistory is { } selected && !HistoryItems.Contains(selected))
            SelectedHistory = HistoryItems.FirstOrDefault();
        else if (SelectedHistory is null && HistoryItems.Count > 0)
            SelectedHistory = HistoryItems[0];
        Changed(nameof(EmptyVisibility));
        Changed(nameof(CanClearHistory));
        Changed(nameof(HistorySummary));
    }

    private void RefreshSelected()
    {
        Changed(nameof(SelectedDetails));
        Changed(nameof(CanViewChat));
        Changed(nameof(CanDeleteSelected));
    }

    private void ViewChat_Click(object sender, RoutedEventArgs e)
    {
        if (!CanViewChat || SelectedHistory?.AttemptId is not { } attemptId) return;
        new ProjectTaskChatWindow(_sourceModel, attemptId) { Owner = this }.ShowDialog();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteSelected || _sourceModel.ProjectId is not { } projectId ||
            SelectedHistory is not { AttemptId: { } attemptId } row) return;
        var response = MessageBox.Show(this,
            $"Delete '{row.Name}' from History?\n\n" +
            "This removes its tracked activity. If its outcome is unresolved, its queue hold is abandoned. " +
            "Codex work may still be running. The saved execution receipt and outcome remain in All attempts.",
            "Delete task from History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (response != MessageBoxResult.Yes) return;

        StatusMessage = await _sourceModel.DeleteTaskActivityAsync(projectId, attemptId)
            ? "Task removed from History. Its saved execution receipt remains in All attempts."
            : _sourceModel.Feedback;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!CanClearHistory) return;
        var response = MessageBox.Show(this,
            $"Clear all {HistoryItems.Count} task history entries for '{_sourceModel.ProjectName}'?\n\n" +
            "This removes their task activity from History. Saved execution receipts and outcomes remain in All attempts.",
            "Clear task history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (response != MessageBoxResult.Yes) return;

        StatusMessage = _sourceModel.ClearTaskHistory()
            ? "History cleared. Saved execution receipts remain in All attempts."
            : _sourceModel.Feedback;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _sourceModel.PropertyChanged -= SourceModel_PropertyChanged;
        HistoryItems.CollectionChanged -= HistoryItems_CollectionChanged;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
