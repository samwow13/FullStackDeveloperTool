namespace FullStackLauncher.ProjectTasks;

/// <summary>Moves saved task content while retaining its execution evidence.</summary>
internal static class ProjectQueueMaintenance
{
    /// <summary>
    /// Clear execution intent after the owner has stopped, without deleting notes,
    /// immutable receipts, or queue entries whose task inactivity is still unknown.
    /// </summary>
    internal static int ClearForShutdown(ProjectTaskData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.PauseAllQueues = true;
        foreach (var queue in data.Queues)
        {
            queue.Enabled = false;
            queue.ExternalPredecessor = null;
            queue.ExternalPredecessorSatisfiedAt = null;
            queue.AutomaticLoopEnabled = false;
            queue.AutomaticLoopEnabledAt = null;
            queue.AutomaticLoopChainId = "";
            queue.AutomaticLoopSeedAttemptId = "";
            queue.AutomaticLoopLastGeneratedAttemptId = "";
        }

        var removed = data.QueueItems.RemoveAll(item => item.LastAttemptId is null &&
                item.State is ProjectQueueItemState.Pending or ProjectQueueItemState.Completed or
                    ProjectQueueItemState.Failed or ProjectQueueItemState.Interrupted ||
            item.LastAttemptId is not null && data.Receipts.Any(receipt =>
                receipt.AttemptId == item.LastAttemptId &&
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == item.ProjectId && receipt.Snapshot.QueueItemId == item.Id &&
                (HasConfirmedInactiveTask(receipt) || receipt.QueueAbandonedAt is not null)));
        foreach (var item in data.QueueItems) item.Enabled = false;
        foreach (var queue in data.Queues)
        {
            var unresolved = data.Receipts.Any(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == queue.ProjectId &&
                receipt.QueueReviewCompletedAt is null && receipt.QueueAbandonedAt is null &&
                !(receipt.FinishedAt is not null && receipt.State == ProjectTaskRunState.Completed &&
                    receipt.Outcome == ProjectTaskOutcome.Succeeded &&
                    !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId)));
            queue.RecoveryState = unresolved
                ? ProjectQueueRecoveryState.NeedsAttention : ProjectQueueRecoveryState.None;
            queue.StatusMessage = unresolved
                ? "Queue stopped and cleared. Retained attempt evidence still needs review."
                : "Queue stopped and cleared when the dashboard closed. Notes and task history remain saved.";
        }
        return removed;
    }

    internal static bool HasConfirmedInactiveTask(ProjectTaskExecutionReceipt receipt) =>
        receipt.QueueReviewCompletedAt is not null ||
        receipt.FinishedAt is not null && receipt.Outcome != ProjectTaskOutcome.Unknown &&
            !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId) ||
        receipt.SubmissionStartedAt is null && receipt.StartedAt is null &&
            receipt.ThreadId is null && receipt.TurnId is null &&
            receipt.Outcome is ProjectTaskOutcome.Failed or ProjectTaskOutcome.Interrupted;

    internal static bool CanRemoveFailedItem(ProjectTaskData data, ProjectQueueItem item,
        string? activeAttemptId = null)
    {
        if (item.State is ProjectQueueItemState.Starting or ProjectQueueItemState.Running or
            ProjectQueueItemState.Recovering ||
            item.LastAttemptId is not null && item.LastAttemptId == activeAttemptId)
            return false;
        if (item.LastAttemptId is null) return item.State == ProjectQueueItemState.Failed;
        var receipt = data.Receipts.FirstOrDefault(receipt => receipt.AttemptId == item.LastAttemptId &&
            receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            receipt.Snapshot.ProjectId == item.ProjectId && receipt.Snapshot.QueueItemId == item.Id);
        return receipt is { State: ProjectTaskRunState.Failed } &&
            (receipt.QueueReviewCompletedAt is not null || receipt.QueueAbandonedAt is not null ||
             receipt.FinishedAt is not null && !string.IsNullOrWhiteSpace(receipt.ThreadId) &&
             !string.IsNullOrWhiteSpace(receipt.TurnId));
    }

    internal static int RemoveFailedItems(ProjectTaskData data, string projectId,
        string? activeAttemptId = null)
    {
        var items = data.QueueItems.Where(item => item.ProjectId == projectId &&
            CanRemoveFailedItem(data, item, activeAttemptId)).OrderBy(item => item.Order).ToArray();
        foreach (var item in items.Reverse()) ReturnToNotes(data, item);
        return items.Length;
    }

    internal static void ReturnToNotes(ProjectTaskData data, ProjectQueueItem item)
    {
        SettleFailedAttempt(data, item);
        var note = data.Notes.Single(note => note.Id == item.NoteId && note.ProjectId == item.ProjectId);
        var notes = data.Notes.Where(candidate => candidate.ProjectId == item.ProjectId &&
            candidate.Id != note.Id && !data.QueueItems.Any(queueItem => queueItem.NoteId == candidate.Id))
            .OrderBy(candidate => candidate.Order).ThenBy(candidate => candidate.CreatedAt).ToArray();
        for (var index = 0; index < notes.Length; index++) notes[index].Order = index + 1;
        note.Order = 0;
        note.IsArchived = false;
        data.QueueItems.Remove(item);
    }

    internal static void SettleFailedAttempt(ProjectTaskData data, ProjectQueueItem item)
    {
        if (!CanRemoveFailedItem(data, item) || item.LastAttemptId is null) return;
        var receipt = data.Receipts.Single(receipt => receipt.AttemptId == item.LastAttemptId);
        if (receipt.QueueReviewCompletedAt is not null || receipt.QueueAbandonedAt is not null) return;
        var now = DateTimeOffset.UtcNow;
        if (now < receipt.UpdatedAt) now = receipt.UpdatedAt;
        receipt.QueueAbandonedAt = now;
        receipt.ActivityDeletedAt ??= now;
        receipt.UpdatedAt = now;
        receipt.AttentionReason = "The failed queue item was removed. Its confirmed failure and task identities remain saved; this attempt will not be resent.";
        if (receipt.NotificationState == ProjectTaskNotificationState.Pending)
            receipt.NotificationState = ProjectTaskNotificationState.NotPending;
    }
}
