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
    private string? _loadedFingerprint;
    private ProjectTaskData? _lastGoodData;

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "project-tasks.json");
    public string StorePath { get; }
    public string? LoadWarning { get; private set; }
    public bool CanSave => _loaded && !_blocked;

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

    public ProjectTaskData Load()
    {
        lock (_gate)
        {
            try
            {
                var content = ReadExistingFile();
                var data = content == null ? new ProjectTaskData() : ParseAndValidate(content);
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

    public void Save(ProjectTaskData data)
    {
        lock (_gate)
        {
            if (!CanSave)
                throw new InvalidOperationException(LoadWarning ?? "Load project tasks before saving them.");
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
                throw new InvalidOperationException("Project tasks changed in another launcher or editor. Reload project tasks before saving again.");

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
            throw new InvalidOperationException("Another launcher is saving project tasks. Wait briefly, then reload and retry.", ex);
        }
    }

    private byte[]? ReadExistingFile()
    {
        try { return File.ReadAllBytes(StorePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

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
        if (!version.TryGetInt32(out var sourceVersion) || sourceVersion is not (1 or 2))
            throw new ArgumentException("Unsupported project task store version.");
        var notes = RequireProperty(root, "notes", JsonValueKind.Array);
        var items = RequireProperty(root, "queueItems", JsonValueKind.Array);
        var queues = RequireProperty(root, "queues", JsonValueKind.Array);
        var receipts = RequireProperty(root, "receipts", JsonValueKind.Array);
        foreach (var note in notes.EnumerateArray())
        {
            RequireProperties(note, JsonValueKind.String, "id", "projectId", "name", "prompt", "createdAt", "updatedAt");
            RequireProperty(note, "order", JsonValueKind.Number);
            RequireBoolean(note, "isCompleted");
            RequireBoolean(note, "isArchived");
        }
        foreach (var item in items.EnumerateArray())
        {
            RequireProperties(item, JsonValueKind.String, "id", "projectId", "noteId", "modelId", "reasoningEffort", "state");
            RequireProperty(item, "order", JsonValueKind.Number);
            RequireBoolean(item, "enabled");
        }
        foreach (var queue in queues.EnumerateArray())
        {
            RequireProperties(queue, JsonValueKind.String, "projectId", "assignedFolder", "recoveryState", "statusMessage");
            RequireBoolean(queue, "enabled");
        }
        foreach (var receipt in receipts.EnumerateArray())
        {
            if (sourceVersion == 2)
                RequireProperties(receipt, JsonValueKind.String, "purpose", "connectionDetails", "desktopAssociation");
            RequireProperties(receipt, JsonValueKind.String, "attemptId", "state", "outcome", "createdAt", "updatedAt", "notificationState",
                "resultSummary", "finalResponse", "attentionReason", "completionMessage", "notificationError");
            var snapshot = RequireProperty(receipt, "snapshot", JsonValueKind.Object);
            RequireProperties(snapshot, JsonValueKind.String, "projectId", "projectName", "noteId", "queueItemId", "name", "prompt", "modelId", "reasoningEffort", "folder");
        }
        var data = JsonSerializer.Deserialize<ProjectTaskData>(json.Span, JsonOptions)
            ?? throw new ArgumentException("Project task data is empty.");
        // Version 2 adds an immutable receipt purpose and connection evidence.
        // Migrate in memory; the normal atomic save retains the original as backup.
        // Older launchers reject version 2 instead of silently dropping these fields.
        if (data.Version == 1) data.Version = 2;
        Validate(data);
        return data;
    }

    private static void Validate(ProjectTaskData data)
    {
        if (data.Version != 2) throw new ArgumentException("Unsupported project task store version; expected version 1 or 2 on load, and version 2 on save.");
        if (data.Notes == null || data.QueueItems == null || data.Queues == null || data.Receipts == null)
            throw new ArgumentException("Project task collections must be arrays.");
        var notes = new Dictionary<string, ProjectTaskNote>(StringComparer.Ordinal);
        foreach (var note in data.Notes)
        {
            if (note == null) throw new ArgumentException("A note cannot be null.");
            Require(note.Id, "Note ID");
            Require(note.ProjectId, "Project ID");
            Require(note.Name, "Task name");
            RequireText(note.Prompt, "Task prompt");
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
            RequireText(queue.StatusMessage, "Queue status message");
            RequireEnum(queue.RecoveryState);
            if (!queueIds.Add(queue.ProjectId)) throw new ArgumentException("Duplicate project queue.");
            if (queue.ExternalPredecessor is { } predecessor)
            {
                Require(predecessor.ThreadId, "Predecessor task ID");
                Require(predecessor.TurnId, "Predecessor turn ID");
            }
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
            Require(snapshot.ModelId, "Dispatched model");
            // Some discovered models may not expose an effort override; the
            // runner validates that combination and records the empty override.
            RequireText(snapshot.ReasoningEffort, "Dispatched thinking level");
            RequireAbsoluteFolder(snapshot.Folder);
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
            if (receipt.NotificationState == ProjectTaskNotificationState.Attempted && receipt.NotificationAttemptedAt == null)
                throw new ArgumentException("A notification attempt must record its time.");
        }
        foreach (var item in data.QueueItems.Where(item => item.LastAttemptId != null))
        {
            if (!receiptIds.TryGetValue(item.LastAttemptId!, out var receipt) ||
                receipt.Snapshot.QueueItemId != item.Id || receipt.Snapshot.ProjectId != item.ProjectId)
                throw new ArgumentException("A queue item's attempt must reference its own execution receipt.");
        }
    }

    private static void ValidateReceiptPreservation(ProjectTaskData candidate, ProjectTaskData previous)
    {
        var receipts = candidate.Receipts.ToDictionary(receipt => receipt.AttemptId, StringComparer.Ordinal);
        foreach (var oldReceipt in previous.Receipts)
        {
            if (!receipts.TryGetValue(oldReceipt.AttemptId, out var receipt))
                throw new ArgumentException("Execution receipts must be retained independently of notes and queue entries.");
            if (receipt.Snapshot != oldReceipt.Snapshot || receipt.CreatedAt != oldReceipt.CreatedAt || receipt.Purpose != oldReceipt.Purpose)
                throw new ArgumentException("A saved execution's dispatch snapshot and creation time are immutable.");
            if ((oldReceipt.ThreadId != null && receipt.ThreadId != oldReceipt.ThreadId) ||
                (oldReceipt.TurnId != null && receipt.TurnId != oldReceipt.TurnId))
                throw new ArgumentException("An acknowledged execution identity cannot be changed or cleared.");
            if (oldReceipt.NotificationState == ProjectTaskNotificationState.Attempted &&
                (receipt.NotificationState != ProjectTaskNotificationState.Attempted ||
                 receipt.NotificationAttemptedAt != oldReceipt.NotificationAttemptedAt))
                throw new ArgumentException("A recorded notification attempt cannot be reset.");
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
