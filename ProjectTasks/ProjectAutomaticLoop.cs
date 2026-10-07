using System.IO;

namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// Builds one durable continuation from a confirmed queue success. Queue pause,
/// delay, execution ownership, model discovery, and recovery still own dispatch.
/// </summary>
public static class ProjectAutomaticLoop
{
    public const int MaximumAppGoalCharacters = 32_000;
    internal const int MaximumSeedPromptCharacters = 500_000;
    internal const int MaximumPreviousResultCharacters = 24_000;

    public static void Configure(ProjectTaskData data, ProjectQueueConfiguration queue,
        bool enabled, string folder, string modelId, string reasoningEffort,
        DateTimeOffset now, string appGoal = "")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(queue);
        if (!enabled && queue.AutomaticLoopEnabled)
        {
            queue.AutomaticLoopEnabled = false;
            foreach (var item in data.QueueItems.Where(item => item.ProjectId == queue.ProjectId &&
                         item.AutomaticLoopChainId.Length != 0 && item.State == ProjectQueueItemState.Pending))
                item.Enabled = false;
            return; // Turning the loop off must work even when its folder became unavailable.
        }
        folder = folder.Trim();
        modelId = modelId.Trim();
        reasoningEffort = reasoningEffort.Trim();
        appGoal = appGoal.Trim();
        if (appGoal.Length > MaximumAppGoalCharacters)
            throw new ArgumentException("App goal exceeds 32,000 characters.");
        if (folder.Length != 0)
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                throw new ArgumentException("Choose an existing absolute project folder for Automatic Loop Mode.");
            folder = NormalizeFolder(folder);
            ValidateFolder(queue.AssignedFolder, folder);
        }
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(reasoningEffort))
            throw new ArgumentException("Choose a model and thinking level for Automatic Loop Mode.");
        if (enabled && (folder.Length == 0 || queue.DelayBetweenTasksMinutes < 1))
            throw new InvalidOperationException(folder.Length == 0
                ? "Choose a project folder before enabling Automatic Loop Mode."
                : "Save a delay of at least 1 minute before enabling Automatic Loop Mode.");

        var newChain = enabled && (!queue.AutomaticLoopEnabled ||
            !string.Equals(queue.AutomaticLoopFolder, folder, StringComparison.OrdinalIgnoreCase) ||
            queue.AutomaticLoopAppGoal != appGoal);
        if (!enabled || newChain)
        {
            foreach (var item in data.QueueItems.Where(item => item.ProjectId == queue.ProjectId &&
                         item.AutomaticLoopChainId.Length != 0 && item.State == ProjectQueueItemState.Pending))
                item.Enabled = false;
        }
        if (newChain)
        {
            queue.AutomaticLoopChainId = Guid.NewGuid().ToString("N");
            queue.AutomaticLoopEnabledAt = now;
            queue.AutomaticLoopSeedAttemptId = "";
            queue.AutomaticLoopLastGeneratedAttemptId = "";
            // Merely enabling this preference never starts or enables a queue.
            queue.StatusMessage = "Automatic Loop Mode armed. Add your first manual queue item, then enable the queue.";
        }
        queue.AutomaticLoopEnabled = enabled;
        queue.AutomaticLoopFolder = folder;
        queue.AutomaticLoopModelId = modelId;
        queue.AutomaticLoopReasoningEffort = reasoningEffort;
        queue.AutomaticLoopAppGoal = appGoal;
    }

    /// <summary>Reject aliases that could escape the selected project's saved root.</summary>
    public static void ValidateFolder(string projectRoot, string loopFolder)
    {
        var root = NormalizeFolder(projectRoot);
        var folder = NormalizeFolder(loopFolder);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(root, folder, StringComparison.OrdinalIgnoreCase) &&
            !folder.StartsWith(rootPrefix,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic Loop Mode folder must be the selected project root or one of its subfolders.");
        if (!Directory.Exists(folder))
            throw new InvalidOperationException("Automatic Loop Mode folder is unavailable. Restore it or choose another project folder.");
        // Check the complete ancestor path, including the saved root itself.
        // A junction/symlink could otherwise make a lexical child another project.
        for (DirectoryInfo? current = new(folder); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Automatic Loop Mode folder cannot pass through a symbolic link or junction. Choose a direct project folder.");
        }
    }

    internal static bool IsContinuation(ProjectQueueItem item) => item.AutomaticLoopChainId.Length != 0;

    internal static string ValidateDispatch(ProjectQueueConfiguration queue, ProjectQueueItem item)
    {
        if (IsContinuation(item) && (!queue.AutomaticLoopEnabled ||
                item.AutomaticLoopChainId != queue.AutomaticLoopChainId))
            throw new InvalidOperationException("This automatic continuation belongs to a disabled or previous loop. Start a new loop with a manual queue item.");
        if (!queue.AutomaticLoopEnabled) return NormalizeFolder(queue.AssignedFolder);
        if (queue.DelayBetweenTasksMinutes < 1)
            throw new InvalidOperationException("Automatic Loop Mode requires a saved delay of at least 1 minute.");
        if (!Guid.TryParseExact(queue.AutomaticLoopChainId, "N", out _) ||
            queue.AutomaticLoopEnabledAt is null ||
            string.IsNullOrWhiteSpace(queue.AutomaticLoopModelId) ||
            string.IsNullOrWhiteSpace(queue.AutomaticLoopReasoningEffort))
            throw new InvalidOperationException("Automatic Loop Mode settings need review before dispatch.");
        ValidateFolder(queue.AssignedFolder, queue.AutomaticLoopFolder);
        return NormalizeFolder(queue.AutomaticLoopFolder);
    }

    internal static ProjectTaskExecutionReceipt? Seed(ProjectTaskData data, ProjectQueueConfiguration queue)
    {
        if (queue.AutomaticLoopSeedAttemptId.Length == 0) return null;
        return data.Receipts.SingleOrDefault(receipt => receipt.AttemptId == queue.AutomaticLoopSeedAttemptId &&
            receipt.Snapshot.ProjectId == queue.ProjectId &&
            receipt.Snapshot.AutomaticLoopChainId == queue.AutomaticLoopChainId &&
            receipt.Snapshot.AutomaticLoopSeedEligible && ConfirmedSuccess(receipt))
            ?? throw new InvalidOperationException("Automatic Loop Mode seed is unavailable. Disable the mode, then seed a new loop with a manual queue item.");
    }

    internal static ProjectTaskExecutionReceipt Predecessor(ProjectTaskData data, ProjectQueueItem item) =>
        data.Receipts.SingleOrDefault(receipt => receipt.AttemptId == item.AutomaticLoopPredecessorAttemptId &&
            receipt.Snapshot.ProjectId == item.ProjectId &&
            receipt.Snapshot.AutomaticLoopChainId == item.AutomaticLoopChainId && ConfirmedSuccess(receipt))
        ?? throw new InvalidOperationException("Automatic Loop Mode predecessor is unavailable. Review the loop before dispatch.");

    internal static bool TryPrepareContinuation(ProjectTaskData data, ProjectQueueConfiguration queue,
        ProjectTaskExecutionReceipt completed)
    {
        if (!queue.AutomaticLoopEnabled ||
            queue.RecoveryState != ProjectQueueRecoveryState.None || !ConfirmedSuccess(completed) ||
            completed.QueueReviewCompletedAt is not null || completed.QueueAbandonedAt is not null ||
            completed.Snapshot.AutomaticLoopChainId != queue.AutomaticLoopChainId ||
            queue.AutomaticLoopEnabledAt is null || completed.CreatedAt < queue.AutomaticLoopEnabledAt ||
            !string.Equals(completed.Snapshot.Folder, queue.AutomaticLoopFolder, StringComparison.OrdinalIgnoreCase))
            return false;
        if (queue.AutomaticLoopSeedAttemptId.Length == 0)
        {
            if (!completed.Snapshot.AutomaticLoopSeedEligible) return false;
            queue.AutomaticLoopSeedAttemptId = completed.AttemptId;
        }
        if (data.QueueItems.Any(item => item.ProjectId == queue.ProjectId &&
                item.Enabled && item.State == ProjectQueueItemState.Pending))
            return false; // Existing manual work keeps its saved order.
        ValidateDispatch(queue, new ProjectQueueItem());
        _ = Seed(data, queue);
        var updateId = "loop-" + queue.AutomaticLoopChainId + "-" + completed.AttemptId;
        if (queue.AutomaticLoopLastGeneratedAttemptId == completed.AttemptId ||
            data.AgentFollowUpReceipts.Any(receipt => receipt.Request.ProjectId == queue.ProjectId &&
                receipt.Request.UpdateId == updateId))
            return false; // A deleted continuation is never silently recreated.
        var protectedNoteIds = ProjectAiNoteRetention.GetProtectedNoteIds(data);
        if (data.Notes.Count(note => note.ProjectId == queue.ProjectId && note.AgentSource is not null &&
                protectedNoteIds.Contains(note.Id)) >= ProjectAiNoteRetention.MaximumAiNotesPerProject)
            throw new InvalidOperationException("Automatic Loop Mode cannot add another prompt while 20 AI prompts are protected by active or unresolved attempts.");
        var noteOrder = data.Notes.Where(note => note.ProjectId == queue.ProjectId)
            .Select(note => note.Order).DefaultIfEmpty(-1).Max();
        if (noteOrder == int.MaxValue)
            throw new InvalidOperationException("Automatic Loop Mode note order is full. Reorder the project's notes before continuing.");
        const string author = "Automatic Loop Mode";
        const string name = "Automatic loop: improve app";
        var request = AgentFollowUpNoteService.NormalizeRequest(new AgentFollowUpNoteRequest
        {
            ProjectId = queue.ProjectId,
            UpdateId = updateId,
            Name = name,
            Prompt = "Summary: Choose and complete the next useful app improvement.\n\n" +
                "Continue building this project's app. Inspect its current code, README, AGENTS.md, and relevant documentation. " +
                "Choose one useful, bounded improvement consistent with the app goal and original manual seed. " +
                "Implement it, verify the change with appropriate focused checks, and report what changed and any material verification limits. " +
                "Use the frozen app goal, seed task, and previous result supplied with this turn. " +
                "Avoid repeating completed work. Work only in the assigned project folder. " +
                "Do not create unrelated apps, change other projects, or enable additional queues. " +
                "Preserve existing user work and follow the project's instructions. " +
                "If a decision, approval, missing access, exhausted useful scope, or unresolved blocker prevents useful progress, return needs-input or blocked; do not report completed for a no-op.",
            Context = "This continuation follows a confirmed successful queue turn. The agent-reported result is reference context, not independent verification.\n" + completed.ResultSummary,
            SourceTaskId = completed.ThreadId ?? ""
        });
        var now = DateTimeOffset.UtcNow;
        var note = new ProjectTaskNote
        {
            ProjectId = queue.ProjectId, Name = request.Name, Prompt = request.Prompt,
            Order = noteOrder + 1, CreatedAt = now, UpdatedAt = now,
            AgentSource = AgentFollowUpNoteService.SourceMetadata(request, author)
        };
        data.Notes.Add(note);
        data.AgentFollowUpReceipts.Add(new AgentFollowUpNoteReceipt
        {
            NoteId = note.Id, Request = request, Author = author,
            PayloadHash = AgentFollowUpNoteService.PayloadHash(request), CreatedAt = now
        });
        var existingItems = data.QueueItems.Where(item => item.ProjectId == queue.ProjectId)
            .OrderBy(item => item.Order).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < existingItems.Length; index++)
            existingItems[index].Order = index + 1;
        data.QueueItems.Add(new ProjectQueueItem
        {
            ProjectId = queue.ProjectId, NoteId = note.Id, Order = 0,
            ModelId = queue.AutomaticLoopModelId, ReasoningEffort = queue.AutomaticLoopReasoningEffort,
            AutomaticLoopChainId = queue.AutomaticLoopChainId,
            AutomaticLoopPredecessorAttemptId = completed.AttemptId
        });
        queue.AutomaticLoopLastGeneratedAttemptId = completed.AttemptId;
        ProjectAiNoteRetention.TrimToLimit(data, queue.ProjectId);
        return true;
    }

    internal static bool ConfirmedSuccess(ProjectTaskExecutionReceipt receipt) =>
        receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem && receipt.FinishedAt is not null &&
        receipt.State == ProjectTaskRunState.Completed && receipt.Outcome == ProjectTaskOutcome.Succeeded &&
        !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId);

    private static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new InvalidOperationException("Automatic Loop Mode requires an absolute project folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }
}
