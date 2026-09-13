namespace FullStackLauncher.ProjectTasks;

/// <summary>Local task content, kept separate from portable launcher and monitor settings.</summary>
public sealed class ProjectTaskData
{
    public int Version { get; set; } = 2;
    public List<ProjectTaskNote> Notes { get; set; } = [];
    public List<ProjectQueueItem> QueueItems { get; set; } = [];
    public List<ProjectQueueConfiguration> Queues { get; set; } = [];
    public List<ProjectTaskExecutionReceipt> Receipts { get; set; } = [];
}

public sealed class ProjectTaskNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int Order { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectQueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string NoteId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    // Empty choices are valid for planning. A runner must discover and validate
    // the installed model/effort combination immediately before dispatch.
    public string ModelId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public ProjectQueueItemState State { get; set; } = ProjectQueueItemState.Pending;
    public string? LastAttemptId { get; set; }
}

public enum ProjectQueueItemState { Pending, Starting, Running, Completed, Failed, Interrupted, NeedsAttention, Recovering }

public sealed class ProjectQueueConfiguration
{
    public string ProjectId { get; set; } = "";
    // Assignment is explicit and durable. Selecting or renaming a project must
    // not change this folder or enable a queue.
    public string AssignedFolder { get; set; } = "";
    public bool Enabled { get; set; }
    public ProjectTaskIdentity? ExternalPredecessor { get; set; }
    public ProjectQueueRecoveryState RecoveryState { get; set; } = ProjectQueueRecoveryState.None;
    public string StatusMessage { get; set; } = "";
}

public enum ProjectQueueRecoveryState { None, Required, NeedsAttention }

/// <summary>An exact task and turn, never an arbitrary current or recently finished task.</summary>
public sealed record ProjectTaskIdentity
{
    public string ThreadId { get; init; } = "";
    public string TurnId { get; init; } = "";
}

/// <summary>Frozen before submission. Subsequent note edits cannot change dispatched work.</summary>
public sealed record ProjectTaskDispatchSnapshot
{
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string NoteId { get; init; } = "";
    public string QueueItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string ReasoningEffort { get; init; } = "";
    public string Folder { get; init; } = "";
}

/// <summary>
/// An independent attempt record. Deleting notes, queue entries, or displayed
/// completion history never deletes an execution receipt.
/// </summary>
public sealed class ProjectTaskExecutionReceipt
{
    public string AttemptId { get; init; } = Guid.NewGuid().ToString("N");
    // Missing in earlier version 1 stores means an ordinary queue attempt.
    public ProjectTaskExecutionPurpose Purpose { get; init; } = ProjectTaskExecutionPurpose.QueueItem;
    public ProjectTaskDispatchSnapshot Snapshot { get; init; } = new();
    public string? ThreadId { get; set; }
    public string? TurnId { get; set; }
    public ProjectTaskRunState State { get; set; } = ProjectTaskRunState.Prepared;
    public ProjectTaskOutcome Outcome { get; set; } = ProjectTaskOutcome.Unknown;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmissionStartedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string ResultSummary { get; set; } = "";
    public string FinalResponse { get; set; } = "";
    public string AttentionReason { get; set; } = "";
    public string CompletionMessage { get; set; } = "";
    public ProjectTaskNotificationState NotificationState { get; set; } = ProjectTaskNotificationState.NotPending;
    public DateTimeOffset? NotificationAttemptedAt { get; set; }
    public string NotificationError { get; set; } = "";
    public DateTimeOffset? CompletionHistoryClearedAt { get; set; }
    public string ConnectionDetails { get; set; } = "";
    public CodexDesktopAssociation DesktopAssociation { get; set; } = CodexDesktopAssociation.Unverified;
    // A manual review releases a diagnostic hold; it never changes the recorded outcome.
    public DateTimeOffset? ConnectionReviewCompletedAt { get; set; }
}

public enum ProjectTaskExecutionPurpose { QueueItem, ConnectionCheck }
public enum CodexDesktopAssociation { Unverified, ConfirmedByUser, NotVisibleToUser }
public enum ProjectTaskRunState { Prepared, Starting, Running, Completed, Failed, Interrupted, NeedsAttention, Recovering }
public enum ProjectTaskOutcome { Unknown, Succeeded, Failed, Interrupted, Blocked, NeedsInput }
// Attempted records deduplication even when Windows suppresses a popup. It does
// not claim that the user saw the notification; the retained message is separate.
public enum ProjectTaskNotificationState { NotPending, Pending, Attempted }
