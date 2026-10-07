using System.IO;
using System.Text.Json;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// Single execution owner for the task store. The host owns its lifetime and
/// per-store IPC mutex; this class owns serial selection and durable attempts.
/// </summary>
public sealed class ProjectQueueCoordinator
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(3);
    private const string DashboardRequiredMessage =
        "Waiting for Long Running Task. Keep the main dashboard open and turn on Long Running Task to start enabled queue items. Started tasks continue.";
    private static readonly string GlobalMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "project-queue-execution.json");
    private readonly ProjectTaskStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly Func<ICodexQueueTaskRunner> _runnerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ActiveAttempt? _active;
    private bool _started;
    private bool _waitingOnActivePredecessor;
    private bool _handoffPending;
    private string _ownerStatus = "Queue owner is starting.";

    public ProjectQueueCoordinator(ProjectTaskStore store, SettingsStore settingsStore,
        Func<ICodexQueueTaskRunner> runnerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _runnerFactory = runnerFactory ?? throw new ArgumentNullException(nameof(runnerFactory));
    }

    /// <summary>Completes only after outstanding attempts are held and saved.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Run on the independent queue host, never on the WPF dispatcher.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
        try
        {
            if (_started) throw new InvalidOperationException("The queue coordinator is already running.");
            _started = true;
            var data = LoadWritable();
            var needsMigration = _store.LoadedSourceVersion < 9;
            if (ProjectTaskRecovery.HoldUnfinishedQueueAttempts(data, DateTimeOffset.UtcNow) > 0 || needsMigration)
                _store.Save(data);
            await InspectHeldAttemptsAsync(cancellationToken).ConfigureAwait(false);
            TryClearSettledMarker();
            _ownerStatus = "Queue owner is ready.";
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ownerStatus = "Queue recovery failed. Automatic dispatch is stopped.";
            _ready.TrySetException(ex);
            throw;
        }
        finally { _gate.Release(); }

        while (!cancellationToken.IsCancellationRequested)
        {
            ActiveAttempt? attempt = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { attempt = await TryClaimNextAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // Never dispatch from a stale or unwritable task store.
                _ownerStatus = "Queue selection failed. Review task-store settings and reload the queue owner.";
            }
            finally { _gate.Release(); }

            if (attempt is not null)
            {
                try { await ExecuteAsync(attempt, cancellationToken).ConfigureAwait(false); }
                finally
                {
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(_active, attempt)) _active = null;
                        attempt.ExecutionLock.Dispose();
                    }
                    finally { _gate.Release(); }
                }
                continue;
            }

            try { await _wake.WaitAsync(IdlePollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<ProjectQueueCoordinatorStatus> EnableQueueAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var queue = FindQueue(data, projectId);
            var removed = ProjectQueueMaintenance.RemoveFailedItems(data, projectId, _active?.AttemptId);
            if (removed > 0)
            {
                _store.Save(data);
                TryClearSettledMarker();
            }
            if (HasUnresolvedAttempt(data, _active?.AttemptId))
                throw new InvalidOperationException("An earlier queue attempt needs review before automatic dispatch can resume.");
            if (data.QueueItems.All(item => item.ProjectId != projectId || item.State != ProjectQueueItemState.Pending || !item.Enabled))
            {
                queue.Enabled = false;
                queue.StatusMessage = removed > 0
                    ? $"Moved {removed} failed queue item(s) back to Notes. No enabled pending items remain."
                    : "This queue has no enabled pending item.";
                _store.Save(data);
                return Status(data, projectId);
            }
            ValidateSavedProject(queue, out _);
            if (queue.AutomaticLoopEnabled)
                ProjectAutomaticLoop.ValidateDispatch(queue, data.QueueItems.First(item =>
                    item.ProjectId == projectId && item.State == ProjectQueueItemState.Pending && item.Enabled));
            queue.Enabled = true;
            queue.RecoveryState = ProjectQueueRecoveryState.None;
            queue.StatusMessage = "Queue enabled. The next eligible item can start.";
            _store.Save(data);
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectQueueCoordinatorStatus> PauseQueueAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var queue = FindQueue(data, projectId);
            queue.Enabled = false;
            queue.StatusMessage = "Queue paused. An active task continues until it finishes or is stopped separately.";
            _waitingOnActivePredecessor = false;
            _handoffPending = false;
            _store.Save(data);
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectQueueCoordinatorStatus> PauseAllAsync(bool paused,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            data.PauseAllQueues = paused;
            if (paused) { _waitingOnActivePredecessor = false; _handoffPending = false; }
            _store.Save(data);
            Wake();
            return Status(data, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Delete task activity in one action. An unresolved attempt is abandoned,
    /// not reported as reviewed or completed. Any remaining Codex work may keep
    /// running, so all queues pause until the user explicitly resumes them.
    /// </summary>
    public async Task<ProjectQueueCoordinatorStatus> AbandonAttemptAsync(string projectId,
        string attemptId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active?.AttemptId == attemptId)
                throw new InvalidOperationException("The queue owner still has this attempt active. Stop it before deleting its task history.");
            var data = AbandonAttemptInStoreCore(_store, projectId, attemptId,
                claimOwnerSlot: false);
            _ownerStatus = "Queue owner is ready.";
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Local fallback when an older queue owner cannot accept the Delete command.
    /// The global execution lock excludes a live owned run, and a fresh store load
    /// preserves concurrent-edit detection. No Codex task or turn is interrupted.
    /// </summary>
    public static ProjectTaskData AbandonAttemptInStore(ProjectTaskStore store,
        string projectId, string attemptId) =>
        AbandonAttemptInStoreCore(store, projectId, attemptId, claimOwnerSlot: true);

    private static ProjectTaskData AbandonAttemptInStoreCore(ProjectTaskStore store,
        string projectId, string attemptId, bool claimOwnerSlot)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(attemptId))
            throw new ArgumentException("An exact project and attempt are required to delete task activity.");
        // Only dashboard fallback claims this mutex. The owner process already
        // owns it on its host thread, so an IPC worker cannot claim it again.
        using var ownerClaim = claimOwnerSlot
            ? QueueOwnerClient.TryClaimOwnerMutex(store.StorePath)
                ?? throw new InvalidOperationException("A queue owner is running. Retry Delete through its command channel.")
            : null;
        using var executionLock = TryAcquireExecutionLock()
            ?? throw new InvalidOperationException("A queue attempt still owns the global execution slot. Stop it before deleting its task history.");
        var data = store.Load();
        if (!store.CanSave)
            throw new InvalidOperationException(store.LoadWarning ?? "Task store is unavailable.");
        var receipt = data.Receipts.SingleOrDefault(candidate => candidate.AttemptId == attemptId &&
            candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            candidate.Snapshot.ProjectId == projectId)
            ?? throw new ArgumentException("The exact queue attempt was not found.", nameof(attemptId));
        var marker = ReadGlobalMarker();
        var matchingMarker = marker is not null && marker.AttemptId == attemptId &&
            string.Equals(marker.StorePath, store.StorePath, StringComparison.OrdinalIgnoreCase);
        if (receipt.ActivityDeletedAt is not null)
        {
            if (matchingMarker && (receipt.QueueAbandonedAt is not null ||
                receipt.QueueReviewCompletedAt is not null || IsAutomaticMarkerReleaseAllowed(receipt)))
                File.Delete(GlobalMarkerPath);
            return data;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < receipt.UpdatedAt) now = receipt.UpdatedAt;
        var settled = IsAutomaticMarkerReleaseAllowed(receipt) || receipt.QueueReviewCompletedAt is not null;
        if (!settled)
        {
            receipt.QueueAbandonedAt = now;
            if (receipt.FinishedAt is null) receipt.State = ProjectTaskRunState.NeedsAttention;
            receipt.AttentionReason = "User deleted task activity and abandoned queue tracking. Codex work may still be active; the recorded outcome and identities remain unchanged. This item will not be resent automatically.";
            var item = data.QueueItems.FirstOrDefault(candidate =>
                candidate.Id == receipt.Snapshot.QueueItemId &&
                candidate.ProjectId == projectId && candidate.LastAttemptId == attemptId);
            if (item is not null) item.State = ProjectQueueItemState.NeedsAttention;
            var queue = data.Queues.FirstOrDefault(candidate => candidate.ProjectId == projectId);
            if (queue is not null)
            {
                queue.Enabled = false;
                queue.RecoveryState = data.Receipts.Any(candidate =>
                    candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                    candidate.Snapshot.ProjectId == projectId && candidate.AttemptId != attemptId &&
                    candidate.QueueReviewCompletedAt is null && candidate.QueueAbandonedAt is null &&
                    !IsAutomaticMarkerReleaseAllowed(candidate))
                    ? ProjectQueueRecoveryState.NeedsAttention : ProjectQueueRecoveryState.None;
                queue.StatusMessage = "Task history deleted. Attempt skipped. Resume all queues, then enable this queue for later pending items.";
            }
            data.PauseAllQueues = true;
        }
        receipt.ActivityDeletedAt = now;
        if (receipt.NotificationState == ProjectTaskNotificationState.Pending)
            receipt.NotificationState = ProjectTaskNotificationState.NotPending;
        receipt.UpdatedAt = now;
        store.Save(data);
        // An abandoned Codex task or child may still need staged images.
        // Deleting launcher activity must not remove its input files.
        if (receipt.QueueAbandonedAt is null)
            QueueImageStaging.Cleanup(receipt.Snapshot);
        if (matchingMarker && (receipt.QueueAbandonedAt is not null || settled))
            File.Delete(GlobalMarkerPath);
        return data;
    }

    /// <summary>
    /// A human must confirm the uncertain Codex task is no longer active.
    /// The attempt's unknown outcome is retained and its item remains skipped.
    /// </summary>
    public async Task<ProjectQueueCoordinatorStatus> ReleaseAttemptAfterReviewAsync(
        string projectId, string attemptId, bool confirmedNoActiveTask,
        CancellationToken cancellationToken = default)
    {
        if (!confirmedNoActiveTask)
            throw new InvalidOperationException("Confirm that the exact Codex task is no longer active before releasing this queue hold.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = data.Receipts.SingleOrDefault(candidate => candidate.AttemptId == attemptId &&
                candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                candidate.Snapshot.ProjectId == projectId)
                ?? throw new ArgumentException("The exact queue attempt was not found.", nameof(attemptId));
            var queue = data.Queues.FirstOrDefault(candidate => candidate.ProjectId == projectId);
            if (_active?.AttemptId == attemptId)
                throw new InvalidOperationException("The queue owner still has this attempt active. Stop it and await a terminal result first.");
            if (string.IsNullOrWhiteSpace(receipt.ThreadId) || string.IsNullOrWhiteSpace(receipt.TurnId))
                throw new InvalidOperationException(receipt.SubmissionStartedAt is null && receipt.StartedAt is null
                    ? "No Codex submission start or task identity was recorded. Inspect Codex for related active work before dismissing this hold."
                    : "Codex submission may have started, but the exact task and turn IDs are missing. This hold cannot be released by exact-task review.");
            if (receipt.QueueReviewCompletedAt != null || receipt.QueueAbandonedAt != null ||
                IsAutomaticMarkerReleaseAllowed(receipt) ||
                receipt.State is not (ProjectTaskRunState.NeedsAttention or ProjectTaskRunState.Recovering or
                    ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed))
                throw new InvalidOperationException("This attempt is not an unreleased non-success queue hold.");
            receipt.QueueReviewCompletedAt = DateTimeOffset.UtcNow;
            if (receipt.FinishedAt is null) receipt.State = ProjectTaskRunState.NeedsAttention;
            receipt.AttentionReason = receipt.FinishedAt is null
                ? "User confirmed this exact Codex task and child work are no longer active. Its outcome remains unverified; this item will not be resent."
                : "User confirmed this exact Codex task and child work are no longer active. The recorded non-success result is unchanged.";
            receipt.UpdatedAt = receipt.QueueReviewCompletedAt.Value;
            var item = data.QueueItems.FirstOrDefault(candidate => candidate.Id == receipt.Snapshot.QueueItemId);
            if (item is not null) item.State = ProjectQueueItemState.NeedsAttention;
            if (queue is not null)
            {
                queue.Enabled = false;
                queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
                queue.StatusMessage = "Attempt reviewed. It remains skipped. Use Enable Queue to continue with later pending items.";
            }
            _store.Save(data);
            QueueImageStaging.Cleanup(receipt.Snapshot);
            TryClearSettledMarker();
            _ownerStatus = "Queue owner is ready.";
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Explicitly dismiss a receipt with no recorded pre-submission milestone
    /// after the user confirms no related Codex work remains active. The receipt
    /// stays in history and the queue remains paused. No task is retried.
    /// </summary>
    public async Task<ProjectQueueCoordinatorStatus> DismissUnsubmittedAttemptAsync(
        string projectId, string attemptId, bool confirmedNoRelatedActiveWork,
        CancellationToken cancellationToken = default)
    {
        if (!confirmedNoRelatedActiveWork)
            throw new InvalidOperationException("Confirm no related Codex task or child work is active before releasing this queue hold.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = data.Receipts.SingleOrDefault(candidate => candidate.AttemptId == attemptId &&
                candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                candidate.Snapshot.ProjectId == projectId)
                ?? throw new ArgumentException("The exact queue attempt was not found.", nameof(attemptId));
            if (_active?.AttemptId == attemptId)
                throw new InvalidOperationException("The queue owner still has this attempt active. Stop it before dismissing its receipt.");
            if (receipt.QueueReviewCompletedAt is not null || receipt.QueueAbandonedAt is not null ||
                IsAutomaticMarkerReleaseAllowed(receipt) ||
                receipt.SubmissionStartedAt is not null || receipt.StartedAt is not null ||
                receipt.ThreadId is not null || receipt.TurnId is not null ||
                receipt.State is not (ProjectTaskRunState.Prepared or ProjectTaskRunState.Recovering or
                    ProjectTaskRunState.NeedsAttention or ProjectTaskRunState.Failed or
                    ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed))
                throw new InvalidOperationException("This attempt has possible Codex submission evidence or is already settled. Review its exact task and turn instead.");

            // The global lock excludes another queue owner in this Windows user
            // session. A matching marker may then be retired only after save.
            using var executionLock = TryAcquireExecutionLock()
                ?? throw new InvalidOperationException("Another queue attempt owns the global execution slot. Wait for it to finish before dismissing this hold.");
            var marker = ReadGlobalMarker();
            if (marker is not null && (!string.Equals(marker.StorePath, _store.StorePath,
                    StringComparison.OrdinalIgnoreCase) || marker.AttemptId != attemptId))
                throw new InvalidOperationException("The global execution marker belongs to another attempt. Review that attempt before dismissing this hold.");

            receipt.QueueReviewCompletedAt = DateTimeOffset.UtcNow;
            if (receipt.FinishedAt is null) receipt.State = ProjectTaskRunState.NeedsAttention;
            receipt.AttentionReason = "User confirmed no related Codex work is active and dismissed this hold. No submission start or task identity was recorded; the saved outcome is unchanged.";
            receipt.UpdatedAt = receipt.QueueReviewCompletedAt.Value;
            var item = data.QueueItems.FirstOrDefault(candidate => candidate.Id == receipt.Snapshot.QueueItemId);
            if (item is not null) item.State = ProjectQueueItemState.NeedsAttention;
            var queue = data.Queues.FirstOrDefault(candidate => candidate.ProjectId == projectId);
            if (queue is not null)
            {
                queue.Enabled = false;
                queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
                queue.StatusMessage = "Attempt hold dismissed after user review. No submission was recorded; the receipt remains saved and the queue stays paused.";
            }
            _store.Save(data);
            QueueImageStaging.Cleanup(receipt.Snapshot);
            if (marker is not null) ClearMarkerForSettledReceipt(receipt);
            _ownerStatus = "Queue owner is ready.";
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// An explicit retry creates a future new attempt. The previous receipt
    /// remains immutable and no work starts until auto-run is enabled again.
    /// </summary>
    public async Task<ProjectQueueCoordinatorStatus> RetryItemAsync(string projectId,
        string itemId, string expectedAttemptId, bool confirmedRetry, CancellationToken cancellationToken = default)
    {
        if (!confirmedRetry)
            throw new InvalidOperationException("Confirm an explicit new attempt before retrying this queue item.");
        if (string.IsNullOrWhiteSpace(expectedAttemptId))
            throw new InvalidOperationException("The exact previous attempt is required before retrying this queue item.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var queue = FindQueue(data, projectId);
            var item = data.QueueItems.SingleOrDefault(candidate => candidate.Id == itemId &&
                candidate.ProjectId == projectId)
                ?? throw new ArgumentException("The exact queue item was not found.", nameof(itemId));
            if (_active?.ProjectId == projectId)
                throw new InvalidOperationException("Stop or finish the active task before retrying an item in this queue.");
            if (item.State is not (ProjectQueueItemState.Failed or ProjectQueueItemState.Interrupted or
                    ProjectQueueItemState.NeedsAttention) || item.LastAttemptId is null)
                throw new InvalidOperationException("This queue item has no failed or reviewed attempt to retry.");
            if (!string.Equals(item.LastAttemptId, expectedAttemptId, StringComparison.Ordinal))
                throw new InvalidOperationException("The queue item's last attempt changed. Refresh before retrying.");
            var receipt = data.Receipts.SingleOrDefault(candidate => candidate.AttemptId == item.LastAttemptId &&
                candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                candidate.Snapshot.QueueItemId == item.Id)
                ?? throw new InvalidOperationException("The item does not have its exact prior receipt.");
            if (receipt.QueueAbandonedAt is not null || IsAutomaticMarkerReleaseAllowed(receipt) ||
                receipt.QueueReviewCompletedAt is null)
                throw new InvalidOperationException("Complete the exact prior attempt review before an explicit retry.");
            item.State = ProjectQueueItemState.Pending;
            item.LastAttemptId = null;
            queue.Enabled = false;
            queue.RecoveryState = ProjectQueueRecoveryState.None;
            queue.StatusMessage = "Item reset for an explicit new attempt. Use Enable Queue separately when ready.";
            _store.Save(data);
            Wake();
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Pause future work first, then request interruption of the one owned run.</summary>
    public async Task<ProjectQueueCoordinatorStatus> StopCurrentAsync(string projectId, string expectedAttemptId,
        CancellationToken cancellationToken = default)
    {
        ICodexQueueTaskRunner? runner;
        ProjectQueueCoordinatorStatus status;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var queue = FindQueue(data, projectId);
            if (_active is null || _active.ProjectId != projectId ||
                _active.AttemptId != expectedAttemptId)
                throw new InvalidOperationException("The active attempt changed. Refresh status before stopping a task.");
            queue.Enabled = false;
            queue.StatusMessage = "Stop requested for the current task. Waiting for an exact terminal result.";
            _store.Save(data);
            runner = _active.Runner;
            status = Status(data, projectId);
        }
        finally { _gate.Release(); }
        await runner.StopCurrentAsync(cancellationToken).ConfigureAwait(false);
        Wake();
        return status;
    }

    public async Task<ProjectQueueCoordinatorStatus> GetStatusAsync(string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = _store.Load();
            if (!_store.CanSave) return new(false, false, false, _active is not null,
                _active is not null && (projectId is null || _active.ProjectId == projectId),
                _active?.ProjectId, _active?.AttemptId, _store.LoadWarning ?? "Task store unavailable.");
            return Status(data, projectId);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ProjectQueueNotification>> GetPendingNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            return data.Receipts.Where(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                    receipt.NotificationState == ProjectTaskNotificationState.Pending &&
                    !string.IsNullOrWhiteSpace(receipt.CompletionMessage))
                .OrderBy(receipt => receipt.FinishedAt).ThenBy(receipt => receipt.AttemptId, StringComparer.Ordinal)
                .Select(receipt => new ProjectQueueNotification(receipt.AttemptId, receipt.Snapshot.ProjectId,
                    receipt.CompletionMessage, receipt.FinishedAt ?? receipt.UpdatedAt)).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Claim one notification durably before the host tries to display it.
    /// A crash may suppress a popup, but cannot produce duplicate popups.
    /// Its completion message remains in the receipt either way.
    /// </summary>
    public async Task<ProjectQueueNotification?> ClaimNextPendingNotificationAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = data.Receipts.Where(candidate => candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                    candidate.NotificationState == ProjectTaskNotificationState.Pending &&
                    !string.IsNullOrWhiteSpace(candidate.CompletionMessage))
                .OrderBy(candidate => candidate.FinishedAt).ThenBy(candidate => candidate.AttemptId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (receipt is null) return null;
            receipt.NotificationState = ProjectTaskNotificationState.Attempted;
            receipt.NotificationAttemptedAt = DateTimeOffset.UtcNow;
            receipt.UpdatedAt = receipt.NotificationAttemptedAt.Value;
            _store.Save(data);
            return new(receipt.AttemptId, receipt.Snapshot.ProjectId,
                receipt.CompletionMessage, receipt.FinishedAt ?? receipt.UpdatedAt);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkNotificationAttemptedAsync(string attemptId, string? error = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = data.Receipts.SingleOrDefault(receipt => receipt.AttemptId == attemptId &&
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem)
                ?? throw new ArgumentException("The queue attempt no longer exists.", nameof(attemptId));
            if (receipt.NotificationState == ProjectTaskNotificationState.Attempted) return;
            if (receipt.NotificationState != ProjectTaskNotificationState.Pending)
                throw new InvalidOperationException("This attempt has no pending completion notification.");
            receipt.NotificationState = ProjectTaskNotificationState.Attempted;
            receipt.NotificationAttemptedAt = DateTimeOffset.UtcNow;
            receipt.NotificationError = error is null ? "" : error[..Math.Min(error.Length, 500)];
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            _store.Save(data);
        }
        finally { _gate.Release(); }
    }

    private async Task<ActiveAttempt?> TryClaimNextAsync(CancellationToken cancellationToken)
    {
        _waitingOnActivePredecessor = false;
        _handoffPending = false;
        var data = LoadWritable();
        if (data.PauseAllQueues) return null;
        if (HasUnresolvedAttempt(data))
        {
            _ownerStatus = "An unresolved queue attempt needs explicit review before further dispatch.";
            return null;
        }
        if (!QueueDashboardPermit.IsEnabled(_settingsStore.SettingsPath))
        {
            _ownerStatus = DashboardRequiredMessage;
            return null;
        }
        _ownerStatus = "Queue owner is ready.";

        ProjectQueueConfiguration? chosenQueue = null;
        ProjectQueueItem? chosenItem = null;
        foreach (var queue in data.Queues.OrderBy(candidate => candidate.ProjectId, StringComparer.Ordinal))
        {
            if (!queue.Enabled || queue.RecoveryState != ProjectQueueRecoveryState.None) continue;
            var item = data.QueueItems.Where(candidate => candidate.ProjectId == queue.ProjectId &&
                    candidate.Enabled && candidate.State == ProjectQueueItemState.Pending)
                .OrderBy(candidate => candidate.Order).ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (item is null) continue;
            if (queue.ExternalPredecessor is { } predecessor && queue.ExternalPredecessorSatisfiedAt is null)
            {
                CodexExternalTurnObservation observation;
                try
                {
                    observation = await _runnerFactory().InspectExternalPredecessorAsync(predecessor,
                        queue.AssignedFolder, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                    or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    observation = new(CodexExternalTurnState.Unknown, predecessor.ThreadId,
                        predecessor.TurnId, "The selected predecessor could not be observed.");
                }
                if (observation.State == CodexExternalTurnState.Pending)
                {
                    _waitingOnActivePredecessor |= observation.IsRunning;
                    var waiting = $"Waiting for exact Codex task {ShortId(predecessor.ThreadId)} and turn {ShortId(predecessor.TurnId)}. Its observed completion is required.";
                    if (queue.StatusMessage != waiting)
                    {
                        queue.StatusMessage = waiting;
                        _store.Save(data);
                    }
                    continue; // Other project queues may run while this one waits.
                }
                if (observation.State != CodexExternalTurnState.Completed || !observation.TerminalConfirmed ||
                    observation.ThreadId != predecessor.ThreadId || observation.TurnId != predecessor.TurnId)
                {
                    queue.Enabled = false;
                    queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
                    queue.StatusMessage = "The selected predecessor did not have a confirmed exact completed turn. Review it before enabling the queue.";
                    _store.Save(data);
                    continue;
                }
                queue.ExternalPredecessorSatisfiedAt = DateTimeOffset.UtcNow;
                queue.StatusMessage = "Selected predecessor's exact turn completed. This is observed lifecycle status, not proof of task success.";
                _store.Save(data);
            }
            if (ProjectQueueDelay.IsWaiting(data, queue, DateTimeOffset.UtcNow))
            {
                _handoffPending = true; // Keep the owner awake across a delayed handoff.
                continue; // Other project queues can use the execution slot during this wait.
            }
            chosenQueue = queue;
            chosenItem = item;
            break;
        }
        if (chosenQueue is null || chosenItem is null) return null;
        var choice = (queue: chosenQueue, item: chosenItem);

        // One global Windows-user lock serializes all queue owners, including
        // explicit --settings stores pointing at overlapping checkouts.
        var executionLock = TryAcquireExecutionLock();
        if (executionLock is null)
        {
            _ownerStatus = "Another launcher queue owns the execution slot.";
            return null;
        }

        try
        {
            if (!CanClaimGlobalSlot(data)) return null;
            // Settings are loaded at claim time. A selected dashboard project
            // cannot substitute for the saved project identity.
            if (!TryValidateClaim(data, choice.queue, choice.item, out var note,
                    out var profile, out var reason))
            {
                choice.queue.Enabled = false;
                choice.queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
                choice.queue.StatusMessage = reason;
                _store.Save(data);
                return null;
            }

            // The saved queue choice is the single visible setting. Older item
            // choices remain a fallback for stores created before this UI change.
            var hasQueueChoice = !string.IsNullOrWhiteSpace(choice.queue.DefaultModelId) &&
                !string.IsNullOrWhiteSpace(choice.queue.DefaultReasoningEffort);
            var model = choice.queue.AutomaticLoopEnabled ? choice.queue.AutomaticLoopModelId :
                hasQueueChoice ? choice.queue.DefaultModelId : choice.item.ModelId;
            var effort = choice.queue.AutomaticLoopEnabled ? choice.queue.AutomaticLoopReasoningEffort :
                hasQueueChoice ? choice.queue.DefaultReasoningEffort : choice.item.ReasoningEffort;
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort))
            {
                choice.queue.Enabled = false;
                choice.queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
                choice.queue.StatusMessage = "Choose a Codex model and thinking level for this queue, then use Enable Queue again.";
                _store.Save(data);
                return null;
            }

            var dispatchFolder = ProjectAutomaticLoop.ValidateDispatch(choice.queue, choice.item);
            var loopSeed = ProjectAutomaticLoop.IsContinuation(choice.item)
                ? ProjectAutomaticLoop.Seed(data, choice.queue) : null;
            if (ProjectAutomaticLoop.IsContinuation(choice.item) && loopSeed is null)
                throw new InvalidOperationException("Automatic Loop Mode requires a successful manual seed before running a continuation.");
            var loopPredecessor = loopSeed is null ? null : ProjectAutomaticLoop.Predecessor(data, choice.item);
            var dispatchImages = loopSeed?.Snapshot.Images ?? note!.Images;
            var snapshot = new ProjectTaskDispatchSnapshot
            {
                ProjectId = profile!.Id,
                ProjectName = profile.Name,
                NoteId = note!.Id,
                QueueItemId = choice.item.Id,
                Name = note.Name,
                Prompt = note.AgentSource == null ? note.Prompt : AgentPromptSummary.EnsureSummary(note.Name, note.Prompt),
                Images = dispatchImages.Select(image => new ProjectTaskNoteImage
                {
                    Id = image.Id, Caption = image.Caption, MimeType = image.MimeType,
                    DataBase64 = image.DataBase64, PageUrl = image.PageUrl,
                    PageHtml = image.PageHtml, PageCss = image.PageCss,
                    PageCaptureStatus = image.PageCaptureStatus,
                    IncludePageContextInPrompt = image.IncludePageContextInPrompt
                }).ToList(),
                ImageStagingId = dispatchImages.Count == 0 ? "" : Guid.NewGuid().ToString("N"),
                ModelId = model,
                ReasoningEffort = effort,
                Folder = dispatchFolder,
                PredecessorHandoff = FindPredecessorHandoff(data, choice.item),
                AutomaticLoopChainId = choice.queue.AutomaticLoopEnabled ? choice.queue.AutomaticLoopChainId : "",
                AutomaticLoopSeedEligible = choice.queue.AutomaticLoopEnabled &&
                    !ProjectAutomaticLoop.IsContinuation(choice.item) && note.AgentSource is null,
                AutomaticLoopSeedAttemptId = loopSeed?.AttemptId ?? "",
                AutomaticLoopPredecessorAttemptId = loopPredecessor?.AttemptId ?? "",
                AutomaticLoopAppGoal = choice.queue.AutomaticLoopEnabled ? choice.queue.AutomaticLoopAppGoal : "",
                AutomaticLoopSeedPrompt = loopSeed?.Snapshot.Prompt ?? "",
                AutomaticLoopPreviousResult = loopPredecessor is null ? "" :
                    loopPredecessor.ResultSummary + "\n\n" + loopPredecessor.FinalResponse
            };
            var receipt = new ProjectTaskExecutionReceipt { Snapshot = snapshot };
            // Recheck after predecessor inspection and project validation. Closing
            // or disabling the dashboard never changes pending intent or receipts.
            if (!QueueDashboardPermit.IsEnabled(_settingsStore.SettingsPath))
            {
                _waitingOnActivePredecessor = false;
                _handoffPending = false;
                _ownerStatus = DashboardRequiredMessage;
                return null;
            }
            choice.item.State = ProjectQueueItemState.Starting;
            choice.item.LastAttemptId = receipt.AttemptId;
            choice.queue.StatusMessage = $"Starting task: {snapshot.Name}";
            data.Receipts.Add(receipt);
            _store.Save(data); // Intent and frozen content precede all Codex requests.
            WriteGlobalMarker(new(_store.StorePath, receipt.AttemptId, profile.Id,
                snapshot.Folder, DateTimeOffset.UtcNow));
            var attempt = new ActiveAttempt(profile.Id, receipt.AttemptId, snapshot,
                _runnerFactory(), executionLock);
            _active = attempt;
            _ownerStatus = choice.queue.StatusMessage;
            return attempt;
        }
        finally
        {
            if (_active is null || !ReferenceEquals(_active.ExecutionLock, executionLock))
                executionLock.Dispose();
        }
    }

    private async Task ExecuteAsync(ActiveAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            var result = await attempt.Runner.RunAsync(attempt.Snapshot,
                update => PersistUpdateAsync(attempt, update), cancellationToken).ConfigureAwait(false);
            var recovered = false;
            if (!result.TerminalConfirmed && !cancellationToken.IsCancellationRequested &&
                !string.IsNullOrWhiteSpace(result.ThreadId) && !string.IsNullOrWhiteSpace(result.TurnId))
            {
                // Transport loss may follow an exact terminal turn. Read its saved
                // identity, frozen input, and outcome before retaining an unknown hold.
                var observed = await _runnerFactory().InspectExactAsync(attempt.Snapshot,
                    result.ThreadId, result.TurnId, cancellationToken).ConfigureAwait(false);
                if (observed.TerminalConfirmed && observed.ThreadId == result.ThreadId &&
                    observed.TurnId == result.TurnId)
                {
                    result = observed;
                    recovered = true;
                }
            }
            await PersistResultAsync(attempt, result, recovered).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or OperationCanceledException)
        {
            // A failed save, transport loss, or owner shutdown after a possible
            // submission must never select another item or resend this attempt.
            await HoldUncertainAsync(attempt).ConfigureAwait(false);
        }
    }

    private async Task PersistUpdateAsync(ActiveAttempt attempt, CodexQueueRunUpdate update)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = FindReceipt(data, attempt);
            var item = data.QueueItems.FirstOrDefault(item => item.Id == attempt.Snapshot.QueueItemId);
            var queue = FindQueue(data, attempt.ProjectId);
            ApplyIdentity(receipt, update.ThreadId, update.TurnId);
            if (!string.IsNullOrEmpty(update.ConnectionDetails))
            {
                if (receipt.SubmissionStartedAt is not null && receipt.ConnectionDetails != update.ConnectionDetails)
                    throw new InvalidOperationException("The submitted attempt's access policy cannot change.");
                receipt.ConnectionDetails = update.ConnectionDetails;
            }
            var now = DateTimeOffset.UtcNow;
            if (update.Stage == CodexQueueRunStage.CommandAccessChecking)
                queue.StatusMessage = "Checking command access before task creation.";
            if (update.Stage == CodexQueueRunStage.ThreadSubmissionStarting)
                receipt.SubmissionStartedAt ??= now;
            if (update.Stage == CodexQueueRunStage.Running)
            {
                receipt.State = ProjectTaskRunState.Running;
                receipt.StartedAt ??= now;
                if (item is not null) item.State = ProjectQueueItemState.Running;
                queue.StatusMessage = $"Running: {attempt.Snapshot.Name}";
            }
            else if (update.Stage == CodexQueueRunStage.Terminal)
                ApplyTerminal(data, receipt, item, queue, update.State, update.Outcome,
                    update.TerminalConfirmed, update.Summary, update.FinalResponse);
            else if (receipt.State == ProjectTaskRunState.Prepared)
                receipt.State = ProjectTaskRunState.Starting;
            receipt.UpdatedAt = now;
            if (update.Stage == CodexQueueRunStage.Terminal)
                _handoffPending = receipt.State == ProjectTaskRunState.Completed &&
                    receipt.Outcome == ProjectTaskOutcome.Succeeded && queue.Enabled &&
                    data.QueueItems.Any(candidate => candidate.ProjectId == queue.ProjectId && candidate.Enabled &&
                        candidate.State == ProjectQueueItemState.Pending);
            _store.Save(data);
            if (IsAutomaticMarkerReleaseAllowed(receipt)) ClearMarkerForSettledReceipt(receipt);
        }
        finally { _gate.Release(); }
    }

    private async Task PersistResultAsync(ActiveAttempt attempt, CodexQueueRunResult result,
        bool recovered = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = LoadWritable();
            var receipt = FindReceipt(data, attempt);
            if (receipt.FinishedAt is not null) return; // Terminal callback already committed.
            var item = data.QueueItems.FirstOrDefault(item => item.Id == attempt.Snapshot.QueueItemId);
            var queue = FindQueue(data, attempt.ProjectId);
            ApplyIdentity(receipt, result.ThreadId, result.TurnId);
            if (recovered) queue.Enabled = false; // Recovery never silently advances the queue.
            ApplyTerminal(data, receipt, item, queue, result.State, result.Outcome,
                result.TerminalConfirmed, result.Summary, result.FinalResponse, allowLoopContinuation: !recovered);
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            _handoffPending = receipt.State == ProjectTaskRunState.Completed &&
                receipt.Outcome == ProjectTaskOutcome.Succeeded && queue.Enabled &&
                data.QueueItems.Any(candidate => candidate.ProjectId == queue.ProjectId && candidate.Enabled &&
                    candidate.State == ProjectQueueItemState.Pending);
            _store.Save(data);
            if (recovered && result.TerminalConfirmed)
                QueueImageStaging.Cleanup(receipt.Snapshot);
            if (IsAutomaticMarkerReleaseAllowed(receipt)) ClearMarkerForSettledReceipt(receipt);
        }
        finally { _gate.Release(); }
    }

    private async Task HoldUncertainAsync(ActiveAttempt attempt)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                var data = LoadWritable();
                var receipt = FindReceipt(data, attempt);
                if (receipt.FinishedAt is not null) return;
                var queue = FindQueue(data, attempt.ProjectId);
                var item = data.QueueItems.FirstOrDefault(item => item.Id == attempt.Snapshot.QueueItemId);
                HoldForReview(receipt, item, queue,
                    "Queue result or receipt persistence is uncertain. Review the retained Codex task and turn IDs before any new dispatch.");
                _store.Save(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                _ownerStatus = "Queue receipt could not be saved. Dispatch is suspended until the owner restarts and recovers the task store.";
            }
        }
        finally { _gate.Release(); }
    }

    private async Task InspectHeldAttemptsAsync(CancellationToken cancellationToken)
    {
        var data = LoadWritable();
        foreach (var held in data.Receipts.Where(receipt =>
                     receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                     receipt.FinishedAt is null && receipt.QueueReviewCompletedAt is null &&
                     receipt.QueueAbandonedAt is null &&
                     receipt.State is ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(held.ThreadId) || string.IsNullOrWhiteSpace(held.TurnId))
                continue; // Exact read-only reconciliation needs both IDs.
            CodexQueueRunResult result;
            try
            {
                result = await _runnerFactory().InspectExactAsync(held.Snapshot,
                    held.ThreadId, held.TurnId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                continue; // Keep the persisted recovery hold; never resend.
            }
            if (!result.TerminalConfirmed) continue;
            var receipt = data.Receipts.Single(candidate => candidate.AttemptId == held.AttemptId);
            var queue = data.Queues.FirstOrDefault(candidate => candidate.ProjectId == receipt.Snapshot.ProjectId);
            if (queue is null) continue;
            try { ApplyIdentity(receipt, result.ThreadId, result.TurnId); }
            catch (InvalidOperationException) { continue; }
            var item = data.QueueItems.FirstOrDefault(candidate => candidate.Id == receipt.Snapshot.QueueItemId);
            queue.Enabled = false; // Recovery never silently resumes a queue.
            ApplyTerminal(data, receipt, item, queue, result.State, result.Outcome,
                result.TerminalConfirmed, result.Summary, result.FinalResponse, allowLoopContinuation: false);
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            _store.Save(data);
            QueueImageStaging.Cleanup(receipt.Snapshot);
            data = LoadWritable();
        }
    }

    private bool TryValidateClaim(ProjectTaskData data, ProjectQueueConfiguration queue,
        ProjectQueueItem item, out ProjectTaskNote? note, out ProjectProfile? profile, out string reason)
    {
        note = data.Notes.FirstOrDefault(candidate => candidate.Id == item.NoteId && candidate.ProjectId == item.ProjectId);
        profile = null;
        reason = "";
        if (note is null || note.IsCompleted || note.IsArchived)
        {
            reason = "The next queue note is missing, complete, or archived. Review its queue entry.";
            return false;
        }
        if (queue.ExternalPredecessor is not null && queue.ExternalPredecessorSatisfiedAt is null)
        {
            reason = "The selected external predecessor has not reached a confirmed exact completed turn.";
            return false;
        }
        string folder;
        try { profile = ValidateSavedProject(queue, out folder); }
        catch (InvalidOperationException ex) { reason = ex.Message; return false; }
        if (!Directory.Exists(folder))
        {
            reason = "The assigned queue folder is unavailable. Review the project folder before auto-run.";
            return false;
        }
        try { ProjectAutomaticLoop.ValidateDispatch(queue, item); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            reason = ex is InvalidOperationException or ArgumentException ? ex.Message :
                "Automatic Loop Mode folder could not be validated. Review the saved folder before resuming.";
            return false;
        }
        return true;
    }

    private ProjectProfile ValidateSavedProject(ProjectQueueConfiguration queue, out string folder)
    {
        folder = NormalizeFolder(queue.AssignedFolder);
        if (!File.Exists(_settingsStore.SettingsPath))
            throw new InvalidOperationException("Saved launcher settings are unavailable. Restore the project library before auto-run.");
        var settings = _settingsStore.Load();
        if (_settingsStore.LoadWarning is not null)
            throw new InvalidOperationException("Saved launcher settings could not be read. Restore them before auto-run.");
        var profile = settings.Projects.SingleOrDefault(project => project.Id == queue.ProjectId)
            ?? throw new InvalidOperationException("The saved launcher project is missing. Review the queue assignment before auto-run.");
        var currentFolder = NormalizeFolder(_settingsStore.ResolveRoot(profile));
        if (!string.Equals(folder, currentFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved project folder changed. Use current project folder in Notes & queue before auto-run.");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("The saved project has no valid name. Review its profile before auto-run.");
        return profile;
    }

    private static void ApplyIdentity(ProjectTaskExecutionReceipt receipt, string? threadId, string? turnId)
    {
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            if (receipt.ThreadId is not null && receipt.ThreadId != threadId)
                throw new InvalidOperationException("Codex returned a different task ID for this attempt.");
            receipt.ThreadId = threadId;
        }
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            if (receipt.TurnId is not null && receipt.TurnId != turnId)
                throw new InvalidOperationException("Codex returned a different turn ID for this attempt.");
            receipt.TurnId = turnId;
        }
    }

    private static void ApplyTerminal(ProjectTaskData data, ProjectTaskExecutionReceipt receipt,
        ProjectQueueItem? item, ProjectQueueConfiguration queue, ProjectTaskRunState state,
        ProjectTaskOutcome outcome, bool terminalConfirmed, string summary, string finalResponse,
        bool allowLoopContinuation = true)
    {
        var confirmedSuccess = terminalConfirmed && state == ProjectTaskRunState.Completed &&
            outcome == ProjectTaskOutcome.Succeeded && receipt.ThreadId is not null && receipt.TurnId is not null;
        receipt.State = confirmedSuccess ? ProjectTaskRunState.Completed :
            terminalConfirmed && state is ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted
                ? state : ProjectTaskRunState.NeedsAttention;
        receipt.Outcome = confirmedSuccess ? ProjectTaskOutcome.Succeeded : outcome;
        receipt.ResultSummary = Limit(summary, 4000);
        receipt.FinalResponse = Limit(finalResponse, 16000);
        receipt.UpdatedAt = DateTimeOffset.UtcNow;
        if (terminalConfirmed) receipt.FinishedAt ??= receipt.UpdatedAt;
        if (item is not null)
            item.State = confirmedSuccess ? ProjectQueueItemState.Completed :
                receipt.State == ProjectTaskRunState.Failed ? ProjectQueueItemState.Failed :
                receipt.State == ProjectTaskRunState.Interrupted ? ProjectQueueItemState.Interrupted :
                ProjectQueueItemState.NeedsAttention;
        if (confirmedSuccess)
        {
            string? loopError = null;
            if (allowLoopContinuation)
            {
                try { ProjectAutomaticLoop.TryPrepareContinuation(data, queue, receipt); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // Keep the confirmed terminal result. A continuation failure
                    // pauses later work without rewriting this successful attempt.
                    queue.Enabled = false;
                    loopError = ex is InvalidOperationException or ArgumentException ? ex.Message :
                        "Automatic Loop Mode could not prepare its next prompt. Review the folder and task store before resuming.";
                }
            }
            var hasNext = data.QueueItems.Any(candidate => candidate.ProjectId == queue.ProjectId &&
                candidate.Enabled && candidate.State == ProjectQueueItemState.Pending);
            if (queue.Enabled && !hasNext)
            {
                queue.Enabled = false;
                queue.StatusMessage = "Queue complete. Use Enable Queue explicitly for later work.";
            }
            else
                queue.StatusMessage = queue.Enabled
                    ? $"Recently completed: {receipt.Snapshot.Name}"
                    : "Queue paused. The current task completed.";
            queue.RecoveryState = loopError is null ? ProjectQueueRecoveryState.None : ProjectQueueRecoveryState.NeedsAttention;
            if (loopError is not null) queue.StatusMessage = loopError;
        }
        else
        {
            queue.Enabled = false;
            queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
            queue.StatusMessage = $"Needs attention: {receipt.Snapshot.Name}. {receipt.ResultSummary}";
            receipt.AttentionReason = queue.StatusMessage;
        }
        if (terminalConfirmed)
        {
            var finishedUtc = receipt.FinishedAt!.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
            var resultLabel = confirmedSuccess ? "Completed" : "Finished with attention needed";
            receipt.CompletionMessage =
                $"{receipt.Snapshot.ProjectName}: {receipt.Snapshot.Name}\n{resultLabel} at {finishedUtc}.\n{Limit(receipt.ResultSummary, 240)}";
            receipt.NotificationState = ProjectTaskNotificationState.Pending;
        }
    }

    private static void HoldForReview(ProjectTaskExecutionReceipt receipt, ProjectQueueItem? item,
        ProjectQueueConfiguration queue, string reason)
    {
        receipt.State = ProjectTaskRunState.NeedsAttention;
        receipt.Outcome = ProjectTaskOutcome.Unknown;
        receipt.AttentionReason = reason;
        receipt.UpdatedAt = DateTimeOffset.UtcNow;
        if (item is not null) item.State = ProjectQueueItemState.NeedsAttention;
        queue.Enabled = false;
        queue.RecoveryState = ProjectQueueRecoveryState.NeedsAttention;
        queue.StatusMessage = reason;
    }

    private static bool HasUnresolvedAttempt(ProjectTaskData data, string? ownedAttemptId = null) =>
        data.Receipts.Any(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            receipt.QueueReviewCompletedAt is null && receipt.QueueAbandonedAt is null &&
            receipt.AttemptId != ownedAttemptId &&
            !IsAutomaticMarkerReleaseAllowed(receipt));

    private static bool IsAutomaticMarkerReleaseAllowed(ProjectTaskExecutionReceipt receipt) =>
        receipt.FinishedAt is not null && receipt.State == ProjectTaskRunState.Completed &&
        receipt.Outcome == ProjectTaskOutcome.Succeeded && receipt.ThreadId is not null && receipt.TurnId is not null;

    private static ProjectTaskExecutionReceipt FindReceipt(ProjectTaskData data, ActiveAttempt attempt) =>
        data.Receipts.SingleOrDefault(receipt => receipt.AttemptId == attempt.AttemptId &&
            receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            ProjectTaskStore.SnapshotsEqual(receipt.Snapshot, attempt.Snapshot))
        ?? throw new InvalidOperationException("The active queue receipt changed or disappeared.");

    private static string FindPredecessorHandoff(ProjectTaskData data, ProjectQueueItem next)
    {
        var predecessor = data.QueueItems.Where(item => item.ProjectId == next.ProjectId &&
                item.Order < next.Order && item.State == ProjectQueueItemState.Completed &&
                item.LastAttemptId is not null)
            .OrderByDescending(item => item.Order).ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (predecessor is null) return "";
        var receipt = data.Receipts.FirstOrDefault(candidate => candidate.AttemptId == predecessor.LastAttemptId &&
            candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            candidate.Snapshot.QueueItemId == predecessor.Id &&
            candidate.State == ProjectTaskRunState.Completed && candidate.Outcome == ProjectTaskOutcome.Succeeded &&
            candidate.FinishedAt is not null && candidate.ThreadId is not null && candidate.TurnId is not null);
        return receipt is null ? "" : Limit(receipt.ResultSummary, 1000);
    }

    private static ProjectQueueConfiguration FindQueue(ProjectTaskData data, string projectId) =>
        data.Queues.SingleOrDefault(queue => queue.ProjectId == projectId)
        ?? throw new ArgumentException("The selected project has no saved queue.", nameof(projectId));

    private ProjectTaskData LoadWritable()
    {
        var data = _store.Load();
        if (!_store.CanSave) throw new InvalidOperationException(_store.LoadWarning ?? "Task store is unavailable.");
        return data;
    }

    private ProjectQueueCoordinatorStatus Status(ProjectTaskData data, string? projectId)
    {
        var queue = projectId is null ? null : data.Queues.FirstOrDefault(candidate => candidate.ProjectId == projectId);
        var activeMatches = _active is not null && (projectId is null || _active.ProjectId == projectId);
        var dashboardPermitsStarts = QueueDashboardPermit.IsEnabled(_settingsStore.SettingsPath);
        var message = queue?.StatusMessage;
        if (!activeMatches && queue is not null && ProjectQueueDelay.IsWaiting(data, queue, DateTimeOffset.UtcNow))
            message = ProjectQueueDelay.WaitingMessage(data, queue, DateTimeOffset.UtcNow);
        if (!activeMatches && data.PauseAllQueues) message = "All queues paused.";
        else if (!activeMatches && !dashboardPermitsStarts &&
            (queue is { Enabled: true } || projectId is null && data.Queues.Any(candidate => candidate.Enabled)))
            message = DashboardRequiredMessage;
        if (_ownerStatus.Contains("global execution hold", StringComparison.Ordinal) ||
            _ownerStatus.Contains("unresolved queue attempt", StringComparison.Ordinal))
            message = _ownerStatus;
        if (string.IsNullOrWhiteSpace(message))
            message = data.PauseAllQueues ? "All queues paused." :
                queue is { Enabled: false } ? "Queue paused." :
                queue is { Enabled: true } ? "Queue enabled." : _ownerStatus;
        return new(_ready.Task.IsCompletedSuccessfully, data.PauseAllQueues, queue?.Enabled ?? false,
            _active is not null || (_handoffPending || _waitingOnActivePredecessor) &&
                !data.PauseAllQueues && dashboardPermitsStarts,
            activeMatches, _active?.ProjectId, _active?.AttemptId, message);
    }

    private static string NormalizeFolder(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];

    private static string Limit(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    private static FileStream? TryAcquireExecutionLock()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher");
        Directory.CreateDirectory(folder);
        try
        {
            return new FileStream(Path.Combine(folder, "project-queue-execution.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }

    private bool CanClaimGlobalSlot(ProjectTaskData data)
    {
        var marker = ReadGlobalMarker();
        if (marker is null)
        {
            _ownerStatus = "Queue owner is ready.";
            return true;
        }
        if (string.Equals(marker.StorePath, _store.StorePath, StringComparison.OrdinalIgnoreCase) &&
            data.Receipts.FirstOrDefault(receipt => receipt.AttemptId == marker.AttemptId &&
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem) is { } receipt &&
            (IsAutomaticMarkerReleaseAllowed(receipt) || receipt.QueueReviewCompletedAt is not null ||
                receipt.QueueAbandonedAt is not null))
        {
            File.Delete(GlobalMarkerPath);
            _ownerStatus = "Queue owner is ready.";
            return true;
        }
        _ownerStatus = "Another queue attempt has an unresolved global execution hold. Recover or review its exact attempt before dispatch.";
        return false;
    }

    private void TryClearSettledMarker()
    {
        using var executionLock = TryAcquireExecutionLock();
        if (executionLock is null) return;
        var marker = ReadGlobalMarker();
        if (marker is null || !string.Equals(marker.StorePath, _store.StorePath,
                StringComparison.OrdinalIgnoreCase)) return;
        var data = LoadWritable();
        if (data.Receipts.FirstOrDefault(receipt => receipt.AttemptId == marker.AttemptId &&
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem) is { } receipt &&
            (IsAutomaticMarkerReleaseAllowed(receipt) || receipt.QueueReviewCompletedAt is not null ||
                receipt.QueueAbandonedAt is not null))
            File.Delete(GlobalMarkerPath);
    }

    private void ClearMarkerForSettledReceipt(ProjectTaskExecutionReceipt receipt)
    {
        var marker = ReadGlobalMarker();
        if (marker is not null && marker.AttemptId == receipt.AttemptId &&
            string.Equals(marker.StorePath, _store.StorePath, StringComparison.OrdinalIgnoreCase))
            File.Delete(GlobalMarkerPath);
    }

    private static QueueExecutionMarker? ReadGlobalMarker()
    {
        if (!File.Exists(GlobalMarkerPath)) return null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(GlobalMarkerPath); }
        catch (FileNotFoundException) { return null; }
        if (bytes.Length is < 2 or > 8192)
            throw new InvalidOperationException("The global queue execution marker is invalid. Dispatch is suspended.");
        QueueExecutionMarker? marker;
        try { marker = JsonSerializer.Deserialize<QueueExecutionMarker>(bytes); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The global queue execution marker is invalid. Dispatch is suspended.", ex);
        }
        if (marker is null || !Path.IsPathFullyQualified(marker.StorePath) ||
            !Path.IsPathFullyQualified(marker.Folder) ||
            string.IsNullOrWhiteSpace(marker.AttemptId) || string.IsNullOrWhiteSpace(marker.ProjectId))
            throw new InvalidOperationException("The global queue execution marker is invalid. Dispatch is suspended.");
        return marker;
    }

    private static void WriteGlobalMarker(QueueExecutionMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalMarkerPath)!);
        if (File.Exists(GlobalMarkerPath))
            throw new InvalidOperationException("A previous global queue execution marker still needs review.");
        var temporaryPath = GlobalMarkerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(marker);
                if (bytes.Length > 8192)
                    throw new InvalidOperationException("The queue execution marker exceeds its size limit. No Codex task was submitted.");
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, GlobalMarkerPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private sealed record ActiveAttempt(string ProjectId, string AttemptId,
        ProjectTaskDispatchSnapshot Snapshot, ICodexQueueTaskRunner Runner, FileStream ExecutionLock);

    private sealed record QueueExecutionMarker(string StorePath, string AttemptId, string ProjectId,
        string Folder, DateTimeOffset CreatedAt);
}

public sealed record ProjectQueueCoordinatorStatus(bool Ready, bool PauseAllQueues, bool QueueEnabled,
    bool HasActiveWork, bool CanStopCurrent, string? ActiveProjectId, string? ActiveAttemptId, string Message);

public sealed record ProjectQueueNotification(string AttemptId, string ProjectId, string Message,
    DateTimeOffset FinishedAt);
