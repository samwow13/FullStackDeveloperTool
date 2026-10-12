using System.IO;
using System.Text.Json;

namespace FullStackLauncher.CodexMonitor;

/// <summary>Read-only metadata adapter for both SQLite and pre-SQLite CLI history.</summary>
internal sealed class CodexLocalMetadata(string codexHome)
{
    private const int MaximumFiles = 50_000;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumUncachedHeaderBytes = 32 * 1024 * 1024;
    private readonly Dictionary<string, CachedHeader> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal static string? FindDatabase(string home, string prefix)
    {
        // Use the newest present store only. Never silently read an older,
        // stale database when the active schema is unreadable or incompatible.
        if (!Directory.Exists(home)) throw Unavailable();
        return Directory.EnumerateFiles(home, prefix + "_*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Suffix: Path.GetFileNameWithoutExtension(path)[(prefix.Length + 1)..]))
            .Where(item => int.TryParse(item.Suffix, out var version) && version > 0)
            .OrderByDescending(item => int.Parse(item.Suffix, System.Globalization.CultureInfo.InvariantCulture))
            .Select(item => item.Path).FirstOrDefault();
    }

    internal static HashSet<string?> Columns(NativeSqlite database, string table) =>
        database.Query("PRAGMA table_info(" + table + ")").Select(row => row[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static void RequireColumns(HashSet<string?> columns, params string[] required)
    {
        if (required.Any(column => !columns.Contains(column))) throw Unavailable();
    }

    internal IReadOnlyList<LocalThreadMetadata> ReadThreads(CancellationToken token = default)
    {
        lock (_gate) return ReadThreadsCore(token);
    }

    private IReadOnlyList<LocalThreadMetadata> ReadThreadsCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FindDatabase(codexHome, "state") is { } statePath)
        {
            using var database = new NativeSqlite(statePath);
            var columns = Columns(database, "threads");
            RequireColumns(columns, "id", "cwd", "source", "rollout_path", "archived", "title");
            var title = columns.Contains("name") ? "COALESCE(NULLIF(name, ''), title)" : "title";
            var userEvent = columns.Contains("has_user_event") ? "has_user_event" : "1";
            var model = columns.Contains("model") ? "model" : "NULL";
            var effort = columns.Contains("reasoning_effort") ? "reasoning_effort" : "NULL";
            // Never select first_user_message, instructions, or credential fields.
            var ordering = columns.Contains("updated_at") ? " ORDER BY updated_at DESC" : "";
            return database.Query($"SELECT id, cwd, source, rollout_path, archived, substr({title}, 1, 160), {userEvent}, {model}, {effort} FROM threads{ordering}")
                .Select(row => new LocalThreadMetadata(Required(row[0]), Required(row[1]), Required(row[2]),
                    Required(row[3]), row[4] != "0", row[5] ?? "Codex task", row[6] != "0", row[7], row[8])).ToArray();
        }

        // Older CLI versions have JSONL history without any state database.
        // Index session headers only; titles and prompts are deliberately omitted.
        var result = new List<LocalThreadMetadata>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncachedBytes = 0;
        foreach (var (folder, archived) in new[] { ("sessions", false), ("archived_sessions", true) })
        {
            var root = Path.Combine(codexHome, folder);
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 8
            };
            foreach (var path in Directory.EnumerateFiles(root, "rollout-*.jsonl", options))
            {
                token.ThrowIfCancellationRequested();
                if (!retained.Add(path) || retained.Count > MaximumFiles) throw Unavailable();
                var file = new FileInfo(path);
                if (!_headers.TryGetValue(path, out var cached) || cached.Length != file.Length ||
                    cached.Modified != file.LastWriteTimeUtc || cached.Created != file.CreationTimeUtc)
                {
                    var capacity = (int)Math.Min(file.Length, MaximumHeaderBytes);
                    var metadata = ReadHeader(path, capacity, archived, out var bytesRead);
                    uncachedBytes += bytesRead;
                    if (uncachedBytes > MaximumUncachedHeaderBytes) throw Unavailable();
                    _headers[path] = cached = new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, metadata);
                }
                result.Add(cached.Metadata);
            }
        }
        foreach (var obsolete in _headers.Keys.Where(path => !retained.Contains(path)).ToArray()) _headers.Remove(obsolete);
        return result.OrderByDescending(thread => _headers[thread.RolloutPath].Modified).ToArray();
    }

    internal string ReadRolloutPath(string threadId)
    {
        if (!Guid.TryParseExact(threadId, "D", out var expectedId)) throw Unavailable();
        if (FindDatabase(codexHome, "state") is { } statePath)
        {
            using var database = new NativeSqlite(statePath);
            RequireColumns(Columns(database, "threads"), "id", "rollout_path");
            var rows = database.Query($"SELECT id, rollout_path FROM threads WHERE id = '{expectedId:D}' LIMIT 2");
            return rows.Count == 1 && Guid.TryParseExact(rows[0][0], "D", out var storedId) && storedId == expectedId
                ? Required(rows[0][1]) : throw Unavailable();
        }
        var matches = ReadThreads().Where(thread => string.Equals(thread.Id, threadId, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].RolloutPath : throw Unavailable();
    }

    private static LocalThreadMetadata ReadHeader(string path, int capacity, bool archived, out int bytesRead)
    {
        bytesRead = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[capacity];
        try
        {
            var count = 0;
            var newline = -1;
            while (count < bytes.Length && newline < 0)
            {
                var read = stream.Read(bytes, count, Math.Min(4096, bytes.Length - count));
                if (read == 0) break;
                newline = Array.IndexOf(bytes, (byte)'\n', count, read);
                count += read;
                bytesRead = count;
            }
            if (newline < 0) throw Unavailable();
            var header = bytes.AsSpan(0, newline);
            if (header.StartsWith("\uFEFF"u8)) header = header[3..];
            var reader = new Utf8JsonReader(header);
            string? type = null, id = null, cwd = null, source = null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var isType = reader.ValueTextEquals("type"u8);
                var isPayload = reader.ValueTextEquals("payload"u8);
                if (!reader.Read()) throw new JsonException();
                if (isType && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                else if (isPayload && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1))
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2) continue;
                        var field = reader.ValueTextEquals("id"u8) ? 1 : reader.ValueTextEquals("cwd"u8) ? 2 :
                            reader.ValueTextEquals("source"u8) ? 3 : 0;
                        if (!reader.Read()) throw new JsonException();
                        if (field != 0 && reader.TokenType == JsonTokenType.String)
                        {
                            if (field == 1) id = reader.GetString();
                            if (field == 2) cwd = reader.GetString();
                            if (field == 3) source = reader.GetString();
                        }
                        else if (field == 3 && reader.TokenType == JsonTokenType.StartObject)
                        {
                            using var value = JsonDocument.ParseValue(ref reader);
                            source = value.RootElement.GetRawText();
                        }
                        else reader.Skip();
                    }
                }
                else reader.Skip();
            }
            if (type != "session_meta" || !Guid.TryParseExact(id, "D", out _) ||
                string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) throw Unavailable();
            return new(Required(id), cwd, source ?? "cli", path, archived, "Codex task", true, null, null);
        }
        finally { Array.Clear(bytes); }
    }

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw Unavailable() : value;
    private static InvalidOperationException Unavailable() => new(
        "Codex local metadata is unavailable or incompatible; completion is unconfirmed.");
    private sealed record CachedHeader(long Length, DateTime Modified, DateTime Created, LocalThreadMetadata Metadata);
}

internal sealed record LocalThreadMetadata(string Id, string ProjectPath, string Source, string RolloutPath,
    bool Archived, string Title, bool HasUserEvent, string? Model, string? ReasoningEffort);
