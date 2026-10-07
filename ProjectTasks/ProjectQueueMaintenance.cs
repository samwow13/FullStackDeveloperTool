namespace FullStackLauncher.ProjectTasks;

/// <summary>Moves saved task content while retaining its execution evidence.</summary>
internal static class ProjectQueueMaintenance
{
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
