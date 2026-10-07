namespace FullStackLauncher.ProjectTasks;

public sealed record ProjectAiNoteRemovalResult(int RemovedCount, int ProtectedCount,
    int RemainingCount, IReadOnlyList<string> RemovedNoteIds);

/// <summary>
/// Limits visible agent suggestions without rewriting submission or execution
/// receipts. Active and unresolved attempts retain their notes for review.
/// </summary>
public static class ProjectAiNoteRetention
{
    public const int MaximumAiNotesPerProject = 20;

    public static IReadOnlySet<string> GetProtectedNoteIds(ProjectTaskData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var protectedIds = data.QueueItems.Where(item =>
                item.State is ProjectQueueItemState.Starting or ProjectQueueItemState.Running or
                    ProjectQueueItemState.Recovering ||
                item.State != ProjectQueueItemState.Pending && item.LastAttemptId is null)
            .Select(item => item.NoteId).ToHashSet(StringComparer.Ordinal);
        foreach (var receipt in data.Receipts.Where(receipt =>
                     receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                     receipt.QueueReviewCompletedAt is null && receipt.QueueAbandonedAt is null &&
                     !IsConfirmedSuccess(receipt)))
            protectedIds.Add(receipt.Snapshot.NoteId);
        return protectedIds;
    }

    public static ProjectAiNoteRemovalResult TrimToLimit(ProjectTaskData data, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var protectedIds = GetProtectedNoteIds(data);
        var notes = AiNotes(data, projectId).ToArray();
        var removeIds = notes.GroupBy(note => note.ProjectId, StringComparer.Ordinal)
            .SelectMany(projectNotes => projectNotes
                .Where(note => !protectedIds.Contains(note.Id))
                .OrderBy(note => note.CreatedAt).ThenBy(note => note.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, projectNotes.Count() - MaximumAiNotesPerProject)))
            .Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        return Remove(data, notes, protectedIds, removeIds);
    }

    public static ProjectAiNoteRemovalResult RemoveAll(ProjectTaskData data, string projectId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var protectedIds = GetProtectedNoteIds(data);
        var notes = AiNotes(data, projectId).ToArray();
        var removeIds = notes.Where(note => !protectedIds.Contains(note.Id))
            .Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        return Remove(data, notes, protectedIds, removeIds);
    }

    private static IEnumerable<ProjectTaskNote> AiNotes(ProjectTaskData data, string? projectId) =>
        data.Notes.Where(note => note.AgentSource is not null &&
            (projectId is null || string.Equals(note.ProjectId, projectId, StringComparison.Ordinal)));

    private static ProjectAiNoteRemovalResult Remove(ProjectTaskData data, ProjectTaskNote[] notes,
        IReadOnlySet<string> protectedIds, HashSet<string> removeIds)
    {
        // Referenced inactive queue entries disappear with their notes. Queue
        // configuration, enablement, execution state, and receipts stay intact.
        data.QueueItems.RemoveAll(item => removeIds.Contains(item.NoteId));
        data.Notes.RemoveAll(note => removeIds.Contains(note.Id));
        return new(removeIds.Count, notes.Count(note => protectedIds.Contains(note.Id)),
            notes.Length - removeIds.Count, notes.Where(note => removeIds.Contains(note.Id))
                .Select(note => note.Id).ToArray());
    }

    private static bool IsConfirmedSuccess(ProjectTaskExecutionReceipt receipt) =>
        receipt.FinishedAt is not null && receipt.State == ProjectTaskRunState.Completed &&
        receipt.Outcome == ProjectTaskOutcome.Succeeded &&
        !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId);
}
