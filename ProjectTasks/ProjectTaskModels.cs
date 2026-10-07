namespace FullStackLauncher.ProjectTasks;

/// <summary>Local task content, kept separate from portable launcher and monitor settings.</summary>
public sealed class ProjectTaskData
{
    public int Version { get; set; } = 9;
    // Global pause is independent of each queue's explicit, default-off enablement.
    public bool PauseAllQueues { get; set; }
    public List<ProjectTaskNote> Notes { get; set; } = [];
    public List<ProjectQueueItem> QueueItems { get; set; } = [];
    public List<ProjectQueueConfiguration> Queues { get; set; } = [];
    public List<ProjectTaskExecutionReceipt> Receipts { get; set; } = [];
    // Suggestion receipts survive deletion so a repeated submission never
    // restores a note the user removed or duplicates a previous suggestion.
    public List<AgentFollowUpNoteReceipt> AgentFollowUpReceipts { get; set; } = [];
}

public sealed class ProjectTaskNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<ProjectTaskNoteImage> Images { get; set; } = [];
    public int Order { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AgentFollowUpNoteSource? AgentSource { get; set; }
}

/// <summary>Agent-authored work for human review, without queue execution settings.</summary>
public sealed record AgentFollowUpNoteRequest
{
    public string ProjectId { get; init; } = "";
    public string UpdateId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Context { get; init; } = "";
    public string SourceTaskId { get; init; } = "";
    public string SourcePrompt { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string PageTitle { get; init; } = "";
    // API-only input. Normalize it into Prompt before saving/hashing so older
    // queue owners retain the existing schema and legacy receipt hashes.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Summary { get; init; }
}

/// <summary>Original provenance; edits to the suggested note do not rewrite it.</summary>
public sealed record AgentFollowUpNoteSource
{
    public string UpdateId { get; init; } = "";
    public string Author { get; init; } = "";
    public string Context { get; init; } = "";
    public string SourceTaskId { get; init; } = "";
    public string SourcePrompt { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string PageTitle { get; init; } = "";
}

/// <summary>An immutable submission identity retained independently of its note.</summary>
public sealed record AgentFollowUpNoteReceipt
{
    public string NoteId { get; init; } = "";
    public AgentFollowUpNoteRequest Request { get; init; } = new();
    public string Author { get; init; } = "";
    public string PayloadHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An image owned by one note and saved atomically with its text.</summary>
public sealed class ProjectTaskNoteImage
{
    public const int MaximumCount = 12;
    public const int MaximumBytes = 8_000_000;
    public const int MaximumTotalBytes = 24_000_000;
    public const int MaximumPageUrlCharacters = 2_048;
    public const int MaximumPageHtmlCharacters = 200_000;
    public const int MaximumPageCssCharacters = 200_000;
    public const int MaximumPageCaptureStatusCharacters = 240;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Caption { get; set; } = "";
    public string MimeType { get; set; } = "image/png";
    public string DataBase64 { get; set; } = "";
    // Browser source is captured when the snip is made, regardless of whether
    // the user includes it in a later queue prompt. Older images have no source.
    public string PageUrl { get; set; } = "";
    public string PageHtml { get; set; } = "";
    public string PageCss { get; set; } = "";
    public string PageCaptureStatus { get; set; } = "";
    public bool IncludePageContextInPrompt { get; set; }
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
    // Empty for user-authored queue entries. The predecessor is a durable
    // deduplication key even if an automatic continuation is later removed.
    public string AutomaticLoopChainId { get; set; } = "";
    public string AutomaticLoopPredecessorAttemptId { get; set; } = "";
}

public enum ProjectQueueItemState { Pending, Starting, Running, Completed, Failed, Interrupted, NeedsAttention, Recovering }

public sealed class ProjectQueueConfiguration
{
    public const int MaximumDelayMinutes = 10_080;
    public string ProjectId { get; set; } = "";
    // Assignment is explicit and durable. Selecting or renaming a project must
    // not change this folder or enable a queue.
    public string AssignedFolder { get; set; } = "";
    public bool Enabled { get; set; }
    // Optional user defaults. Empty means the UI must use a currently discovered
    // model/effort choice and the runner must validate it before submission.
    public string DefaultModelId { get; set; } = "";
    public string DefaultReasoningEffort { get; set; } = "";
    // Measured from confirmed completion, never from submission or task start.
    public int DelayBetweenTasksMinutes { get; set; }
    public bool AutomaticLoopEnabled { get; set; }
    public string AutomaticLoopFolder { get; set; } = "";
    public string AutomaticLoopModelId { get; set; } = "gpt-6-luna";
    public string AutomaticLoopReasoningEffort { get; set; } = "high";
    public string AutomaticLoopAppGoal { get; set; } = "";
    public DateTimeOffset? AutomaticLoopEnabledAt { get; set; }
    public string AutomaticLoopChainId { get; set; } = "";
    public string AutomaticLoopSeedAttemptId { get; set; } = "";
    public string AutomaticLoopLastGeneratedAttemptId { get; set; } = "";
    public ProjectTaskIdentity? ExternalPredecessor { get; set; }
    // Exact read-only observed terminal completion, not a semantic success claim.
    public DateTimeOffset? ExternalPredecessorSatisfiedAt { get; set; }
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
    public List<ProjectTaskNoteImage> Images { get; init; } = [];
    // Unique per attempt. Only saved image attempts use an image staging folder.
    public string ImageStagingId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string ReasoningEffort { get; init; } = "";
    public string Folder { get; init; } = "";
    // Frozen summary from the exact prior successful queue attempt, if any.
    // This is agent-reported context, not independent proof of correctness.
    public string PredecessorHandoff { get; init; } = "";
    // A frozen chain boundary prevents an old active task from seeding a newly
    // enabled loop. Continuations retain app intent without resuming a thread.
    public string AutomaticLoopChainId { get; init; } = "";
    public bool AutomaticLoopSeedEligible { get; init; }
    public string AutomaticLoopSeedAttemptId { get; init; } = "";
    public string AutomaticLoopPredecessorAttemptId { get; init; } = "";
    public string AutomaticLoopAppGoal { get; init; } = "";
    public string AutomaticLoopSeedPrompt { get; init; } = "";
    public string AutomaticLoopPreviousResult { get; init; } = "";
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
    // Presentation only. Queue recovery and exact-attempt evidence still use this receipt.
    public DateTimeOffset? ActivityArchivedAt { get; set; }
    public DateTimeOffset? ActivityDeletedAt { get; set; }
    // Explicit human confirmation only releases the global uncertain-run hold.
    // It does not change the recorded outcome or permit this item to resend.
    public DateTimeOffset? QueueReviewCompletedAt { get; set; }
    // Explicit Delete abandons queue tracking without claiming that Codex work
    // stopped or succeeded. The attempt and its recorded outcome remain saved.
    public DateTimeOffset? QueueAbandonedAt { get; set; }
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
