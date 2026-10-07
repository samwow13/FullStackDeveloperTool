using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace FullStackLauncher.CodexMonitor;

public sealed record CodexMonitorSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<AgentSnapshot>> Projects,
    IReadOnlyDictionary<string, string> ParentWorkspaces);

/// <summary>
/// Reads internal local Codex metadata; this is not a supported public Codex API.
/// Queries lifecycle, explicit question metadata, and reply identities read-only.
/// Prompts, message text, answers, credentials, and tool-result bodies are never returned or exposed.
/// Call from one background worker at a time, and treat any exception as lost status.
/// </summary>
public sealed class LocalCodexReader
{
    private const int MaximumRolloutTail = 4 * 1024 * 1024;
    private readonly string _codexHome;
    private readonly Dictionary<string, RolloutCache> _rolloutCache = new(StringComparer.OrdinalIgnoreCase);

    public LocalCodexReader(string codexHome)
    {
        _codexHome = NormalizePath(codexHome);
    }

    public IReadOnlyList<string> GetProjectPaths()
    {
        try
        {
            return ReadThreads().Where(thread => !thread.Archived && !thread.Guardian)
                .Select(thread => thread.ProjectPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Unavailable();
        }
    }

    public IReadOnlyList<AgentSnapshot> ReadSnapshot(string projectPath) =>
        ReadSnapshots([projectPath])[projectPath];

    public IReadOnlyDictionary<string, IReadOnlyList<AgentSnapshot>> ReadSnapshots(IReadOnlyList<string> projectPaths) =>
        ReadStatus(projectPaths).Projects;

    /// <summary>
    /// Reads every local thread without depending on saved launcher watches or
    /// workspace paths. Retained identities keep archived terminal observations
    /// available to callers that previously saw those agents working.
    /// </summary>
    public IReadOnlyList<AgentSnapshot> ReadAllSnapshot(IReadOnlyCollection<string>? retainedAgentIds = null,
        bool includeGuardians = true) =>
        ReadStatusCore([], readAll: true, retainedAgentIds: retainedAgentIds,
            includeGuardians: includeGuardians).Projects[string.Empty];

    public CodexMonitorSnapshot ReadStatus(IReadOnlyList<string> projectPaths) =>
        ReadStatusCore(projectPaths, readAll: false, null);

    private CodexMonitorSnapshot ReadStatusCore(IReadOnlyList<string> projectPaths,
        bool readAll, IReadOnlyCollection<string>? retainedAgentIds, bool includeGuardians = true)
    {
        try
        {
            var projects = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => new WatchedProject(path, NormalizePath(path), index,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            var snapshots = projects.ToDictionary(project => project.Key, _ => new List<AgentSnapshot>(),
                StringComparer.OrdinalIgnoreCase);
            if (projects.Length == 0 && !readAll)
                return new(new Dictionary<string, IReadOnlyList<AgentSnapshot>>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (readAll)
                snapshots.Add(string.Empty, []);

            if (!IsCodexRunning())
                throw new InvalidOperationException("Codex is not running. Completion is unconfirmed; open Codex to resume monitoring.");

            var threads = ReadThreads();
            var repositoryCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? RepositoryFor(string path)
            {
                if (!repositoryCache.TryGetValue(path, out var repository))
                {
                    repository = FindRepository(path);
                    repositoryCache.Add(path, repository);
                }
                return repository;
            }

            // .git + commondir identify linked worktrees without launching git or
            // reading repository configuration, remotes, or authentication data.
            // Share both the metadata read and repository cache across all watches.
            foreach (var project in projects)
            {
                var repository = RepositoryFor(project.Path);
                if (repository is not null)
                    project.Repositories.Add(repository);
                foreach (var thread in threads)
                {
                    if (thread.Guardian || !IsWithin(thread.ProjectPath, project.Path))
                        continue;
                    repository = RepositoryFor(thread.ProjectPath);
                    if (repository is not null)
                        project.Repositories.Add(repository);
                }
            }

            // Assign a whole spawn family to one watch. Keep archived ancestors
            // for membership, even though they are never emitted as active rows.
            var threadsById = new Dictionary<string, ThreadMetadata>(StringComparer.Ordinal);
            foreach (var thread in threads)
            {
                if (thread.Guardian && !readAll)
                    continue;
                if (!threadsById.TryAdd(thread.Id, thread))
                    throw Unavailable();
            }
            var roots = new Dictionary<string, string>(StringComparer.Ordinal);
            var families = new Dictionary<string, List<ThreadMetadata>>(StringComparer.Ordinal);
            foreach (var thread in threadsById.Values)
            {
                var rootId = FindFamilyRoot(thread, threadsById, roots);
                if (!families.TryGetValue(rootId, out var members))
                    families[rootId] = members = [];
                members.Add(thread);
            }

            var owners = new Dictionary<string, WatchedProject>(StringComparer.Ordinal);
            foreach (var (rootId, members) in families)
            {
                threadsById.TryGetValue(rootId, out var root);
                var matches = new List<ProjectMatch>();
                foreach (var project in projects)
                {
                    // A root's actual checkout is stronger evidence than a child
                    // in another watched folder. For nested watches, use the
                    // longest matching ancestor; linked-worktree ties use watch order.
                    if (root is not null && IsWithin(root.ProjectPath, project.Path))
                        matches.Add(new ProjectMatch(project, 0, project.Path.Length));
                    else if (members.Any(member => IsWithin(member.ProjectPath, project.Path)))
                        matches.Add(new ProjectMatch(project, 1, project.Path.Length));
                    else if (project.Repositories.Count != 0)
                    {
                        var rootRepository = root is null ? null : RepositoryFor(root.ProjectPath);
                        if (rootRepository is not null && project.Repositories.Contains(rootRepository))
                            matches.Add(new ProjectMatch(project, 2, 0));
                        else if (members.Any(member => RepositoryFor(member.ProjectPath) is { } repository &&
                                     project.Repositories.Contains(repository)))
                            matches.Add(new ProjectMatch(project, 3, 0));
                    }
                }

                var owner = matches.OrderBy(match => match.Kind)
                    .ThenByDescending(match => match.Specificity)
                    .ThenBy(match => match.Project.Order)
                    .ThenBy(match => match.Project.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.Project;
                if (owner is null)
                    continue;
                foreach (var member in members)
                    owners.Add(member.Id, owner);
            }

            // A launcher project can be below the workspace used to start Codex.
            // Suggest only roots not already covered by any watch, including
            // linked-worktree/family ownership. Do not broaden a watch silently.
            var parentWorkspaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootFolders = threads.Where(thread => !thread.Archived && !thread.Guardian &&
                    string.IsNullOrWhiteSpace(thread.ParentId) && !owners.ContainsKey(thread.Id))
                .Select(thread => thread.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var project in projects)
            {
                var parent = rootFolders.Where(folder => IsWithin(project.Path, folder) &&
                        !string.Equals(project.Path, folder, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(folder => folder.Length).FirstOrDefault();
                if (parent is not null) parentWorkspaces[project.Key] = parent;
            }

            using var history = new NativeSqlite(Path.Combine(_codexHome, "thread_history_1.sqlite"));
            // Keep latest turns, questions, and their exact replies in one WAL
            // read snapshot. Disposing this read-only connection ends it on failure.
            history.Query("BEGIN");
            var latest = history.Query("""
                SELECT t.thread_id, t.status, t.turn_id, t.completed_at, t.started_at
                FROM thread_turns t
                INNER JOIN (
                    SELECT thread_id, MAX(rollout_ordinal) AS ordinal
                    FROM thread_turns GROUP BY thread_id
                ) latest ON latest.thread_id = t.thread_id AND latest.ordinal = t.rollout_ordinal
                """);
            var states = new Dictionary<string, TurnMetadata>(StringComparer.Ordinal);
            foreach (var row in latest)
            {
                var id = Required(row[0]);
                var state = row[1] switch
                {
                    "inProgress" => AgentRunState.Running,
                    "completed" => AgentRunState.Completed,
                    "failed" or "interrupted" => AgentRunState.Failed,
                    _ => AgentRunState.Unknown
                };

                // Duplicate latest ordinals are unexpected; never infer completion
                // from only one of conflicting records.
                var turn = new TurnMetadata(state, Required(row[2]), ReadTimestamp(row[3]), ReadTimestamp(row[4]));
                if (states.TryGetValue(id, out var prior) && prior != turn)
                    turn = new TurnMetadata(AgentRunState.Unknown, null, null, null);
                states[id] = turn;
            }
            var pendingAsyncQuestions = ReadPendingAsyncQuestions(history);
            history.Query("COMMIT");

            using var queue = new NativeSqlite(Path.Combine(_codexHome, "queue_1.sqlite"));
            var queued = queue.Query("SELECT DISTINCT thread_id FROM queued_items")
                .Select(row => Required(row[0])).ToHashSet(StringComparer.Ordinal);

            if (readAll && (queued.Any(id => !threadsById.ContainsKey(id)) ||
                pendingAsyncQuestions.Any(question => !threadsById.ContainsKey(question.ThreadId)) ||
                states.Any(pair => !threadsById.ContainsKey(pair.Key) &&
                    (pair.Value.State is AgentRunState.Running or AgentRunState.Waiting or AgentRunState.NeedsInput or AgentRunState.Unknown))))
                throw Unavailable();
            var retained = retainedAgentIds?.ToHashSet(StringComparer.Ordinal);
            var activeAncestors = new HashSet<string>(StringComparer.Ordinal);
            if (readAll)
            {
                // Active spawned work still belongs to its user-started chat
                // when Codex has archived a terminal ancestor. Return only those
                // ancestor identities, with their own native lifecycle metadata.
                foreach (var thread in threads)
                {
                    if (!includeGuardians && thread.Guardian) continue;
                    var observedState = states.TryGetValue(thread.Id, out var observedTurn) ? observedTurn.State : AgentRunState.Unknown;
                    var hasQuestion = observedTurn?.TurnId is { } questionTurn &&
                        pendingAsyncQuestions.Contains((thread.Id, questionTurn));
                    if (observedState is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Idle &&
                        !queued.Contains(thread.Id) && !hasQuestion) continue;
                    var parentId = thread.ParentId;
                    var visited = new HashSet<string>(StringComparer.Ordinal) { thread.Id };
                    while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
                    {
                        activeAncestors.Add(parentId);
                        parentId = threadsById.TryGetValue(parentId, out var parent) ? parent.ParentId : null;
                    }
                }
            }

            foreach (var thread in threads)
            {
                if (readAll && !includeGuardians && thread.Guardian)
                    continue;
                if (!readAll && (thread.Archived || thread.Guardian || !owners.ContainsKey(thread.Id)))
                    continue;
                RolloutObservation? lifecycle = null;
                var state = states.TryGetValue(thread.Id, out var projected)
                    ? projected.State
                    : (lifecycle = ReadRolloutLifecycle(thread.RolloutPath, thread.HasUserEvent)).State;
                if (queued.Contains(thread.Id) && state != AgentRunState.Running)
                    state = AgentRunState.Waiting;
                // Async questions permit continued work. Raise the hand only once
                // that turn stops working with an explicit question still unanswered.
                if (projected?.State == AgentRunState.Completed && projected.TurnId is { } completedTurn &&
                    pendingAsyncQuestions.Contains((thread.Id, completedTurn)))
                    state = AgentRunState.NeedsInput;
                if (state == AgentRunState.Running)
                {
                    lifecycle ??= ReadRolloutLifecycle(thread.RolloutPath, thread.HasUserEvent);
                    var questionTurnId = projected?.TurnId ?? (lifecycle.ActivityIdentity is { } identity &&
                        identity.StartsWith("turn:", StringComparison.Ordinal) ? identity[5..] : null);
                    if (questionTurnId is not null && lifecycle.PendingQuestionTurns.Contains(questionTurnId))
                        state = AgentRunState.NeedsInput;
                }

                // Archived work is not automatically idle. Keep every archived
                // nonterminal/unknown row, and terminal rows explicitly retained
                // by a caller; ordinary historical terminal rows add no activity.
                if (readAll && thread.Archived &&
                    (state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Idle) &&
                    retained?.Contains(thread.Id) != true && !activeAncestors.Contains(thread.Id))
                    continue;

                var key = readAll ? string.Empty : owners[thread.Id].Key;
                // Unprojected child turns still have a verified JSONL lifecycle
                // identity. Carry that turn through to the saved-message reader;
                // byte-offset start identities are not turn IDs.
                var latestTurnId = projected?.TurnId ??
                    (lifecycle?.ActivityIdentity is { } lifecycleIdentity &&
                     lifecycleIdentity.StartsWith("turn:", StringComparison.Ordinal) && lifecycleIdentity.Length > 5
                        ? lifecycleIdentity[5..] : null);
                // The thread selection is the current saved Codex choice. Older
                // stores may omit it; the observed turn context is a bounded fallback.
                if ((thread.Model is null || thread.ReasoningEffort is null) &&
                    (state is AgentRunState.Running or AgentRunState.Waiting or AgentRunState.NeedsInput ||
                     retained?.Contains(thread.Id) == true || activeAncestors.Contains(thread.Id)))
                {
                    try { lifecycle ??= ReadRolloutLifecycle(thread.RolloutPath, thread.HasUserEvent); }
                    catch (Exception exception) when (IsReadFailure(exception))
                    { /* Display metadata must not invalidate an otherwise readable status. */ }
                }
                var matchingSelection = lifecycle is not null &&
                    (latestTurnId is null || lifecycle.SelectionTurnId == latestTurnId);
                snapshots[key].Add(new AgentSnapshot(thread.Id, thread.Title, thread.ProjectPath, state,
                    thread.ParentId, latestTurnId, projected?.CompletedAt)
                {
                    ActivityIdentity = latestTurnId is { Length: > 0 } turnId
                        ? "turn:" + turnId : lifecycle?.ActivityIdentity,
                    ActivityStartedAt = projected?.StartedAt ?? lifecycle?.StartedAt,
                    Model = thread.Model ?? (matchingSelection ? lifecycle?.Model : null),
                    ReasoningEffort = thread.ReasoningEffort ?? (matchingSelection ? lifecycle?.ReasoningEffort : null)
                });
            }

            if (!IsCodexRunning())
                throw new InvalidOperationException("Codex stopped while reading status. Completion is unconfirmed.");

            return new(snapshots.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AgentSnapshot>)pair.Value,
                StringComparer.OrdinalIgnoreCase), parentWorkspaces);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Unavailable();
        }
    }

    private static HashSet<(string ThreadId, string TurnId)> ReadPendingAsyncQuestions(NativeSqlite history)
    {
        // The native async tool becomes an agentMessage with questions. Its
        // accepted tool result does not mean the human answered the question.
        var questions = history.Query("""
            WITH latest AS (
                SELECT thread_id, MAX(rollout_ordinal) AS ordinal FROM thread_turns GROUP BY thread_id
            )
            SELECT i.thread_id, i.turn_id, i.item_id, i.rollout_ordinal,
                   json_array_length(i.item_json, '$.questions')
            FROM latest l
            JOIN thread_turns t ON t.thread_id = l.thread_id AND t.rollout_ordinal = l.ordinal
            JOIN thread_items i ON i.thread_id = t.thread_id AND i.turn_id = t.turn_id
            WHERE t.status = 'completed' AND i.item_type = 'agentMessage'
              AND json_extract(i.item_json, '$.delivery') = 'async'
              AND json_array_length(i.item_json, '$.questions') > 0
            """);
        if (questions.Count == 0) return [];

        var pending = new Dictionary<(string ThreadId, string TurnId, string CallId), PendingQuestion>();
        foreach (var row in questions)
        {
            if (!long.TryParse(row[3], out var ordinal) || !int.TryParse(row[4], out var count) || count <= 0 || count > 100)
                throw Unavailable();
            pending.Add((Required(row[0]), Required(row[1]), Required(row[2])), new PendingQuestion(ordinal, count));
        }

        // Project only the encoded questionItemId from the exact native reply
        // envelope. Neither question text nor answer text leaves SQLite.
        var replies = history.Query("""
            WITH latest AS (
                SELECT thread_id, MAX(rollout_ordinal) AS ordinal FROM thread_turns GROUP BY thread_id
            ), reply_parts AS (
                SELECT i.thread_id, i.turn_id, i.rollout_ordinal,
                       json_extract(c.value, '$.text') AS reply
                FROM latest l
                JOIN thread_turns t ON t.thread_id = l.thread_id AND t.rollout_ordinal = l.ordinal
                JOIN thread_items i ON i.thread_id = t.thread_id AND i.turn_id = t.turn_id
                JOIN json_each(i.item_json, '$.content') c
                WHERE t.status = 'completed' AND i.item_type = 'userMessage'
                  AND json_extract(c.value, '$.type') = 'text'
            ), reply_json AS (
                SELECT thread_id, turn_id, rollout_ordinal,
                       substr(reply, length('<send_user_message_question_reply>') + 1,
                           length(reply) - length('<send_user_message_question_reply>') -
                           length('</send_user_message_question_reply>')) AS answer_json
                FROM reply_parts
                WHERE substr(reply, 1, length('<send_user_message_question_reply>')) = '<send_user_message_question_reply>'
                  AND substr(reply, -length('</send_user_message_question_reply>')) = '</send_user_message_question_reply>'
            )
            SELECT q.thread_id, q.turn_id, q.rollout_ordinal, json_extract(r.value, '$.questionItemId')
            FROM reply_json q
            JOIN json_each(CASE WHEN json_valid(q.answer_json) THEN q.answer_json ELSE '[]' END) r
            """);
        foreach (var row in replies)
        {
            if (row[3] is null || !long.TryParse(row[2], out var ordinal)) continue;
            try
            {
                using var encoded = JsonDocument.Parse(row[3]!);
                var identity = encoded.RootElement;
                if (identity.ValueKind != JsonValueKind.Array || identity.GetArrayLength() != 3 ||
                    identity[0].ValueKind != JsonValueKind.String || identity[0].GetString() != "request_user_input_async" ||
                    identity[1].ValueKind != JsonValueKind.String || identity[2].ValueKind != JsonValueKind.Number ||
                    !identity[2].TryGetInt32(out var index)) continue;
                if (pending.TryGetValue((Required(row[0]), Required(row[1]), Required(identity[1].GetString())), out var request) &&
                    ordinal > request.Ordinal && index >= 0 && index < request.Count)
                    request.AnsweredIndexes.Add(index);
            }
            catch (JsonException) { /* Unrecognized reply metadata cannot resolve a question. */ }
        }
        return pending.Where(pair => pair.Value.AnsweredIndexes.Count < pair.Value.Count)
            .Select(pair => (pair.Key.ThreadId, pair.Key.TurnId)).ToHashSet();
    }

    private static string FindFamilyRoot(ThreadMetadata thread,
        IReadOnlyDictionary<string, ThreadMetadata> threads, Dictionary<string, string> roots)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string rootId;
        while (true)
        {
            if (roots.TryGetValue(thread.Id, out rootId!))
                break;
            if (!visited.Add(thread.Id))
                throw Unavailable();
            if (string.IsNullOrWhiteSpace(thread.ParentId))
            {
                rootId = thread.Id;
                break;
            }
            if (!threads.TryGetValue(thread.ParentId, out var parent))
            {
                // Retain the missing parent's identity so known siblings remain
                // together, without inventing a completed ancestor or status.
                rootId = thread.ParentId;
                break;
            }
            thread = parent;
        }
        foreach (var id in visited)
            roots[id] = rootId;
        return rootId;
    }

    private IReadOnlyList<ThreadMetadata> ReadThreads()
    {
        EnsureDatabaseVersion("state", 5);
        EnsureDatabaseVersion("thread_history", 1);
        EnsureDatabaseVersion("queue", 1);
        using var database = new NativeSqlite(Path.Combine(_codexHome, "state_5.sqlite"));
        var columns = database.Query("PRAGMA table_info(threads)")
            .Select(row => row[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelColumn = columns.Contains("model") ? "model" : "NULL";
        var effortColumn = columns.Contains("reasoning_effort") ? "reasoning_effort" : "NULL";
        // Deliberately omit first_user_message, preview, and all content tables.
        var rows = database.Query($"SELECT id, cwd, source, rollout_path, archived, substr(COALESCE(NULLIF(name, ''), title), 1, 160), has_user_event, {modelColumn}, {effortColumn} FROM threads");
        var result = new List<ThreadMetadata>(rows.Count);
        foreach (var row in rows)
        {
            var source = Required(row[2]);
            string? parentId = null;
            var guardian = source is "guardian" or "guardian_review";
            if (source.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(source);
                guardian |= ContainsGuardian(document.RootElement);
                parentId = FindParent(document.RootElement);
            }

            result.Add(new ThreadMetadata(Required(row[0]), NormalizePath(Required(row[1])),
                row[5] ?? "Codex task", parentId, Required(row[3]), row[4] != "0", guardian, row[6] != "0",
                ReadSelectionValue(row[7]), ReadSelectionValue(row[8])));
        }

        return result;
    }

    private RolloutObservation ReadRolloutLifecycle(string rolloutPath, bool hasUserEvent)
    {
        // Older/migrated tasks may have no thread_turns projection. Inspect only
        // lifecycle and model-selection fields in a bounded tail; never
        // materialize prompt, message, reasoning, or tool payload strings.
        var path = NormalizePath(rolloutPath);
        if (!IsWithin(path, _codexHome) || !File.Exists(path))
            return new(AgentRunState.Unknown, null, null);

        var file = new FileInfo(path);
        if (_rolloutCache.TryGetValue(path, out var cached) && cached.Observation.State != AgentRunState.Unknown &&
            cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc &&
            !(cached.Observation.State == AgentRunState.Idle && hasUserEvent))
            return cached.Observation;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - MaximumRolloutTail);
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[(int)(stream.Length - offset)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0)
                break;
            count += read;
        }

        var state = AgentRunState.Unknown;
        string? activityIdentity = null;
        DateTimeOffset? startedAt = null;
        string? model = null;
        string? reasoningEffort = null;
        string? selectionTurnId = null;
        var sawSelection = false;
        var idleCandidate = !hasUserEvent && offset == 0 && count == bytes.Length && count > 0;
        var malformed = false;
        var pendingQuestions = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineStart = 0;
        if (offset > 0)
        {
            var firstNewline = Array.IndexOf(bytes, (byte)'\n', 0, count);
            lineStart = firstNewline < 0 ? count : firstNewline + 1;
        }

        for (var index = lineStart; index < count; index++)
        {
            if (bytes[index] != '\n')
                continue;
            LifecycleRecord record;
            try
            {
                record = ReadLifecycleLine(bytes.AsSpan(lineStart, index - lineStart));
            }
            catch (JsonException)
            {
                malformed = true;
                break;
            }

            idleCandidate &= record.RecordType is "session_meta" or "realtime_item";
            var lifecycleType = record.RecordType == "event_msg" ? record.PayloadType : null;
            if (record.RecordType == "turn_context")
            {
                if (!sawSelection || selectionTurnId != record.TurnId)
                {
                    model = null;
                    reasoningEffort = null;
                }
                sawSelection = true;
                selectionTurnId = record.TurnId;
                model = record.Model ?? model;
                reasoningEffort = record.ReasoningEffort ?? reasoningEffort;
            }
            if (record.RecordType == "turn_context" || lifecycleType is "task_started" or "task_complete" or "turn_aborted")
                pendingQuestions.Clear();
            if (record.RecordType == "response_item" && record.PayloadType == "function_call" &&
                record.ToolName is "request_user_input" or "functions.request_user_input" &&
                record.CallId is { Length: > 0 } callId &&
                (record.ToolTurnId ?? (activityIdentity is { } currentIdentity && currentIdentity.StartsWith("turn:", StringComparison.Ordinal)
                    ? currentIdentity[5..] : null)) is { Length: > 0 } toolTurnId)
                pendingQuestions[callId] = toolTurnId;
            else if (record.RecordType == "response_item" && record.PayloadType == "function_call_output" &&
                     record.CallId is { } completedCall)
                pendingQuestions.Remove(completedCall);
            if (lifecycleType == "task_started")
            {
                activityIdentity = !string.IsNullOrWhiteSpace(record.TurnId)
                    ? "turn:" + record.TurnId
                    : "start:" + (offset + lineStart).ToString(System.Globalization.CultureInfo.InvariantCulture);
                startedAt = record.Timestamp;
            }
            else if (lifecycleType is "task_complete" or "turn_aborted" &&
                     !string.IsNullOrWhiteSpace(record.TurnId))
            {
                var terminalIdentity = "turn:" + record.TurnId;
                if (activityIdentity != terminalIdentity) startedAt = null;
                activityIdentity = terminalIdentity;
            }
            state = lifecycleType switch
            {
                "task_started" => AgentRunState.Running,
                "task_complete" => AgentRunState.Completed,
                "turn_aborted" => AgentRunState.Failed,
                _ => state
            };
            lineStart = index + 1;
        }

        Array.Clear(bytes);
        if (malformed || lineStart != count || count != bytes.Length)
        {
            state = AgentRunState.Unknown;
            activityIdentity = null;
            startedAt = null;
        }
        else if (idleCandidate && DateTime.UtcNow - file.LastWriteTimeUtc >= TimeSpan.FromMinutes(1))
            state = AgentRunState.Idle;

        var lengthBefore = file.Length;
        var modifiedBefore = file.LastWriteTimeUtc;
        file.Refresh();
        if (file.Length != lengthBefore || file.LastWriteTimeUtc != modifiedBefore)
            return new(AgentRunState.Unknown, null, null)
            {
                Model = cached?.Observation.Model,
                ReasoningEffort = cached?.Observation.ReasoningEffort,
                SelectionTurnId = cached?.Observation.SelectionTurnId
            };

        // A bounded tail can lose the start marker. Null is deliberately not new
        // activity evidence; never substitute file length or modification time.
        if (state == AgentRunState.Unknown)
        {
            activityIdentity = null;
            startedAt = null;
        }
        else if (activityIdentity is not null && cached is not null && cached.Observation.ActivityIdentity == activityIdentity)
            startedAt ??= cached.Observation.StartedAt;
        if (malformed || lineStart != count || count != bytes.Length)
        {
            model = null;
            reasoningEffort = null;
            sawSelection = false;
        }
        if (cached is not null && (!sawSelection || selectionTurnId == cached.Observation.SelectionTurnId))
        {
            model ??= cached.Observation.Model;
            reasoningEffort ??= cached.Observation.ReasoningEffort;
            if (!sawSelection) selectionTurnId = cached.Observation.SelectionTurnId;
        }
        var observation = new RolloutObservation(state, activityIdentity, startedAt)
        {
            Model = model,
            ReasoningEffort = reasoningEffort,
            SelectionTurnId = selectionTurnId
        };
        if (!malformed && lineStart == count && count == bytes.Length)
            observation = observation with { PendingQuestionTurns = pendingQuestions.Values.ToHashSet(StringComparer.Ordinal) };

        // Revisit Unknown even without a file change: a brand-new empty voice
        // session must pass the quiet period before it can be classified as idle.
        if (state != AgentRunState.Unknown || observation.PendingQuestionTurns.Count > 0 ||
            observation.Model is not null || observation.ReasoningEffort is not null)
            _rolloutCache[path] = new RolloutCache(file.Length, file.LastWriteTimeUtc, observation);
        return observation;
    }

    private static LifecycleRecord ReadLifecycleLine(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        string? recordType = null;
        string? payloadType = null;
        string? turnId = null;
        string? toolName = null;
        string? callId = null;
        string? toolTurnId = null;
        string? model = null;
        string? reasoningEffort = null;
        DateTimeOffset? timestamp = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                continue;
            var isType = reader.ValueTextEquals("type"u8);
            var isPayload = reader.ValueTextEquals("payload"u8);
            var isTimestamp = reader.ValueTextEquals("timestamp"u8);
            if (!reader.Read())
                throw new JsonException();
            if (isType && reader.TokenType == JsonTokenType.String)
                recordType = reader.GetString();
            else if (isTimestamp && reader.TokenType == JsonTokenType.String)
            {
                if (DateTimeOffset.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                    timestamp = parsed;
            }
            else if (isPayload && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    var isPayloadType = reader.ValueTextEquals("type"u8);
                    var isTurnId = reader.ValueTextEquals("turn_id"u8);
                    var isName = reader.ValueTextEquals("name"u8);
                    var isCallId = reader.ValueTextEquals("call_id"u8);
                    var isMetadata = reader.ValueTextEquals("internal_chat_message_metadata_passthrough"u8);
                    var isModel = reader.ValueTextEquals("model"u8);
                    var isEffort = reader.ValueTextEquals("effort"u8) || reader.ValueTextEquals("reasoning_effort"u8);
                    if (!reader.Read())
                        throw new JsonException();
                    if (isPayloadType && reader.TokenType == JsonTokenType.String)
                        payloadType = reader.GetString();
                    else if (isName && reader.TokenType == JsonTokenType.String && recordType == "response_item")
                        toolName = reader.GetString();
                    else if (isCallId && reader.TokenType == JsonTokenType.String && recordType == "response_item")
                        callId = reader.GetString();
                    else if (isMetadata && reader.TokenType == JsonTokenType.StartObject && recordType == "response_item")
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            var isMetadataTurn = reader.ValueTextEquals("turn_id"u8);
                            if (!reader.Read()) throw new JsonException();
                            if (isMetadataTurn && reader.TokenType == JsonTokenType.String) toolTurnId = reader.GetString();
                            else reader.Skip();
                        }
                    }
                    else if (isModel && reader.TokenType == JsonTokenType.String && recordType == "turn_context")
                        model = ReadSelectionValue(reader.GetString());
                    else if (isEffort && reader.TokenType == JsonTokenType.String && recordType == "turn_context")
                        reasoningEffort = ReadSelectionValue(reader.GetString());
                    else if (isTurnId && reader.TokenType == JsonTokenType.String &&
                             (recordType == "turn_context" || recordType == "event_msg" &&
                              payloadType is "task_started" or "task_complete" or "turn_aborted"))
                        turnId = reader.GetString();
                    else
                        reader.Skip();
                }
            }
            else
                reader.Skip();
        }

        if (recordType is null || reader.CurrentDepth != 0)
            throw new JsonException();
        var isLifecycle = recordType == "event_msg" &&
            payloadType is "task_started" or "task_complete" or "turn_aborted";
        return new LifecycleRecord(recordType, payloadType,
            isLifecycle || recordType == "turn_context" ? turnId : null,
            isLifecycle ? timestamp : null, toolName, callId, toolTurnId, model, reasoningEffort);
    }

    private static string? FindParent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "parent_thread_id" && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
            var nested = FindParent(property.Value);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static bool ContainsGuardian(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() is "guardian" or "guardian_review";
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        return element.EnumerateObject().Any(property => property.Name is "guardian" or "guardian_review" || ContainsGuardian(property.Value));
    }

    private static bool IsCodexRunning()
    {
        var processes = Process.GetProcessesByName("Codex");
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void EnsureDatabaseVersion(string prefix, int supportedVersion)
    {
        if (!Directory.Exists(_codexHome))
            throw Unavailable();
        foreach (var path in Directory.EnumerateFiles(_codexHome, prefix + "_*.sqlite", SearchOption.TopDirectoryOnly))
        {
            var suffix = Path.GetFileNameWithoutExtension(path)[(prefix.Length + 1)..];
            if (int.TryParse(suffix, out var version) && version > supportedVersion)
                throw new InvalidOperationException("Codex local status storage has changed. Update the launcher monitor before relying on completion reminders.");
        }
    }

    private static string? FindRepository(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath))
                return NormalizePath(gitPath);
            if (!File.Exists(gitPath))
                continue;
            var pointer = ReadSmallPointer(gitPath);
            if (pointer is null || !pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                return null;
            var target = NormalizePath(Path.GetFullPath(pointer[7..].Trim(), directory.FullName));
            var common = ReadSmallPointer(Path.Combine(target, "commondir"));
            return common is null ? target : NormalizePath(Path.GetFullPath(common, target));
        }
        return null;
    }

    private static string? ReadSmallPointer(string path)
    {
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > 8192)
            return null;
        return File.ReadAllText(path).Trim();
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithin(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw Unavailable() : value;

    private static string? ReadSelectionValue(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':')
            ? value : null;
    }

    private static DateTimeOffset? ReadTimestamp(string? value)
    {
        if (value is null)
            return null;
        if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            throw Unavailable();
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static bool IsReadFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or JsonException or ArgumentException or
        DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception;

    private static InvalidOperationException Unavailable() => new(
        "Codex local status data could not be read safely. Open Codex and verify the selected folder; completion is unconfirmed.");

    private sealed record ThreadMetadata(string Id, string ProjectPath, string Title, string? ParentId, string RolloutPath,
        bool Archived, bool Guardian, bool HasUserEvent, string? Model, string? ReasoningEffort);
    private sealed record WatchedProject(string Key, string Path, int Order, HashSet<string> Repositories);
    private sealed record ProjectMatch(WatchedProject Project, int Kind, int Specificity);
    private sealed record TurnMetadata(AgentRunState State, string? TurnId, DateTimeOffset? CompletedAt, DateTimeOffset? StartedAt);
    private sealed record LifecycleRecord(string RecordType, string? PayloadType, string? TurnId, DateTimeOffset? Timestamp,
        string? ToolName, string? CallId, string? ToolTurnId, string? Model, string? ReasoningEffort);
    private sealed record RolloutObservation(AgentRunState State, string? ActivityIdentity, DateTimeOffset? StartedAt)
    {
        public IReadOnlySet<string> PendingQuestionTurns { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public string? Model { get; init; }
        public string? ReasoningEffort { get; init; }
        public string? SelectionTurnId { get; init; }
    }
    private sealed record PendingQuestion(long Ordinal, int Count)
    {
        public HashSet<int> AnsweredIndexes { get; } = [];
    }
    private sealed record RolloutCache(long Length, DateTime Modified, RolloutObservation Observation);
}
