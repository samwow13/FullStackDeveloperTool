using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Read-only view of one saved queue attempt and its Codex conversation.</summary>
public partial class ProjectTaskChatWindow : Window, INotifyPropertyChanged
{
    private readonly ProjectTaskStore _store = new();
    private readonly string _attemptId;
    private readonly string _projectId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _busy;
    private bool _closed;
    private bool _refreshWhileActive;
    private string _taskIdentity;
    private string _status;
    private string _emptyMessage = "Loading Codex chat…";

    public ProjectTaskChatWindow(ProjectTasksViewModel sourceModel, string attemptId)
    {
        ArgumentNullException.ThrowIfNull(sourceModel);
        if (string.IsNullOrWhiteSpace(attemptId))
            throw new ArgumentException("A queue attempt ID is required.", nameof(attemptId));
        if (!string.Equals(_store.StorePath, sourceModel.StorePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Task chat is not using the selected task store.");

        _projectId = sourceModel.ProjectId ?? throw new InvalidOperationException("Select a project to view its task chat.");
        var receipt = sourceModel.Receipts.Select(row => row.Receipt).FirstOrDefault(candidate =>
            candidate.AttemptId == attemptId && candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            candidate.Snapshot.ProjectId == _projectId)
            ?? throw new InvalidOperationException("This queue attempt is unavailable for the selected project.");

        _attemptId = attemptId;
        TaskName = string.IsNullOrWhiteSpace(receipt.Snapshot.Name) ? "Untitled queue task" : receipt.Snapshot.Name;
        SavedRequest = string.IsNullOrWhiteSpace(receipt.Snapshot.Prompt)
            ? "No task request text was saved for this attempt."
            : receipt.Snapshot.Prompt;
        SavedImages = DescribeImages(receipt.Snapshot.Images);
        _taskIdentity = DescribeIdentity(receipt);
        _refreshWhileActive = IsUnfinished(receipt);
        _status = "Loading messages saved by Codex…";

        InitializeComponent();
        DataContext = this;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(manual: false);
    }

    public string TaskName { get; }
    public string SavedRequest { get; }
    public string SavedImages { get; }
    public ObservableCollection<TaskChatDisplayEntry> ChatEntries { get; } = [];
    public string TaskIdentity { get => _taskIdentity; private set { if (_taskIdentity == value) return; _taskIdentity = value; Changed(); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; Changed(); } }
    public string EmptyMessage { get => _emptyMessage; private set { if (_emptyMessage == value) return; _emptyMessage = value; Changed(); } }
    public bool CanRefresh => !_busy && !_closed;
    public string RefreshDescription => _refreshWhileActive
        ? "Shows messages Codex has saved so far. Refreshes about every 8 seconds while this attempt is unfinished."
        : "Shows messages Codex has saved so far. Select Refresh to check again.";

    private static bool IsUnfinished(ProjectTaskExecutionReceipt receipt) =>
        receipt.QueueReviewCompletedAt is null && receipt.State is
            ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
            ProjectTaskRunState.Running or ProjectTaskRunState.Recovering;

    private static string DescribeIdentity(ProjectTaskExecutionReceipt receipt) =>
        $"Task: {receipt.ThreadId ?? "not recorded"} · Turn: {receipt.TurnId ?? "not recorded"} · Attempt: {receipt.AttemptId}";

    private static string DescribeImages(IReadOnlyList<ProjectTaskNoteImage> images)
    {
        if (images.Count == 0) return "";
        var captions = images.Select((image, index) =>
        {
            var name = string.IsNullOrWhiteSpace(image.Caption) ? $"Image {index + 1}" : image.Caption.Trim();
            if (string.IsNullOrWhiteSpace(image.PageHtml) && string.IsNullOrWhiteSpace(image.PageCss))
                return name;
            return name + (image.IncludePageContextInPrompt ? " (page source included)" : " (page source saved only)");
        });
        return $"{images.Count} saved image{(images.Count == 1 ? "" : "s")}: {string.Join(", ", captions)}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_refreshWhileActive) _refreshTimer.Start();
        await RefreshAsync(manual: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(manual: true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync(bool manual)
    {
        if (_busy || _closed) return;
        _busy = true;
        Changed(nameof(CanRefresh));
        if (manual) Status = "Refreshing Codex chat…";
        try
        {
            var token = _lifetime.Token;
            var (receipt, warning) = await Task.Run(() =>
            {
                var data = _store.Load();
                var exact = data.Receipts.FirstOrDefault(candidate =>
                    candidate.AttemptId == _attemptId &&
                    candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                    candidate.Snapshot.ProjectId == _projectId);
                return (exact, _store.LoadWarning);
            }, token);
            token.ThrowIfCancellationRequested();

            if (warning is not null)
            {
                Status = "Saved task history is unavailable. Previously loaded chat remains visible. Select Refresh to retry.";
                return;
            }
            if (receipt is null)
            {
                _refreshTimer.Stop();
                Status = "This exact queue attempt is unavailable in saved task history.";
                return;
            }

            TaskIdentity = DescribeIdentity(receipt);
            _refreshWhileActive = IsUnfinished(receipt);
            Changed(nameof(RefreshDescription));
            if (_refreshWhileActive && !_refreshTimer.IsEnabled) _refreshTimer.Start();
            else if (!_refreshWhileActive) _refreshTimer.Stop();

            // Reader validates the folder and starts its own App Server child before
            // its first await. Keep that preparation off the WPF dispatcher.
            var result = await Task.Run(() => CodexTaskChatReader.ReadAsync(receipt, token), token);
            token.ThrowIfCancellationRequested();
            var retainPrevious = result.Entries.Count == 0 && ChatEntries.Count > 0;
            if (!retainPrevious) UpdateEntries(result.Entries);
            var readStatus = string.IsNullOrWhiteSpace(result.Status)
                ? "Codex chat refreshed."
                : result.Status;
            Status = retainPrevious
                ? readStatus + " Previously loaded messages remain visible and may be stale."
                : readStatus;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception)
        {
            if (!_closed) Status = "Codex chat is temporarily unavailable. Previously loaded messages remain visible. Select Refresh to retry.";
        }
        finally
        {
            _busy = false;
            if (_closed) _lifetime.Dispose();
            else Changed(nameof(CanRefresh));
        }
    }

    private void UpdateEntries(IReadOnlyList<CodexTaskChatEntry> entries)
    {
        var common = 0;
        while (common < ChatEntries.Count && common < entries.Count &&
               ChatEntries[common].Role == entries[common].Role &&
               ChatEntries[common].Text == entries[common].Text &&
               ChatEntries[common].Phase == entries[common].Phase)
            common++;
        for (var index = ChatEntries.Count - 1; index >= common; index--) ChatEntries.RemoveAt(index);
        for (var index = common; index < entries.Count; index++)
            ChatEntries.Add(new(entries[index].Role, entries[index].Text, entries[index].Phase));
        EmptyMessage = entries.Count == 0 ? "No Codex messages are available for this exact attempt yet." : "";
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _refreshTimer.Stop();
        _lifetime.Cancel();
        if (!_busy) _lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TaskChatDisplayEntry(string Role, string Text, string? Phase)
{
    public string PhaseLabel => Phase switch
    {
        "commentary" => "Progress",
        "final_answer" => "Final response",
        _ => ""
    };
}
