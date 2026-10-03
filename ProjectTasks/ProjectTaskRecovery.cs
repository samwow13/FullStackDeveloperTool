namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// Holds unfinished or inconsistent queue attempts after an execution owner's
/// restart. The owner must save the resulting data before it considers another
/// dispatch. Loading the task store alone never changes execution state.
/// </summary>
public static class ProjectTaskRecovery
{
    private const string RecoveryMessage =
        "An earlier queue attempt needs review after restart. No task will be resent automatically.";
    private const string InconsistentMessage =
        "A queue item and its last execution receipt need review after restart. No task will be resent automatically.";

    public static int HoldUnfinishedQueueAttempts(ProjectTaskData data, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (observedAt == default) throw new ArgumentException("A recovery time is required.", nameof(observedAt));

        var heldCount = 0;
        foreach (var receipt in data.Receipts.Where(receipt =>
                     receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                     receipt.FinishedAt is null && receipt.QueueReviewCompletedAt is null &&
                     receipt.QueueAbandonedAt is null &&
                     (receipt.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
                         ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or
                         ProjectTaskRunState.NeedsAttention)))
        {
            heldCount++;
            // Even Prepared without SubmissionStartedAt remains held: an owner
            // must establish whether submission occurred before another try.
            // Keep an existing NeedsAttention state so approval/input evidence
            // remains visible, and never change a reported outcome or identity.
            if (receipt.State != ProjectTaskRunState.NeedsAttention)
                receipt.State = ProjectTaskRunState.Recovering;
            if (string.IsNullOrWhiteSpace(receipt.AttentionReason))
                receipt.AttentionReason = RecoveryMessage;
            receipt.UpdatedAt = Max(receipt.UpdatedAt, observedAt);

            HoldQueue(data, receipt.Snapshot.ProjectId, RecoveryMessage);

            var item = data.QueueItems.FirstOrDefault(item =>
                item.Id == receipt.Snapshot.QueueItemId && item.LastAttemptId == receipt.AttemptId);
            if (item != null && receipt.State == ProjectTaskRunState.Recovering)
                item.State = ProjectQueueItemState.Recovering;
            else if (item != null && receipt.State == ProjectTaskRunState.NeedsAttention)
                item.State = ProjectQueueItemState.NeedsAttention;
        }

        // The terminal receipt and queue item cannot be saved atomically by a
        // future owner in every crash window. A completed receipt with an item
        // still Pending or Running must not make that item eligible for resend.
        // Failed, interrupted, or otherwise non-successful terminal attempts
        // also require review before a queue may continue.
        foreach (var item in data.QueueItems.Where(item => item.LastAttemptId != null))
        {
            var receipt = data.Receipts.FirstOrDefault(receipt =>
                receipt.AttemptId == item.LastAttemptId &&
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == item.ProjectId &&
                receipt.Snapshot.QueueItemId == item.Id);
            if (receipt == null)
            {
                // A validated store cannot reach this case. Keep an invalid
                // in-memory candidate held as well; its next save must fail.
                item.State = ProjectQueueItemState.NeedsAttention;
                HoldQueue(data, item.ProjectId, InconsistentMessage);
                heldCount++;
                continue;
            }

            if (receipt.QueueAbandonedAt is not null)
                continue; // Explicit Delete released tracking; this item remains skipped.

            if (receipt.FinishedAt is null && receipt.QueueReviewCompletedAt is null &&
                (receipt.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
                    ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention))
                continue; // Already held above; count each attempt only once.

            if (receipt.QueueReviewCompletedAt is not null)
                continue; // Explicit review released this hold; the item remains skipped.

            if (receipt.State == ProjectTaskRunState.Completed &&
                receipt.Outcome == ProjectTaskOutcome.Succeeded &&
                item.State == ProjectQueueItemState.Completed)
                continue;

            if (receipt.FinishedAt is not null &&
                (receipt.State != ProjectTaskRunState.Completed || receipt.Outcome != ProjectTaskOutcome.Succeeded) &&
                (item.State is ProjectQueueItemState.Failed or ProjectQueueItemState.Interrupted or
                    ProjectQueueItemState.NeedsAttention))
                continue; // Exact terminal result and matching non-success item are already held.

            // Preserve terminal receipt outcome, timestamps, and exact IDs.
            // Only the queue's recovery state and item's display state change.
            item.State = ProjectQueueItemState.NeedsAttention;
            HoldQueue(data, item.ProjectId, InconsistentMessage);
            heldCount++;
        }

        // A persisted non-pending item without an attempt identity cannot be
        // reconciled to a task/turn. Keep it out of future selection as well.
        foreach (var item in data.QueueItems.Where(item =>
                     item.LastAttemptId == null && item.State != ProjectQueueItemState.Pending))
        {
            item.State = ProjectQueueItemState.NeedsAttention;
            HoldQueue(data, item.ProjectId, InconsistentMessage);
            heldCount++;
        }

        return heldCount;
    }

    private static void HoldQueue(ProjectTaskData data, string projectId, string message)
    {
        var queue = data.Queues.FirstOrDefault(queue => queue.ProjectId == projectId);
        if (queue == null) return;
        queue.Enabled = false;
        queue.RecoveryState = ProjectQueueRecoveryState.Required;
        queue.StatusMessage = message;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;
}
