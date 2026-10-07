using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// Versioned task storage with atomic replacement, a last-version backup, a
/// cross-process writer lock, and optimistic conflict detection. No Codex state
/// or launcher settings are written by this store.
/// </summary>
public sealed class ProjectTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly object _gate = new();
    private bool _loaded;
    private bool _blocked;
    private int _loadedSourceVersion;
    private string? _loadedFingerprint;
    private ProjectTaskData? _lastGoodData;

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "project-tasks.json");
    public string StorePath { get; }
    public string? LoadWarning { get; private set; }
    public bool CanSave => _loaded && !_blocked;
    public int LoadedSourceVersion { get { lock (_gate) return _loadedSourceVersion; } }

    public ProjectTaskStore() : this(null) { }

    /// <param name="explicitSettingsPath">
    /// An isolated launcher settings filename, or null to use the command-line
    /// override/default. The complete filename is retained in the task filename
    /// so two settings files in the same directory never share tasks.
    /// </param>
    public ProjectTaskStore(string? explicitSettingsPath)
    {
        explicitSettingsPath ??= ReadSettingsOverride();
        if (explicitSettingsPath != null)
        {
            if (string.IsNullOrWhiteSpace(explicitSettingsPath))
                throw new ArgumentException("A settings file path is required.", nameof(explicitSettingsPath));
            StorePath = Path.GetFullPath(explicitSettingsPath) + ".project-tasks.json";
        }
        else StorePath = DefaultStorePath;
    }

    private ProjectTaskStore(string storePath, bool independentBaseline)
    {
        StorePath = storePath;
    }

    // An MCP submission must not replace the baseline used by open note drafts.
    internal ProjectTaskStore CreateIndependentInstance() => new(StorePath, true);

    public ProjectTaskData Load()
    {
        lock (_gate)
        {
            try
            {
                var content = ReadExistingFile();
                var data = content == null ? new ProjectTaskData() : ParseAndValidate(content);
                _loadedSourceVersion = content == null ? data.Version : ReadSourceVersion(content);
                _loadedFingerprint = content == null ? null : Fingerprint(content);
                _lastGoodData = Clone(data);
                _loaded = true;
                _blocked = false;
                LoadWarning = null;
                return data;
            }
            catch (Exception ex) when (IsStoreException(ex))
            {
                _blocked = true;
                LoadWarning = "Project tasks could not be loaded. The existing file has been preserved and saving is disabled. " +
                    "Restore a valid supported file or choose a different --settings file, then reload. " +
                    $"Task store: {StorePath}";
                // A refresh failure must not replace an already displayed,
                // successfully loaded collection with an empty collection.
                return _lastGoodData == null ? new ProjectTaskData() : Clone(_lastGoodData);
            }
        }
    }

    /// <summary>Detect another writer without changing the loaded save baseline.</summary>
    public bool HasChangedSinceLoad()
    {
        lock (_gate)
        {
            if (!_loaded) return true;
            if (_blocked) return false; // A blocked store is retried by its caller.
            try
            {
                var current = ReadExistingFile();
                return !string.Equals(current == null ? null : Fingerprint(current),
                    _loadedFingerprint, StringComparison.Ordinal);
            }
            catch (Exception ex) when (IsStoreException(ex))
            {
                return true; // Load will surface the read failure and block writes.
            }
        }
    }

    /// <summary>
    /// Cleans up excess AI suggestions from a freshly loaded model using the
    /// normal atomic save and conflict checks. A failed save leaves it unchanged.
    /// </summary>
    public ProjectAiNoteRemovalResult EnforceAiNoteLimit(ProjectTaskData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var candidate = Clone(data);
        var result = ProjectAiNoteRetention.TrimToLimit(candidate);
        if (result.RemovedCount == 0) return result;
        Save(candidate);
        data.Notes = candidate.Notes;
        data.QueueItems = candidate.QueueItems;
        return result;
    }

    public void Save(ProjectTaskData data)
    {
        lock (_gate)
        {
            if (!CanSave)
                throw new InvalidOperationException(LoadWarning ?? "Load project tasks before saving them.");
            // Hold the owner slot through the file replacement. A one-time check
            // would race an older owner starting immediately before the schema upgrade.
            using var migrationClaim = _loadedSourceVersion < 9 &&
                !Environment.GetCommandLineArgs().Contains("--queue-owner", StringComparer.OrdinalIgnoreCase)
                ? QueueOwnerClient.TryClaimOwnerMutex(StorePath)
                    ?? throw new InvalidOperationException("An older queue owner is still running. Wait for its idle handoff or exit its tray icon before saving project tasks.")
                : null;
            ArgumentNullException.ThrowIfNull(data);
            // Freeze the caller's proposed state before writing; keep a separate
            // baseline so mutations of a returned model cannot alter the checks.
            var candidate = Clone(data);
            Validate(candidate);
            ValidateReceiptPreservation(candidate, _lastGoodData!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(candidate, JsonOptions) + Environment.NewLine);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

            using var writerLock = AcquireWriterLock();
            var current = ReadExistingFile();
            if (current != null)
            {
                try { ParseAndValidate(current); }
                catch (Exception ex) when (IsStoreException(ex))
                {
                    _blocked = true;
                    LoadWarning = "The project task file is now invalid or has an unsupported version. " +
                        "It has been preserved and saving is disabled. Restore a valid file and reload.";
                    throw new InvalidOperationException(LoadWarning, ex);
                }
            }
            var fingerprint = current == null ? null : Fingerprint(current);
            if (!string.Equals(fingerprint, _loadedFingerprint, StringComparison.Ordinal))
                throw new ProjectTaskStoreConflictException("Project tasks changed in another launcher or editor. Reload project tasks before saving again.");

            var temporaryPath = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                if (current != null) File.Replace(temporaryPath, StorePath, StorePath + ".bak");
                else File.Move(temporaryPath, StorePath, overwrite: false);
                _loadedFingerprint = Fingerprint(bytes);
                _loadedSourceVersion = candidate.Version;
                _lastGoodData = candidate;
                LoadWarning = null;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static ProjectTaskData Clone(ProjectTaskData data) =>
        JsonSerializer.Deserialize<ProjectTaskData>(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), JsonOptions)
        ?? throw new ArgumentException("Project task data is empty.", nameof(data));

    private FileStream AcquireWriterLock()
    {
        try
        {
            // The lock file may remain after a crash; the open handle, not its
            // presence, owns the lock and is released when the process exits.
            return new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ProjectTaskStoreConflictException("Another launcher is saving project tasks. Wait briefly, then reload and retry.", ex);
        }
    }

    private byte[]? ReadExistingFile()
    {
        try { return File.ReadAllBytes(StorePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static int ReadSourceVersion(byte[] content)
    {
        ReadOnlyMemory<byte> json = content;
        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
            json = json[3..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return document.RootElement.GetProperty("version").GetInt32();
    }

    private static string? ReadSettingsOverride()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("--settings requires a settings JSON filename.");
                return args[i + 1];
            }
            if (args[i].StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--settings=".Length..];
        }
        return null;
    }

    private static ProjectTaskData ParseAndValidate(byte[] content)
    {
        // Windows editors may save UTF-8 with a BOM. Keep the original bytes for
        // conflict detection, while accepting that encoding for JSON parsing.
        ReadOnlyMemory<byte> json = content;
        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
            json = json[3..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = document.RootElement;
        RequireUniqueProperties(root);
        var version = RequireProperty(root, "version", JsonValueKind.Number);
        if (!version.TryGetInt32(out var sourceVersion) || sourceVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9))
            throw new ArgumentException("Unsupported project task store version.");
        if (sourceVersion >= 4) RequireBoolean(root, "pauseAllQueues");
        var notes = RequireProperty(root, "notes", JsonValueKind.Array);
        var items = RequireProperty(root, "queueItems", JsonValueKind.Array);
        var queues = RequireProperty(root, "queues", JsonValueKind.Array);
        var receipts = RequireProperty(root, "receipts", JsonValueKind.Array);
        if (sourceVersion >= 7)
        {
            var suggestions = RequireProperty(root, "agentFollowUpReceipts", JsonValueKind.Array);
            foreach (var suggestion in suggestions.EnumerateArray())
            {
                RequireProperties(suggestion, JsonValueKind.String, "noteId", "author", "payloadHash", "createdAt");
                RequireProperties(RequireProperty(suggestion, "request", JsonValueKind.Object), JsonValueKind.String,
                    "projectId", "updateId", "name", "prompt", "context", "sourceTaskId", "sourcePrompt", "pageUrl", "pageTitle");
            }
        }
        foreach (var note in notes.EnumerateArray())
        {
            RequireProperties(note, JsonValueKind.String, "id", "projectId", "name", "prompt", "createdAt", "updatedAt");
            RequireProperty(note, "order", JsonValueKind.Number);
            RequireBoolean(note, "isCompleted");
            RequireBoolean(note, "isArchived");
            if (sourceVersion >= 7)
            {
                RequireNullableProperty(note, "agentSource", JsonValueKind.Object);
                var source = note.EnumerateObject().First(property =>
                    property.Name.Equals("agentSource", StringComparison.OrdinalIgnoreCase)).Value;
                if (source.ValueKind == JsonValueKind.Object)
                    RequireProperties(source, JsonValueKind.String,
                        "updateId", "author", "context", "sourceTaskId", "sourcePrompt", "pageUrl", "pageTitle");
            }
            if (sourceVersion >= 3)
            {
                var images = RequireProperty(note, "images", JsonValueKind.Array);
                foreach (var image in images.EnumerateArray())
                {
                    RequireProperties(image, JsonValueKind.String, "id", "caption", "mimeType", "dataBase64");
                    if (sourceVersion >= 6) RequirePageFields(image);
                }
            }
        }
        foreach (var item in items.EnumerateArray())
        {
            RequireProperties(item, JsonValueKind.String, "id", "projectId", "noteId", "modelId", "reasoningEffort", "state");
            RequireProperty(item, "order", JsonValueKind.Number);
            RequireBoolean(item, "enabled");
            if (sourceVersion >= 9)
                RequireProperties(item, JsonValueKind.String, "automaticLoopChainId", "automaticLoopPredecessorAttemptId");
        }
        foreach (var queue in queues.EnumerateArray())
        {
            RequireProperties(queue, JsonValueKind.String, "projectId", "assignedFolder", "recoveryState", "statusMessage");
            RequireBoolean(queue, "enabled");
            if (sourceVersion >= 4)
                RequireProperties(queue, JsonValueKind.String, "defaultModelId", "defaultReasoningEffort");
            if (sourceVersion >= 8)
                RequireProperty(queue, "delayBetweenTasksMinutes", JsonValueKind.Number);
            if (sourceVersion >= 9)
            {
                RequireBoolean(queue, "automaticLoopEnabled");
                RequireProperties(queue, JsonValueKind.String, "automaticLoopFolder", "automaticLoopModelId",
                    "automaticLoopReasoningEffort", "automaticLoopAppGoal", "automaticLoopChainId",
                    "automaticLoopSeedAttemptId", "automaticLoopLastGeneratedAttemptId");
                RequireNullableProperty(queue, "automaticLoopEnabledAt", JsonValueKind.String);
            }
        }
        foreach (var receipt in receipts.EnumerateArray())
        {
            if (sourceVersion >= 2)
                RequireProperties(receipt, JsonValueKind.String, "purpose", "connectionDetails", "desktopAssociation");
            RequireProperties(receipt, JsonValueKind.String, "attemptId", "state", "outcome", "createdAt", "updatedAt", "notificationState",
                "resultSummary", "finalResponse", "attentionReason", "completionMessage", "notificationError");
            if (sourceVersion >= 5)
                RequireNullableProperty(receipt, "queueAbandonedAt", JsonValueKind.String);
            var snapshot = RequireProperty(receipt, "snapshot", JsonValueKind.Object);
            RequireProperties(snapshot, JsonValueKind.String, "projectId", "projectName", "noteId", "queueItemId", "name", "prompt", "modelId", "reasoningEffort", "folder");
            if (sourceVersion >= 9)
            {
                RequireBoolean(snapshot, "automaticLoopSeedEligible");
                RequireProperties(snapshot, JsonValueKind.String, "automaticLoopChainId", "automaticLoopSeedAttemptId",
                    "automaticLoopPredecessorAttemptId", "automaticLoopAppGoal", "automaticLoopSeedPrompt",
                    "automaticLoopPreviousResult");
            }
            if (sourceVersion >= 6)
            {
                var images = RequireProperty(snapshot, "images", JsonValueKind.Array);
                foreach (var image in images.EnumerateArray()) RequirePageFields(image);
            }
        }
        var data = JsonSerializer.Deserialize<ProjectTaskData>(json.Span, JsonOptions)
            ?? throw new ArgumentException("Project task data is empty.");
        // Migrate only in memory; a normal atomic save retains the prior file as backup.
        // Legacy queue flags never authorize execution in the new format.
        if (data.Version is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8)
        {
            data.Version = 9;
            if (data.Queues != null)
                foreach (var queue in data.Queues)
                    if (queue != null)
                    {
                        if (sourceVersion <= 3) queue.Enabled = false;
                        queue.AutomaticLoopEnabled = false;
                        queue.AutomaticLoopChainId = "";
                        queue.AutomaticLoopEnabledAt = null;
                        queue.AutomaticLoopSeedAttemptId = "";
                        queue.AutomaticLoopLastGeneratedAttemptId = "";
                    }
        }
        Validate(data);
        return data;
    }

    private static void Validate(ProjectTaskData data)
    {
        if (data.Version != 9) throw new ArgumentException("Unsupported project task store version; expected version 1, 2, 3, 4, 5, 6, 7, 8, or 9 on load, and version 9 on save.");
        if (data.Notes == null || data.QueueItems == null || data.Queues == null || data.Receipts == null || data.AgentFollowUpReceipts == null)
            throw new ArgumentException("Project task collections must be arrays.");
        var notes = new Dictionary<string, ProjectTaskNote>(StringComparer.Ordinal);
        foreach (var note in data.Notes)
        {
            if (note == null) throw new ArgumentException("A note cannot be null.");
            Require(note.Id, "Note ID");
            Require(note.ProjectId, "Project ID");
            Require(note.Name, "Task name");
            RequireText(note.Prompt, "Task prompt");
            ValidateImages(note.Images);
            RequireOrder(note.Order);
            RequireTimestamps(note.CreatedAt, note.UpdatedAt);
            if (!notes.TryAdd(note.Id, note)) throw new ArgumentException("Duplicate note ID.");
        }
        var queueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queue in data.Queues)
        {
            if (queue == null) throw new ArgumentException("A queue cannot be null.");
            Require(queue.ProjectId, "Queue project ID");
            RequireAbsoluteFolder(queue.AssignedFolder);
            RequireText(queue.DefaultModelId, "Default queue model");
            RequireText(queue.DefaultReasoningEffort, "Default queue thinking level");
            if (queue.DelayBetweenTasksMinutes is < 0 or > ProjectQueueConfiguration.MaximumDelayMinutes)
                throw new ArgumentException("Queue delay must be a whole number from 0 to 10080 minutes.");
            RequireText(queue.AutomaticLoopFolder, "Automatic loop folder");
            if (queue.AutomaticLoopFolder.Length != 0) RequireAbsoluteFolder(queue.AutomaticLoopFolder);
            Require(queue.AutomaticLoopModelId, "Automatic loop model");
            Require(queue.AutomaticLoopReasoningEffort, "Automatic loop thinking level");
            RequireText(queue.AutomaticLoopAppGoal, "Automatic loop app goal");
            if (queue.AutomaticLoopAppGoal.Length > ProjectAutomaticLoop.MaximumAppGoalCharacters)
                throw new ArgumentException("Automatic loop app goal exceeds 32,000 characters.");
            RequireText(queue.AutomaticLoopChainId, "Automatic loop chain ID");
            RequireText(queue.AutomaticLoopSeedAttemptId, "Automatic loop seed attempt ID");
            RequireText(queue.AutomaticLoopLastGeneratedAttemptId, "Automatic loop predecessor attempt ID");
            if (queue.AutomaticLoopChainId.Length != 0 &&
                (!Guid.TryParseExact(queue.AutomaticLoopChainId, "N", out _) || queue.AutomaticLoopEnabledAt is null))
                throw new ArgumentException("An automatic loop chain requires a valid ID and enablement time.");
            if (queue.AutomaticLoopEnabled && (queue.AutomaticLoopChainId.Length == 0 ||
                queue.AutomaticLoopFolder.Length == 0 || queue.DelayBetweenTasksMinutes < 1))
                throw new ArgumentException("An enabled automatic loop requires a saved folder, chain, and delay of at least 1 minute.");
            if (queue.AutomaticLoopChainId.Length == 0 && (queue.AutomaticLoopEnabledAt is not null ||
                queue.AutomaticLoopSeedAttemptId.Length != 0 || queue.AutomaticLoopLastGeneratedAttemptId.Length != 0))
                throw new ArgumentException("Automatic loop progress requires a saved chain.");
            RequireText(queue.StatusMessage, "Queue status message");
            RequireEnum(queue.RecoveryState);
            if (!queueIds.Add(queue.ProjectId)) throw new ArgumentException("Duplicate project queue.");
            if (queue.ExternalPredecessor is { } predecessor)
            {
                Require(predecessor.ThreadId, "Predecessor task ID");
                Require(predecessor.TurnId, "Predecessor turn ID");
            }
            else if (queue.ExternalPredecessorSatisfiedAt is not null)
                throw new ArgumentException("An observed predecessor completion requires its exact task and turn IDs.");
        }
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var queuedNotes = new HashSet<string>(StringComparer.Ordinal);
        var activeProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.QueueItems)
        {
            if (item == null) throw new ArgumentException("A queue item cannot be null.");
            Require(item.Id, "Queue item ID");
            Require(item.ProjectId, "Queue project ID");
            Require(item.NoteId, "Queue note ID");
            RequireText(item.ModelId, "Queue model");
            RequireText(item.ReasoningEffort, "Queue thinking level");
            RequireText(item.AutomaticLoopChainId, "Queue automatic loop chain ID");
            RequireText(item.AutomaticLoopPredecessorAttemptId, "Queue automatic loop predecessor attempt ID");
            if (item.AutomaticLoopChainId.Length != 0 &&
                (!Guid.TryParseExact(item.AutomaticLoopChainId, "N", out _) ||
                    item.AutomaticLoopPredecessorAttemptId.Length == 0))
                throw new ArgumentException("An automatic queue continuation requires a valid chain and predecessor.");
            if (item.AutomaticLoopChainId.Length == 0 && item.AutomaticLoopPredecessorAttemptId.Length != 0)
                throw new ArgumentException("A queue loop predecessor requires an automatic continuation.");
            RequireEnum(item.State);
            RequireOrder(item.Order);
            if (!itemIds.Add(item.Id)) throw new ArgumentException("Duplicate queue item ID.");
            if (!queuedNotes.Add(item.NoteId)) throw new ArgumentException("A note can appear in its queue only once.");
            if (!queueIds.Contains(item.ProjectId)) throw new ArgumentException("A queue item has no project queue configuration.");
            if (!notes.TryGetValue(item.NoteId, out var note) || !string.Equals(note.ProjectId, item.ProjectId, StringComparison.Ordinal))
                throw new ArgumentException("A queue item must reference a note in its assigned project.");
            if (item.State is ProjectQueueItemState.Starting or ProjectQueueItemState.Running or ProjectQueueItemState.Recovering)
            {
                if (!activeProjects.Add(item.ProjectId)) throw new ArgumentException("A project queue has more than one active item.");
                Require(item.LastAttemptId, "Active queue attempt ID");
            }
        }
        var receiptIds = new Dictionary<string, ProjectTaskExecutionReceipt>(StringComparer.Ordinal);
        foreach (var receipt in data.Receipts)
        {
            if (receipt == null) throw new ArgumentException("An execution receipt cannot be null.");
            Require(receipt.AttemptId, "Attempt ID");
            if (!receiptIds.TryAdd(receipt.AttemptId, receipt)) throw new ArgumentException("Duplicate execution attempt ID.");
            if (receipt.Snapshot is not { } snapshot) throw new ArgumentException("A dispatch snapshot is required.");
            Require(snapshot.ProjectId, "Dispatched project ID");
            Require(snapshot.ProjectName, "Dispatched project name");
            Require(snapshot.NoteId, "Dispatched note ID");
            Require(snapshot.QueueItemId, "Dispatched queue item ID");
            Require(snapshot.Name, "Dispatched task name");
            Require(snapshot.Prompt, "Dispatched prompt");
            ValidateImages(snapshot.Images);
            RequireText(snapshot.ImageStagingId, "Image staging ID");
            if (snapshot.Images.Count > 0 && !Guid.TryParseExact(snapshot.ImageStagingId, "N", out _))
                throw new ArgumentException("An image dispatch requires a valid staging ID.");
            if (snapshot.Images.Count == 0 && snapshot.ImageStagingId.Length != 0)
                throw new ArgumentException("A text-only dispatch cannot have image staging.");
            Require(snapshot.ModelId, "Dispatched model");
            // Some discovered models may not expose an effort override; the
            // runner validates that combination and records the empty override.
            RequireText(snapshot.ReasoningEffort, "Dispatched thinking level");
            RequireAbsoluteFolder(snapshot.Folder);
            RequireText(snapshot.PredecessorHandoff, "Dispatched predecessor handoff");
            if (snapshot.PredecessorHandoff.Length > 1000)
                throw new ArgumentException("A predecessor handoff is too long.");
            RequireText(snapshot.AutomaticLoopChainId, "Dispatched automatic loop chain ID");
            RequireText(snapshot.AutomaticLoopSeedAttemptId, "Dispatched automatic loop seed attempt ID");
            RequireText(snapshot.AutomaticLoopPredecessorAttemptId, "Dispatched automatic loop predecessor attempt ID");
            RequireText(snapshot.AutomaticLoopAppGoal, "Dispatched automatic loop app goal");
            RequireText(snapshot.AutomaticLoopSeedPrompt, "Dispatched automatic loop seed prompt");
            RequireText(snapshot.AutomaticLoopPreviousResult, "Dispatched automatic loop previous result");
            if (snapshot.AutomaticLoopAppGoal.Length > ProjectAutomaticLoop.MaximumAppGoalCharacters ||
                snapshot.AutomaticLoopSeedPrompt.Length > ProjectAutomaticLoop.MaximumSeedPromptCharacters ||
                snapshot.AutomaticLoopPreviousResult.Length > ProjectAutomaticLoop.MaximumPreviousResultCharacters)
                throw new ArgumentException("Dispatched automatic loop context exceeds its limit.");
            if (snapshot.AutomaticLoopChainId.Length == 0 && (snapshot.AutomaticLoopSeedEligible ||
                snapshot.AutomaticLoopSeedAttemptId.Length != 0 || snapshot.AutomaticLoopPredecessorAttemptId.Length != 0 ||
                snapshot.AutomaticLoopAppGoal.Length != 0 || snapshot.AutomaticLoopSeedPrompt.Length != 0 ||
                snapshot.AutomaticLoopPreviousResult.Length != 0))
                throw new ArgumentException("Dispatched automatic loop context requires a chain.");
            if (snapshot.AutomaticLoopChainId.Length != 0 && !Guid.TryParseExact(snapshot.AutomaticLoopChainId, "N", out _))
                throw new ArgumentException("Dispatched automatic loop chain ID is invalid.");
            if (snapshot.AutomaticLoopSeedAttemptId.Length != 0 &&
                (snapshot.AutomaticLoopSeedEligible || snapshot.AutomaticLoopSeedPrompt.Length == 0 ||
                    snapshot.AutomaticLoopPredecessorAttemptId.Length == 0))
                throw new ArgumentException("Dispatched automatic loop continuation requires a frozen seed and predecessor.");
            RequireEnum(receipt.State);
            RequireEnum(receipt.Purpose);
            RequireEnum(receipt.DesktopAssociation);
            RequireText(receipt.ConnectionDetails, "Connection details");
            RequireEnum(receipt.Outcome);
            RequireEnum(receipt.NotificationState);
            RequireTimestamps(receipt.CreatedAt, receipt.UpdatedAt);
            RequireText(receipt.ResultSummary, "Result summary");
            RequireText(receipt.FinalResponse, "Final response");
            RequireText(receipt.AttentionReason, "Attention reason");
            RequireText(receipt.CompletionMessage, "Completion message");
            RequireText(receipt.NotificationError, "Notification error");
            if (receipt.TurnId != null) { Require(receipt.TurnId, "Run turn ID"); Require(receipt.ThreadId, "Run task ID"); }
            if (receipt.ThreadId != null) Require(receipt.ThreadId, "Run task ID");
            if (receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.State == ProjectTaskRunState.Completed && receipt.Outcome == ProjectTaskOutcome.Succeeded)
            {
                Require(receipt.ThreadId, "Completed queue task ID");
                Require(receipt.TurnId, "Completed queue turn ID");
                if (receipt.FinishedAt == null)
                    throw new ArgumentException("A successful queue attempt requires a confirmed terminal time.");
            }
            if (receipt.NotificationState == ProjectTaskNotificationState.Attempted && receipt.NotificationAttemptedAt == null)
                throw new ArgumentException("A notification attempt must record its time.");
            if (receipt.QueueReviewCompletedAt != null &&
                (receipt.Purpose != ProjectTaskExecutionPurpose.QueueItem ||
                  receipt.State == ProjectTaskRunState.Completed && receipt.Outcome == ProjectTaskOutcome.Succeeded ||
                  receipt.State is not (ProjectTaskRunState.NeedsAttention or ProjectTaskRunState.Recovering or
                      ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed)))
                throw new ArgumentException("Only a non-success queue attempt can have a completed manual review.");
            var confirmedSuccess = receipt.FinishedAt is not null &&
                receipt.State == ProjectTaskRunState.Completed && receipt.Outcome == ProjectTaskOutcome.Succeeded &&
                !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId);
            if (receipt.QueueAbandonedAt is { } abandonedAt &&
                (receipt.Purpose != ProjectTaskExecutionPurpose.QueueItem || confirmedSuccess ||
                 receipt.QueueReviewCompletedAt is not null || abandonedAt < receipt.CreatedAt ||
                 receipt.ActivityDeletedAt is null || receipt.ActivityDeletedAt < abandonedAt ||
                 receipt.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
                     ProjectTaskRunState.Running or ProjectTaskRunState.Recovering))
                throw new ArgumentException("Only a deleted, non-success queue attempt can be abandoned.");
            if (receipt.ActivityArchivedAt is { } archivedAt &&
                (receipt.Purpose != ProjectTaskExecutionPurpose.QueueItem ||
                  archivedAt < receipt.CreatedAt ||
                  !confirmedSuccess && receipt.QueueReviewCompletedAt is null))
                throw new ArgumentException("Only a confirmed successful or reviewed queue attempt can be moved to activity history.");
            if (receipt.ActivityDeletedAt is { } deletedAt &&
                (receipt.Purpose != ProjectTaskExecutionPurpose.QueueItem ||
                  deletedAt < receipt.CreatedAt ||
                  !confirmedSuccess && receipt.QueueAbandonedAt is null &&
                      receipt.QueueReviewCompletedAt is null))
                throw new ArgumentException("Only a confirmed successful, reviewed, or abandoned queue attempt can be deleted from task activity.");
        }
        foreach (var item in data.QueueItems.Where(item => item.LastAttemptId != null))
        {
            if (!receiptIds.TryGetValue(item.LastAttemptId!, out var receipt) ||
                receipt.Purpose != ProjectTaskExecutionPurpose.QueueItem ||
                receipt.Snapshot.QueueItemId != item.Id || receipt.Snapshot.ProjectId != item.ProjectId)
                throw new ArgumentException("A queue item's attempt must reference its own execution receipt.");
        }
        ValidateFollowUpReceipts(data, notes);
        ValidateAutomaticLoops(data, receiptIds);
    }

    private static void ValidateReceiptPreservation(ProjectTaskData candidate, ProjectTaskData previous)
    {
        var suggestionReceipts = candidate.AgentFollowUpReceipts.ToDictionary(
            receipt => (receipt.Request.ProjectId, receipt.Request.UpdateId));
        foreach (var oldReceipt in previous.AgentFollowUpReceipts)
        {
            if (!suggestionReceipts.TryGetValue((oldReceipt.Request.ProjectId, oldReceipt.Request.UpdateId), out var receipt) ||
                receipt != oldReceipt)
                throw new ArgumentException("Agent follow-up submission receipts must be retained unchanged independently of notes and queue entries.");
        }
        var candidateNotes = candidate.Notes.ToDictionary(note => note.Id, StringComparer.Ordinal);
        foreach (var oldNote in previous.Notes.Where(note => note.AgentSource != null))
        {
            if (candidateNotes.TryGetValue(oldNote.Id, out var note) && note.AgentSource != oldNote.AgentSource)
                throw new ArgumentException("A saved agent note's original provenance cannot be changed or cleared.");
        }
        var receipts = candidate.Receipts.ToDictionary(receipt => receipt.AttemptId, StringComparer.Ordinal);
        foreach (var oldReceipt in previous.Receipts)
        {
            if (!receipts.TryGetValue(oldReceipt.AttemptId, out var receipt))
                throw new ArgumentException("Execution receipts must be retained independently of notes and queue entries.");
            if (!SnapshotsEqual(receipt.Snapshot, oldReceipt.Snapshot) ||
                receipt.CreatedAt != oldReceipt.CreatedAt || receipt.Purpose != oldReceipt.Purpose)
                throw new ArgumentException("A saved execution's dispatch snapshot and creation time are immutable.");
            if ((oldReceipt.ThreadId != null && receipt.ThreadId != oldReceipt.ThreadId) ||
                (oldReceipt.TurnId != null && receipt.TurnId != oldReceipt.TurnId))
                throw new ArgumentException("An acknowledged execution identity cannot be changed or cleared.");
            if ((oldReceipt.SubmissionStartedAt != null && receipt.SubmissionStartedAt != oldReceipt.SubmissionStartedAt) ||
                (oldReceipt.StartedAt != null && receipt.StartedAt != oldReceipt.StartedAt) ||
                (oldReceipt.FinishedAt != null && receipt.FinishedAt != oldReceipt.FinishedAt))
                throw new ArgumentException("Recorded execution milestones cannot be changed or cleared.");
            if (oldReceipt.Purpose == ProjectTaskExecutionPurpose.QueueItem && oldReceipt.SubmissionStartedAt != null &&
                !string.IsNullOrEmpty(oldReceipt.ConnectionDetails) && receipt.ConnectionDetails != oldReceipt.ConnectionDetails)
                throw new ArgumentException("A submitted queue attempt's access evidence cannot be changed or cleared.");
            if (oldReceipt.FinishedAt != null &&
                (receipt.State != oldReceipt.State || receipt.Outcome != oldReceipt.Outcome))
                throw new ArgumentException("A recorded terminal execution result cannot be changed.");
            if (!string.IsNullOrEmpty(oldReceipt.CompletionMessage) &&
                receipt.CompletionMessage != oldReceipt.CompletionMessage)
                throw new ArgumentException("A retained completion message cannot be changed or cleared.");
            if (oldReceipt.CompletionHistoryClearedAt != null &&
                receipt.CompletionHistoryClearedAt != oldReceipt.CompletionHistoryClearedAt)
                throw new ArgumentException("A recorded completion-history clear cannot be reset.");
            if (oldReceipt.ActivityArchivedAt != null &&
                receipt.ActivityArchivedAt != oldReceipt.ActivityArchivedAt)
                throw new ArgumentException("A recorded activity-history move cannot be changed or reset.");
            if (oldReceipt.ActivityDeletedAt != null &&
                receipt.ActivityDeletedAt != oldReceipt.ActivityDeletedAt)
                throw new ArgumentException("A recorded activity deletion cannot be changed or reset.");
            if (oldReceipt.QueueReviewCompletedAt != null &&
                receipt.QueueReviewCompletedAt != oldReceipt.QueueReviewCompletedAt)
                throw new ArgumentException("A completed queue review cannot be changed or reset.");
            if (oldReceipt.QueueAbandonedAt != null &&
                (receipt.QueueAbandonedAt != oldReceipt.QueueAbandonedAt ||
                 receipt.State != oldReceipt.State || receipt.Outcome != oldReceipt.Outcome ||
                 receipt.FinishedAt != oldReceipt.FinishedAt))
                throw new ArgumentException("An abandoned queue attempt and its recorded outcome cannot be changed or reset.");
            if (oldReceipt.NotificationState == ProjectTaskNotificationState.Attempted &&
                (receipt.NotificationState != ProjectTaskNotificationState.Attempted ||
                 receipt.NotificationAttemptedAt != oldReceipt.NotificationAttemptedAt))
                throw new ArgumentException("A recorded notification attempt cannot be reset.");
        }
    }

    private static void ValidateFollowUpReceipts(ProjectTaskData data,
        IReadOnlyDictionary<string, ProjectTaskNote> notes)
    {
        var identities = new HashSet<(string ProjectId, string UpdateId)>();
        var noteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in data.AgentFollowUpReceipts)
        {
            if (receipt == null) throw new ArgumentException("An agent follow-up receipt cannot be null.");
            Require(receipt.NoteId, "Agent follow-up note ID");
            if (receipt.CreatedAt == default)
                throw new ArgumentException("An agent follow-up receipt must record its creation time.");
            var normalized = AgentFollowUpNoteService.NormalizeRequest(receipt.Request);
            if (normalized != receipt.Request || AgentFollowUpNoteService.NormalizeAuthor(receipt.Author) != receipt.Author)
                throw new ArgumentException("An agent follow-up receipt contains invalid submission metadata.");
            if (receipt.PayloadHash != AgentFollowUpNoteService.PayloadHash(receipt.Request))
                throw new ArgumentException("An agent follow-up receipt has an invalid payload identity.");
            if (!identities.Add((receipt.Request.ProjectId, receipt.Request.UpdateId)) || !noteIds.Add(receipt.NoteId))
                throw new ArgumentException("Duplicate agent follow-up submission identity.");
            if (notes.TryGetValue(receipt.NoteId, out var note) &&
                (note.ProjectId != receipt.Request.ProjectId ||
                 note.AgentSource != AgentFollowUpNoteService.SourceMetadata(receipt.Request, receipt.Author)))
                throw new ArgumentException("An agent follow-up note must retain its original project and provenance.");
        }
        foreach (var note in data.Notes.Where(note => note.AgentSource != null))
        {
            if (!noteIds.Contains(note.Id))
                throw new ArgumentException("An agent follow-up note must reference its retained submission receipt.");
        }
    }

    private static void ValidateAutomaticLoops(ProjectTaskData data,
        IReadOnlyDictionary<string, ProjectTaskExecutionReceipt> receipts)
    {
        static bool MatchesChain(ProjectTaskExecutionReceipt receipt, string projectId, string chainId) =>
            receipt.Snapshot.ProjectId == projectId && receipt.Snapshot.AutomaticLoopChainId == chainId &&
            ProjectAutomaticLoop.ConfirmedSuccess(receipt);
        foreach (var queue in data.Queues)
        {
            if (queue.AutomaticLoopSeedAttemptId.Length != 0 &&
                (!receipts.TryGetValue(queue.AutomaticLoopSeedAttemptId, out var seed) ||
                 !MatchesChain(seed, queue.ProjectId, queue.AutomaticLoopChainId) ||
                 !seed.Snapshot.AutomaticLoopSeedEligible || seed.CreatedAt < queue.AutomaticLoopEnabledAt))
                throw new ArgumentException("An automatic loop seed must reference its own confirmed successful manual queue attempt.");
            if (queue.AutomaticLoopLastGeneratedAttemptId.Length != 0 &&
                (!receipts.TryGetValue(queue.AutomaticLoopLastGeneratedAttemptId, out var predecessor) ||
                 !MatchesChain(predecessor, queue.ProjectId, queue.AutomaticLoopChainId)))
                throw new ArgumentException("Automatic loop progress must reference a confirmed successful attempt in its chain.");
        }
        var continuations = new HashSet<(string ProjectId, string ChainId, string PredecessorId)>();
        foreach (var item in data.QueueItems.Where(item => item.AutomaticLoopChainId.Length != 0))
        {
            if (!receipts.TryGetValue(item.AutomaticLoopPredecessorAttemptId, out var predecessor) ||
                !MatchesChain(predecessor, item.ProjectId, item.AutomaticLoopChainId))
                throw new ArgumentException("An automatic queue continuation requires its own confirmed successful predecessor.");
            if (!continuations.Add((item.ProjectId, item.AutomaticLoopChainId, item.AutomaticLoopPredecessorAttemptId)))
                throw new ArgumentException("A successful automatic loop predecessor can generate only one continuation.");
            var updateId = "loop-" + item.AutomaticLoopChainId + "-" + item.AutomaticLoopPredecessorAttemptId;
            if (!data.AgentFollowUpReceipts.Any(receipt => receipt.NoteId == item.NoteId &&
                    receipt.Request.ProjectId == item.ProjectId && receipt.Request.UpdateId == updateId))
                throw new ArgumentException("An automatic queue continuation must retain its prompt submission identity.");
        }
        foreach (var receipt in receipts.Values.Where(receipt => receipt.Snapshot.AutomaticLoopSeedAttemptId.Length != 0))
        {
            var snapshot = receipt.Snapshot;
            if (!receipts.TryGetValue(snapshot.AutomaticLoopSeedAttemptId, out var seed) ||
                !MatchesChain(seed, snapshot.ProjectId, snapshot.AutomaticLoopChainId) ||
                !seed.Snapshot.AutomaticLoopSeedEligible || snapshot.AutomaticLoopSeedPrompt != seed.Snapshot.Prompt ||
                !receipts.TryGetValue(snapshot.AutomaticLoopPredecessorAttemptId, out var predecessor) ||
                !MatchesChain(predecessor, snapshot.ProjectId, snapshot.AutomaticLoopChainId))
                throw new ArgumentException("A dispatched automatic loop continuation must retain its exact seed and successful predecessor.");
        }
    }

    internal static bool SnapshotsEqual(ProjectTaskDispatchSnapshot left, ProjectTaskDispatchSnapshot right) =>
        left.ProjectId == right.ProjectId && left.ProjectName == right.ProjectName &&
        left.NoteId == right.NoteId && left.QueueItemId == right.QueueItemId &&
        left.Name == right.Name && left.Prompt == right.Prompt &&
        left.ImageStagingId == right.ImageStagingId &&
        left.ModelId == right.ModelId && left.ReasoningEffort == right.ReasoningEffort &&
        left.Folder == right.Folder && left.PredecessorHandoff == right.PredecessorHandoff &&
        left.AutomaticLoopChainId == right.AutomaticLoopChainId &&
        left.AutomaticLoopSeedEligible == right.AutomaticLoopSeedEligible &&
        left.AutomaticLoopSeedAttemptId == right.AutomaticLoopSeedAttemptId &&
        left.AutomaticLoopPredecessorAttemptId == right.AutomaticLoopPredecessorAttemptId &&
        left.AutomaticLoopAppGoal == right.AutomaticLoopAppGoal &&
        left.AutomaticLoopSeedPrompt == right.AutomaticLoopSeedPrompt &&
        left.AutomaticLoopPreviousResult == right.AutomaticLoopPreviousResult &&
        left.Images.Count == right.Images.Count && left.Images.Zip(right.Images).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Caption == pair.Second.Caption &&
            pair.First.MimeType == pair.Second.MimeType && pair.First.DataBase64 == pair.Second.DataBase64 &&
            pair.First.PageUrl == pair.Second.PageUrl && pair.First.PageHtml == pair.Second.PageHtml &&
            pair.First.PageCss == pair.Second.PageCss && pair.First.PageCaptureStatus == pair.Second.PageCaptureStatus &&
            pair.First.IncludePageContextInPrompt == pair.Second.IncludePageContextInPrompt);

    private static void RequirePageFields(JsonElement image)
    {
        RequireProperties(image, JsonValueKind.String, "pageUrl", "pageHtml", "pageCss", "pageCaptureStatus");
        RequireBoolean(image, "includePageContextInPrompt");
    }

    private static void ValidateImages(List<ProjectTaskNoteImage>? images)
    {
        if (images == null || images.Count > ProjectTaskNoteImage.MaximumCount)
            throw new ArgumentException("A note has too many images or an invalid image list.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var image in images)
        {
            if (image == null) throw new ArgumentException("A note image cannot be null.");
            Require(image.Id, "Image ID");
            RequireText(image.Caption, "Image caption");
            if (image.Caption.Length > 240) throw new ArgumentException("An image caption is too long.");
            RequireText(image.PageUrl, "Browser page URL");
            RequireText(image.PageHtml, "Browser page HTML");
            RequireText(image.PageCss, "Browser page CSS");
            RequireText(image.PageCaptureStatus, "Browser page capture status");
            if (image.PageUrl.Length > ProjectTaskNoteImage.MaximumPageUrlCharacters ||
                image.PageHtml.Length > ProjectTaskNoteImage.MaximumPageHtmlCharacters ||
                image.PageCss.Length > ProjectTaskNoteImage.MaximumPageCssCharacters ||
                image.PageCaptureStatus.Length > ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters)
                throw new ArgumentException("A browser page capture exceeds the note limit.");
            if (image.IncludePageContextInPrompt &&
                string.IsNullOrWhiteSpace(image.PageHtml) && string.IsNullOrWhiteSpace(image.PageCss))
                throw new ArgumentException("Browser page source is unavailable for prompt inclusion.");
            if (!ids.Add(image.Id)) throw new ArgumentException("Duplicate note image ID.");
            if (image.MimeType is not ("image/png" or "image/jpeg"))
                throw new ArgumentException("A note image must be PNG or JPEG.");
            if (image.DataBase64 == null || image.DataBase64.Length == 0 || image.DataBase64.Length > ((ProjectTaskNoteImage.MaximumBytes + 2) / 3) * 4 + 4)
                throw new ArgumentException("A note image is empty or too large.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(image.DataBase64); }
            catch (FormatException ex) { throw new ArgumentException("A note image contains invalid data.", ex); }
            if (bytes.Length > ProjectTaskNoteImage.MaximumBytes || bytes.Length == 0)
                throw new ArgumentException("A note image is empty or too large.");
            if (image.MimeType == "image/png" && !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                image.MimeType == "image/jpeg" && !bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
                throw new ArgumentException("A note image has an invalid format.");
            totalBytes += bytes.Length;
            if (totalBytes > ProjectTaskNoteImage.MaximumTotalBytes)
                throw new ArgumentException("A note's images exceed the total size limit.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("A task entry must be an object.");
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) found = property.Value;
        }
        if (found == null || found.Value.ValueKind != kind)
            throw new ArgumentException($"A required '{name}' field is missing or invalid.");
        return found.Value;
    }

    private static void RequireNullableProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("A task entry must be an object.");
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind is JsonValueKind.Null || property.Value.ValueKind == kind) return;
            break;
        }
        throw new ArgumentException($"A required '{name}' field is missing or invalid.");
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON properties are not supported.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
        }
    }

    private static void RequireProperties(JsonElement element, JsonValueKind kind, params string[] names)
    {
        foreach (var name in names) RequireProperty(element, name, kind);
    }

    private static void RequireBoolean(JsonElement element, string name)
    {
        try { RequireProperty(element, name, JsonValueKind.True); }
        catch (ArgumentException) { RequireProperty(element, name, JsonValueKind.False); }
    }

    private static void Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} is required.");
    }

    private static void RequireText(string? value, string label)
    {
        if (value == null) throw new ArgumentException($"{label} must be text.");
    }

    private static void RequireOrder(int order)
    {
        if (order < 0) throw new ArgumentException("Note and queue order cannot be negative.");
    }

    private static void RequireTimestamps(DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        if (createdAt == default || updatedAt < createdAt)
            throw new ArgumentException("Task timestamps are missing or out of order.");
    }

    private static void RequireAbsoluteFolder(string folder)
    {
        Require(folder, "Assigned project folder");
        if (!Path.IsPathFullyQualified(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException("The assigned project folder must be an absolute path.");
        _ = Path.GetFullPath(folder);
    }

    private static void RequireEnum<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("An unknown task state is not supported.");
    }

    private static bool IsStoreException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}

internal sealed class ProjectTaskStoreConflictException : InvalidOperationException
{
    public ProjectTaskStoreConflictException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
