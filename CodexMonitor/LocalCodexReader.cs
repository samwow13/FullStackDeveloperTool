using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace FullStackLauncher.CodexMonitor;

public sealed record CodexMonitorSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<AgentSnapshot>> Projects,
    IReadOnlyDictionary<string, string> ParentWorkspaces);

/// <summary>
/// Reads internal local Codex metadata; this is not a supported public Codex API.
/// No prompts, messages, credentials, or tool-result bodies are queried or exposed.
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

    public CodexMonitorSnapshot ReadStatus(IReadOnlyList<string> projectPaths)
    {
        try
        {
            var projects = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => new WatchedProject(path, NormalizePath(path), index,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            var snapshots = projects.ToDictionary(project => project.Key, _ => new List<AgentSnapshot>(),
                StringComparer.OrdinalIgnoreCase);
            if (projects.Length == 0)
                return new(new Dictionary<string, IReadOnlyList<AgentSnapshot>>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

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
                if (thread.Guardian)
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
            var latest = history.Query("""
                SELECT t.thread_id, t.status, t.turn_id, t.completed_at
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
                var turn = new TurnMetadata(state, Required(row[2]), ReadCompletedAt(row[3]));
                if (states.TryGetValue(id, out var prior) && prior != turn)
                    turn = new TurnMetadata(AgentRunState.Unknown, null, null);
                states[id] = turn;
            }

            using var queue = new NativeSqlite(Path.Combine(_codexHome, "queue_1.sqlite"));
            var queued = queue.Query("SELECT DISTINCT thread_id FROM queued_items")
                .Select(row => Required(row[0])).ToHashSet(StringComparer.Ordinal);

            foreach (var thread in threads)
            {
                if (thread.Archived || thread.Guardian || !owners.TryGetValue(thread.Id, out var owner))
                    continue;
                var state = states.TryGetValue(thread.Id, out var projected)
                    ? projected.State
                    : ReadRolloutLifecycle(thread.RolloutPath, thread.HasUserEvent);
                if (queued.Contains(thread.Id) && state != AgentRunState.Running)
                    state = AgentRunState.Waiting;

                snapshots[owner.Key].Add(new AgentSnapshot(thread.Id, thread.Title, thread.ProjectPath, state,
                    thread.ParentId, projected?.TurnId, projected?.CompletedAt));
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
        // Deliberately omit first_user_message, preview, and all content tables.
        var rows = database.Query("SELECT id, cwd, source, rollout_path, archived, substr(COALESCE(NULLIF(name, ''), title), 1, 160), has_user_event FROM threads");
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
                row[5] ?? "Codex task", parentId, Required(row[3]), row[4] != "0", guardian, row[6] != "0"));
        }

        return result;
    }

    private AgentRunState ReadRolloutLifecycle(string rolloutPath, bool hasUserEvent)
    {
        // Older/migrated tasks may have no thread_turns projection. Inspect only
        // lifecycle discriminator fields in a bounded tail; never materialize
        // prompt, message, reasoning, or tool payload strings.
        var path = NormalizePath(rolloutPath);
        if (!IsWithin(path, _codexHome) || !File.Exists(path))
            return AgentRunState.Unknown;

        var file = new FileInfo(path);
        if (_rolloutCache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc &&
            !(cached.State == AgentRunState.Idle && hasUserEvent))
            return cached.State;

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
        var idleCandidate = !hasUserEvent && offset == 0 && count == bytes.Length && count > 0;
        var malformed = false;
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
            state = (record.RecordType == "event_msg" ? record.PayloadType : null) switch
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
            state = AgentRunState.Unknown;
        else if (idleCandidate && DateTime.UtcNow - file.LastWriteTimeUtc >= TimeSpan.FromMinutes(1))
            state = AgentRunState.Idle;

        var lengthBefore = file.Length;
        var modifiedBefore = file.LastWriteTimeUtc;
        file.Refresh();
        if (file.Length != lengthBefore || file.LastWriteTimeUtc != modifiedBefore)
            return AgentRunState.Unknown;

        // Revisit Unknown even without a file change: a brand-new empty voice
        // session must pass the quiet period before it can be classified as idle.
        if (state != AgentRunState.Unknown)
            _rolloutCache[path] = new RolloutCache(file.Length, file.LastWriteTimeUtc, state);
        return state;
    }

    private static LifecycleRecord ReadLifecycleLine(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        string? recordType = null;
        string? payloadType = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                continue;
            var isType = reader.ValueTextEquals("type"u8);
            var isPayload = reader.ValueTextEquals("payload"u8);
            if (!reader.Read())
                throw new JsonException();
            if (isType && reader.TokenType == JsonTokenType.String)
                recordType = reader.GetString();
            else if (isPayload && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    var isPayloadType = reader.ValueTextEquals("type"u8);
                    if (!reader.Read())
                        throw new JsonException();
                    if (isPayloadType && reader.TokenType == JsonTokenType.String)
                        payloadType = reader.GetString();
                    else
                        reader.Skip();
                }
            }
            else
                reader.Skip();
        }

        if (recordType is null || reader.CurrentDepth != 0)
            throw new JsonException();
        return new LifecycleRecord(recordType, payloadType);
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

    private static DateTimeOffset? ReadCompletedAt(string? value)
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

    private sealed record ThreadMetadata(string Id, string ProjectPath, string Title, string? ParentId, string RolloutPath, bool Archived, bool Guardian, bool HasUserEvent);
    private sealed record WatchedProject(string Key, string Path, int Order, HashSet<string> Repositories);
    private sealed record ProjectMatch(WatchedProject Project, int Kind, int Specificity);
    private sealed record TurnMetadata(AgentRunState State, string? TurnId, DateTimeOffset? CompletedAt);
    private sealed record LifecycleRecord(string RecordType, string? PayloadType);
    private sealed record RolloutCache(long Length, DateTime Modified, AgentRunState State);
}
