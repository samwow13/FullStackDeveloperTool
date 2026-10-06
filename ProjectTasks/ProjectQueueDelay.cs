namespace FullStackLauncher.ProjectTasks;

/// <summary>Durable queue spacing based on retained, confirmed completion evidence.</summary>
internal static class ProjectQueueDelay
{
    internal static DateTimeOffset? NextStartAt(ProjectTaskData data, ProjectQueueConfiguration queue)
    {
        if (queue.DelayBetweenTasksMinutes == 0) return null;
        var completedAt = data.Receipts.Where(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == queue.ProjectId &&
                receipt.State == ProjectTaskRunState.Completed &&
                receipt.Outcome == ProjectTaskOutcome.Succeeded &&
                receipt.FinishedAt != null && receipt.ThreadId != null && receipt.TurnId != null)
            .Select(receipt => receipt.FinishedAt).Max();
        if (queue.ExternalPredecessorSatisfiedAt is { } externalAt &&
            (completedAt == null || externalAt > completedAt)) completedAt = externalAt;
        if (completedAt == null) return null; // The first task has no inter-task delay.
        var delay = TimeSpan.FromMinutes(queue.DelayBetweenTasksMinutes);
        return completedAt.Value > DateTimeOffset.MaxValue.Subtract(delay)
            ? DateTimeOffset.MaxValue : completedAt.Value.Add(delay);
    }

    internal static bool IsWaiting(ProjectTaskData data, ProjectQueueConfiguration queue, DateTimeOffset now) =>
        !data.PauseAllQueues && queue.Enabled && queue.RecoveryState == ProjectQueueRecoveryState.None &&
        (queue.ExternalPredecessor == null || queue.ExternalPredecessorSatisfiedAt != null) &&
        data.QueueItems.Any(item => item.ProjectId == queue.ProjectId && item.Enabled &&
            item.State == ProjectQueueItemState.Pending) && NextStartAt(data, queue) > now;

    internal static string WaitingMessage(ProjectTaskData data, ProjectQueueConfiguration queue, DateTimeOffset now)
    {
        var nextStartAt = NextStartAt(data, queue)!.Value;
        var remaining = nextStartAt - now;
        // Format UTC explicitly: the independent owner need not share the dashboard's locale.
        var timeLeft = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : remaining.TotalMinutes >= 1 ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
            : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
        return $"Waiting between tasks: {timeLeft} remaining. Next task can start at {nextStartAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC.";
    }
}
