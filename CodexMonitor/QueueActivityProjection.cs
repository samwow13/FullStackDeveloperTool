using FullStackLauncher.ProjectTasks;
using System.IO;

namespace FullStackLauncher.CodexMonitor;

/// <summary>
/// Read-only view of launcher-owned runs. Queue content and messages stay in the
/// task store; monitor preferences retain only passive Codex completion IDs.
/// </summary>
internal sealed record QueueActivitySnapshot(
    IReadOnlyList<ChatProgressRow> Rows,
    IReadOnlyDictionary<string, string> ProjectStatuses,
    IReadOnlySet<string> OwnedThreadIds,
    string? Warning)
{
    internal static QueueActivitySnapshot Empty { get; } = new([], new Dictionary<string, string>(),
        new HashSet<string>(StringComparer.Ordinal), null);

    internal string StatusFor(string? projectId)
    {
        if (Warning != null) return Warning;
        if (projectId == null) return "No project selected.";
        return ProjectStatuses.TryGetValue(projectId, out var status) ? status : "No queue configured.";
    }
}

internal static class QueueActivityProjection
{
    internal static QueueActivitySnapshot Read(string? settingsPath = null)
    {
        try
        {
            var store = new ProjectTaskStore(settingsPath);
            var data = store.Load();
            if (!store.CanSave)
                return QueueActivitySnapshot.Empty with { Warning = "Queue activity unavailable. Open Notes & queue to review the task store." };
            return Build(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return QueueActivitySnapshot.Empty with { Warning = "Queue activity unavailable. Open Notes & queue to review the task store." };
        }
    }

    internal static bool ClearFinished(string? rowId, string? settingsPath = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var store = new ProjectTaskStore(settingsPath);
                var data = store.Load();
                if (!store.CanSave) return false;
                var now = DateTimeOffset.UtcNow;
                var changed = false;
                foreach (var receipt in data.Receipts.Where(receipt =>
                             receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                             receipt.FinishedAt.HasValue && receipt.CompletionHistoryClearedAt == null &&
                             (rowId == null || rowId == RowId(receipt))))
                {
                    receipt.CompletionHistoryClearedAt = now;
                    changed = true;
                }
                if (!changed) return true;
                store.Save(data);
                return true;
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                // Owner and editor saves may race this explicit Clear action.
                // Reload the newest state; never overwrite their receipt changes.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    private static QueueActivitySnapshot Build(ProjectTaskData data)
    {
        var rows = new List<ChatProgressRow>();
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownedThreads = data.Receipts.Where(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem && !string.IsNullOrWhiteSpace(receipt.ThreadId))
            .Select(receipt => receipt.ThreadId!).ToHashSet(StringComparer.Ordinal);
        var receipts = data.Receipts.Where(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem).ToArray();

        foreach (var receipt in receipts.Where(receipt => receipt.FinishedAt.HasValue &&
                     receipt.CompletionHistoryClearedAt == null).OrderByDescending(receipt => receipt.FinishedAt))
        {
            var succeeded = receipt.State == ProjectTaskRunState.Completed && receipt.Outcome == ProjectTaskOutcome.Succeeded;
            var label = succeeded ? "Recently completed" : receipt.State == ProjectTaskRunState.Interrupted
                ? "Interrupted" : receipt.State == ProjectTaskRunState.Failed ? "Failed" : "Needs attention";
            rows.Add(new ChatProgressRow(RowId(receipt), receipt.Snapshot.Name,
                succeeded ? AgentRunState.Completed : AgentRunState.Failed,
                receipt.CompletionMessage.Length == 0 ? label : receipt.CompletionMessage,
                succeeded ? "#53D7A0" : "#F07D86", true, receipt.FinishedAt, 1)
            {
                ActivityLabel = label,
                ProjectPath = receipt.Snapshot.Folder,
                ProjectName = receipt.Snapshot.ProjectName,
                ProjectId = receipt.Snapshot.ProjectId,
                EstimateText = receipt.FinishedAt?.ToLocalTime().ToString("g") ?? "",
                EstimateDetail = receipt.ResultSummary
            });
        }

        foreach (var queue in data.Queues)
        {
            var items = data.QueueItems.Where(item => item.ProjectId == queue.ProjectId)
                .OrderBy(item => item.Order).ToArray();
            var active = items.Select(item => (Item: item, Receipt: receipts.FirstOrDefault(receipt =>
                    receipt.AttemptId == item.LastAttemptId && receipt.Snapshot.QueueItemId == item.Id)))
                .FirstOrDefault(pair => pair.Item.State is ProjectQueueItemState.Starting or
                    ProjectQueueItemState.Running or ProjectQueueItemState.Recovering or ProjectQueueItemState.NeedsAttention);
            var pending = items.FirstOrDefault(item => item.Enabled && item.State == ProjectQueueItemState.Pending);
            var activeReceipt = active.Receipt;
            var activeName = activeReceipt?.Snapshot.Name ?? data.Notes.FirstOrDefault(note => note.Id == active.Item?.NoteId)?.Name;
            var projectName = activeReceipt?.Snapshot.ProjectName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(queue.AssignedFolder));
            if (string.IsNullOrWhiteSpace(projectName)) projectName = "Project queue";
            var label = active.Item?.State switch
            {
                ProjectQueueItemState.Starting => "Starting task",
                ProjectQueueItemState.Running => "Running",
                ProjectQueueItemState.Recovering => "Recovering",
                ProjectQueueItemState.NeedsAttention => "Needs attention",
                _ => null
            };
            if (label != null && !string.IsNullOrWhiteSpace(activeName))
            {
                statuses[queue.ProjectId] = $"{label}: {activeName}";
                rows.Add(new ChatProgressRow(activeReceipt == null ? $"queue-item:{active.Item!.Id}" : RowId(activeReceipt),
                    activeName!, label == "Running" ? AgentRunState.Running : label == "Starting task" ? AgentRunState.Waiting : AgentRunState.Unknown,
                    queue.StatusMessage.Length == 0 ? label : queue.StatusMessage,
                    label is "Running" or "Starting task" ? "#F4BE4F" : "#F07D86", false, null, 1)
                {
                    ActivityLabel = label,
                    ProjectPath = queue.AssignedFolder,
                    ProjectName = projectName,
                    ProjectId = queue.ProjectId,
                    EstimateText = "",
                    EstimateDetail = activeReceipt?.AttentionReason ?? ""
                });
                continue;
            }

            var status = queue.RecoveryState != ProjectQueueRecoveryState.None ? "Needs attention: queue recovery"
                : data.PauseAllQueues || !queue.Enabled ? "Queue paused"
                : queue.ExternalPredecessor != null ? "Waiting for: selected Codex task"
                : pending != null ? $"Next: {data.Notes.FirstOrDefault(note => note.Id == pending.NoteId)?.Name ?? "queued task"}"
                : items.Length > 0 ? "Queue complete" : "Queue empty";
            if (queue.StatusMessage.Length > 0 && queue.RecoveryState != ProjectQueueRecoveryState.None)
                status += " · " + queue.StatusMessage;
            statuses[queue.ProjectId] = status;
            if (queue.Enabled && (queue.ExternalPredecessor != null || queue.RecoveryState != ProjectQueueRecoveryState.None))
                rows.Add(new ChatProgressRow($"queue-status:{queue.ProjectId}", projectName,
                    queue.RecoveryState != ProjectQueueRecoveryState.None ? AgentRunState.Unknown : AgentRunState.Waiting,
                    status, queue.RecoveryState != ProjectQueueRecoveryState.None ? "#F07D86" : "#F4BE4F", false, null, 1)
                {
                    ActivityLabel = queue.RecoveryState != ProjectQueueRecoveryState.None ? "Needs attention" : "Waiting for",
                    ProjectPath = queue.AssignedFolder,
                    ProjectName = projectName,
                    ProjectId = queue.ProjectId,
                    EstimateText = ""
                });
        }
        return new(rows, statuses, ownedThreads, null);
    }

    private static string RowId(ProjectTaskExecutionReceipt receipt) => "queue:" + receipt.AttemptId;
}
