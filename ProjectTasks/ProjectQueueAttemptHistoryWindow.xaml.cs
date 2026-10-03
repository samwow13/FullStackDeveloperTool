using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectQueueAttemptHistoryWindow : Window, INotifyPropertyChanged
{
    private readonly ProjectTasksViewModel _sourceModel;
    private readonly ProjectTaskStore _store = new();
    private readonly List<QueueAttemptHistoryRow> _allAttempts = [];
    private QueueAttemptHistoryRow? _selectedAttempt;
    private bool _showAllHistory;
    private bool _busy;
    private string _status = "";

    public ProjectQueueAttemptHistoryWindow(ProjectTasksViewModel sourceModel, string? preferredAttemptId = null)
    {
        _sourceModel = sourceModel ?? throw new ArgumentNullException(nameof(sourceModel));
        if (!string.Equals(_store.StorePath, sourceModel.StorePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Queue attempt history is not using the selected task store.");
        InitializeComponent();
        DataContext = this;
        ReloadAttempts(preferredAttemptId);
    }

    public ObservableCollection<QueueAttemptHistoryRow> Attempts { get; } = [];
    public bool ShowAllHistory
    {
        get => _showAllHistory;
        set
        {
            if (_showAllHistory == value) return;
            _showAllHistory = value;
            Changed();
            RebuildRows(_selectedAttempt?.Receipt.AttemptId);
        }
    }
    public QueueAttemptHistoryRow? SelectedAttempt
    {
        get => _selectedAttempt;
        set
        {
            if (ReferenceEquals(_selectedAttempt, value)) return;
            _selectedAttempt = value;
            Changed();
            RefreshSelected();
        }
    }
    public string CountText => $"{_allAttempts.Count(IsOutstanding)} outstanding · {_allAttempts.Count} saved across all projects";
    public string SelectedDetails
    {
        get
        {
            if (SelectedAttempt is not { } row) return "Select an attempt to inspect its status.";
            var receipt = row.Receipt;
            var item = row.IsOrphan ? "Queue item deleted" : "Queue item saved";
            return $"Attempt: {receipt.AttemptId}\n" +
                   $"Project: {receipt.Snapshot.ProjectName} ({receipt.Snapshot.ProjectId})\n" +
                   $"Item: {item} · {receipt.Snapshot.QueueItemId}\n" +
                   $"Task: {receipt.ThreadId ?? "not recorded"}\n" +
                   $"Turn: {receipt.TurnId ?? "not recorded"}\n" +
                   $"State: {receipt.State} · Outcome: {receipt.Outcome}\n" +
                   $"Created: {LocalTime(receipt.CreatedAt)}\n" +
                   $"Submission started: {LocalTime(receipt.SubmissionStartedAt)}\n" +
                   $"Started: {LocalTime(receipt.StartedAt)}\n" +
                   $"Finished: {LocalTime(receipt.FinishedAt)}\n" +
                   $"Review completed: {LocalTime(receipt.QueueReviewCompletedAt)}\n" +
                   $"Deleted and abandoned: {LocalTime(receipt.QueueAbandonedAt)}\n" +
                   $"Moved to History: {LocalTime(receipt.ActivityArchivedAt)}\n" +
                   $"Deleted from task activity: {LocalTime(receipt.ActivityDeletedAt)}\n" +
                   $"Folder: {receipt.Snapshot.Folder}\n" +
                   $"Attention: {receipt.AttentionReason}";
        }
    }
    public string SelectedSummary => SelectedAttempt?.Receipt.ResultSummary ?? "";
    public string SelectedFinalResponse => SelectedAttempt?.Receipt.FinalResponse ?? "";
    public bool CanDismissSelected => !_busy && _store.CanSave && SelectedAttempt is { Receipt: { } receipt } &&
        IsOutstanding(SelectedAttempt) && HasNoRecordedSubmission(receipt) && IsReviewableState(receipt.State);
    public bool CanReviewSelected => !_busy && _store.CanSave && SelectedAttempt is { Receipt: { } receipt } &&
        IsOutstanding(SelectedAttempt) && !HasNoRecordedSubmission(receipt) &&
        !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId) &&
        IsReviewableState(receipt.State);
    public bool CanDeleteSelected => !_busy && _store.CanSave &&
        SelectedAttempt is { Receipt.ActivityDeletedAt: null };
    public string ActionHint
    {
        get
        {
            if (SelectedAttempt is not { } row) return "Select a saved attempt. Outstanding attempts appear first.";
            if (row.Receipt.QueueAbandonedAt is not null)
                return "Tracking deleted and queue hold abandoned. Saved outcome remains unchanged. Resume queues explicitly when ready.";
            if (row.Receipt.ActivityDeletedAt is not null)
                return "Tracking deleted. Receipt remains saved with its original outcome.";
            if (!IsOutstanding(row)) return row.Receipt.QueueReviewCompletedAt is not null
                ? "Hold released. Receipt remains saved; queue remains paused until enabled explicitly."
                : "Confirmed successful attempt. No queue hold remains.";
            if (CanDeleteSelected)
                return "Delete removes tracking and abandons this hold without reviewing its outcome. Codex work may still be active. Queues pause for manual resume.";
            if (CanDismissSelected)
                return "The receipt has no recorded submission start or Codex task identity. Before dismissing this hold, check Codex for related active work; the queue owner will also recheck the receipt and execution slot.";
            if (CanReviewSelected)
                return "Find this exact task and turn in Codex. Confirm its task and child work are no longer active before releasing the hold.";
            if (!_store.CanSave) return "Task store is unavailable. Restore it and reload before changing any hold.";
            return "Submission may have begun, but exact task and turn IDs are unavailable. This hold cannot be released here without exact-task review.";
        }
    }
    public string Status { get => _status; private set { _status = value; Changed(); } }

    private static bool IsConfirmedSuccessful(ProjectTaskExecutionReceipt receipt) =>
        receipt.FinishedAt is not null && receipt.State == ProjectTaskRunState.Completed &&
        receipt.Outcome == ProjectTaskOutcome.Succeeded &&
        !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId);

    private static bool IsOutstanding(QueueAttemptHistoryRow row) =>
        row.Receipt.QueueReviewCompletedAt is null && row.Receipt.QueueAbandonedAt is null &&
        !IsConfirmedSuccessful(row.Receipt);

    private static bool HasNoRecordedSubmission(ProjectTaskExecutionReceipt receipt) =>
        receipt.SubmissionStartedAt is null && receipt.StartedAt is null &&
        receipt.ThreadId is null && receipt.TurnId is null;

    private static bool IsReviewableState(ProjectTaskRunState state) => state is
        ProjectTaskRunState.Prepared or ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention or
        ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed;

    private static string LocalTime(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("g") ?? "not recorded";

    private void ReloadAttempts(string? preferredAttemptId = null)
    {
        preferredAttemptId ??= _selectedAttempt?.Receipt.AttemptId;
        var data = _store.Load();
        _allAttempts.Clear();
        foreach (var receipt in data.Receipts.Where(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem)
                     .OrderByDescending(receipt => receipt.CreatedAt)
                     .ThenByDescending(receipt => receipt.AttemptId, StringComparer.Ordinal))
            _allAttempts.Add(new(receipt, data.QueueItems.All(item => item.Id != receipt.Snapshot.QueueItemId)));
        RebuildRows(preferredAttemptId);
        Status = _store.LoadWarning ?? (_allAttempts.Count == 0
            ? "No saved queue attempts."
            : "Select an attempt for its recorded status and exact Codex task and turn IDs.");
    }

    private void RebuildRows(string? preferredAttemptId)
    {
        Attempts.Clear();
        foreach (var row in _allAttempts.Where(row => _showAllHistory ||
                     row.Receipt.ActivityDeletedAt is null && IsOutstanding(row))) Attempts.Add(row);
        SelectedAttempt = Attempts.FirstOrDefault(row => row.Receipt.AttemptId == preferredAttemptId)
            ?? Attempts.FirstOrDefault(row => row.Receipt.Snapshot.ProjectId == _sourceModel.ProjectId)
            ?? Attempts.FirstOrDefault();
        Changed(nameof(CountText));
        RefreshSelected();
    }

    private void RefreshSelected()
    {
        Changed(nameof(SelectedDetails));
        Changed(nameof(SelectedSummary));
        Changed(nameof(SelectedFinalResponse));
        Changed(nameof(CanDismissSelected));
        Changed(nameof(CanReviewSelected));
        Changed(nameof(CanDeleteSelected));
        Changed(nameof(ActionHint));
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) ReloadAttempts();
    }

    private async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDismissSelected || SelectedAttempt is not { } row) return;
        var receipt = row.Receipt;
        var response = MessageBox.Show(this,
            $"Dismiss the hold on this exact queue attempt?\n\n" +
            $"Attempt: {receipt.AttemptId}\nProject: {receipt.Snapshot.ProjectName}\n" +
            $"Queue item: {(row.IsOrphan ? "deleted" : "saved")}\n\n" +
            "The saved receipt has no recorded Codex submission start or task identity. That alone cannot prove Codex received no work. " +
            "Check Codex for related active work before releasing the hold. The queue owner will recheck the saved receipt and global execution slot. " +
            "The receipt stays saved, its outcome stays unchanged, and the queue stays paused. No work will be resent. " +
            "Have you confirmed no related task or child work is active?",
            "Dismiss queue attempt hold", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (response == MessageBoxResult.Yes)
            await ApplyActionAsync("dismiss-unsubmitted", receipt);
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReviewSelected || SelectedAttempt is not { } row) return;
        var receipt = row.Receipt;
        var response = MessageBox.Show(this,
            $"Review this exact Codex attempt before releasing its queue hold.\n\n" +
            $"Attempt: {receipt.AttemptId}\nTask: {receipt.ThreadId}\nTurn: {receipt.TurnId}\n\n" +
            "Confirm in Codex that this exact task, turn, and any child work are no longer active. " +
            "Release keeps the receipt and leaves the queue paused. It will not resend this attempt. Have you confirmed all related work is no longer active?",
            "Review queue attempt", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (response == MessageBoxResult.Yes)
            await ApplyActionAsync("review-release", receipt);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteSelected || SelectedAttempt is not { } row) return;
        var receipt = row.Receipt;
        if (MessageBox.Show(this,
                $"Delete '{receipt.Snapshot.Name}' from tracked task history?\n\n" +
                $"Attempt: {receipt.AttemptId}\nProject: {receipt.Snapshot.ProjectName}\n\n" +
                "If its outcome is unresolved, this abandons the queue hold and skips that attempt. " +
                "It does not stop Codex work that may still be running. Queues pause until you resume them. " +
                "The receipt and original outcome remain saved for recovery.",
                "Delete tracked attempt", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _busy = true;
        RefreshSelected();
        Status = "Deleting tracked attempt and releasing its queue hold.";
        try
        {
            await _sourceModel.DeleteTaskActivityAsync(receipt.Snapshot.ProjectId, receipt.AttemptId);
            ReloadAttempts();
            await _sourceModel.RefreshOwnerStatusAsync();
            Status = _sourceModel.Feedback;
        }
        finally
        {
            _busy = false;
            RefreshSelected();
        }
    }

    private async Task ApplyActionAsync(string command, ProjectTaskExecutionReceipt receipt)
    {
        _busy = true;
        RefreshSelected();
        Status = "Contacting queue owner. No attempt will be resent.";
        try
        {
            var reply = await QueueOwnerClient.SendAsync(_store.StorePath, command,
                receipt.Snapshot.ProjectId, startIfMissing: true,
                attemptId: receipt.AttemptId, confirmedNoActiveTask: true);
            ReloadAttempts(receipt.AttemptId);
            _sourceModel.ReloadCommand.Execute(null);
            await _sourceModel.RefreshOwnerStatusAsync();
            Status = reply.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Text.Json.JsonException or OperationCanceledException)
        {
            ReloadAttempts(receipt.AttemptId);
            Status = ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshSelected();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        Status = "Wait for the queue owner to finish this command before closing attempt history.";
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record QueueAttemptHistoryRow(ProjectTaskExecutionReceipt Receipt, bool IsOrphan)
{
    public string Heading => $"{Receipt.Snapshot.Name} · {Receipt.Snapshot.ProjectName}";
    public string Detail => $"{(Receipt.QueueAbandonedAt is not null ? "Deleted / abandoned" :
        Receipt.QueueReviewCompletedAt is not null ? "Reviewed" :
        Receipt.State == ProjectTaskRunState.Completed && Receipt.Outcome == ProjectTaskOutcome.Succeeded ? "Completed" :
        Receipt.FinishedAt is null && Receipt.State is (ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
            ProjectTaskRunState.Running) ? "Unfinished / recovering" : "Hold")}" +
        $" · {Receipt.State} / {Receipt.Outcome}" +
        (Receipt.ActivityDeletedAt is not null ? " · Deleted from task activity" :
         Receipt.ActivityArchivedAt is not null ? " · In History" : "") +
        (IsOrphan ? " · Queue item deleted" : "") +
        $" · {Receipt.CreatedAt.ToLocalTime():g}";
}
