using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Services;

internal sealed record CodexActivityFeedSummary(string? TurnId, string Text, string Source);

/// <summary>
/// The dashboard explicitly displays chat excerpts. Keep that opt-in content path
/// separate from the alerts reader, which remains strictly lifecycle/metadata-only.
/// No app server, model request, credential access, persistent cache, or diagnostic output.
/// </summary>
internal sealed class CodexActivityFeedSummaryReader(string codexHome)
{
    private const int MaximumTailBytes = 512 * 1024;
    private const int MaximumReadsPerPoll = 16;
    private const int MaximumMessageBytes = 48 * 1024;
    private readonly string _codexHome = NormalizePath(codexHome);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private int _nextOffset;

    public IReadOnlyDictionary<string, CodexActivityFeedSummary> Read(IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<string> requestedIds, CancellationToken token)
    {
        var result = new Dictionary<string, CodexActivityFeedSummary>(StringComparer.Ordinal);
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _cache.Keys.Where(id => !requested.Contains(id)).ToArray()) _cache.Remove(stale);
        if (requested.Count == 0) return result;
        var current = agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        foreach (var id in requestedIds)
            if (_cache.TryGetValue(id, out var cached) && current.TryGetValue(id, out var agent) &&
                cached.Summary.TurnId == agent.LatestTurnId)
                result[id] = cached.Summary;

        try
        {
            using var database = new NativeSqlite(Path.Combine(_codexHome, "state_5.sqlite"));
            // Query paths only. Message content is restricted to explicitly selected JSONL files below.
            var paths = database.Query("SELECT id, rollout_path FROM threads")
                .Where(row => row[0] is not null && requested.Contains(row[0]!))
                .ToDictionary(row => row[0]!, row => row[1], StringComparer.Ordinal);
            var readCount = Math.Min(MaximumReadsPerPoll, requestedIds.Count);
            var start = _nextOffset % requestedIds.Count;
            for (var index = 0; index < readCount; index++)
            {
                token.ThrowIfCancellationRequested();
                var id = requestedIds[(start + index) % requestedIds.Count];
                if (!current.TryGetValue(id, out var agent)) continue;
                try
                {
                    if (!paths.TryGetValue(id, out var storedPath) || string.IsNullOrWhiteSpace(storedPath))
                    {
                        result[id] = new(agent.LatestTurnId, "", "Chat title · saved chat unavailable");
                        continue;
                    }
                    var path = NormalizePath(storedPath);
                    if (!IsSafeRollout(path))
                    {
                        result[id] = new(agent.LatestTurnId, "", "Chat title · saved chat unavailable");
                        continue;
                    }
                    var file = new FileInfo(path!);
                    if (_cache.TryGetValue(id, out var cached) && cached.Path == path &&
                        cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc &&
                        cached.Summary.TurnId == agent.LatestTurnId)
                    {
                        result[id] = cached.Summary;
                        continue;
                    }
                    var summary = ReadTail(path!, agent.LatestTurnId, token);
                    // A bounded tail can lose the request in a long turn; retain only same-turn excerpts.
                    if (summary.Text.Length == 0 && cached is not null && cached.Summary.TurnId == agent.LatestTurnId)
                        summary = cached.Summary;
                    result[id] = summary;
                    _cache[id] = new(path!, file.Length, file.LastWriteTimeUtc, summary);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    result[id] = new(agent.LatestTurnId, "", "Chat title · saved chat unavailable");
                }
            }
            _nextOffset = (start + readCount) % requestedIds.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            foreach (var id in requestedIds)
                if (!result.ContainsKey(id) && current.TryGetValue(id, out var agent))
                    result[id] = new(agent.LatestTurnId, "", "Chat title · saved chat unavailable");
        }
        return result;
    }

    private static CodexActivityFeedSummary ReadTail(string path, string? expectedTurnId, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - MaximumTailBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[(int)(stream.Length - offset)];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            var lineStart = 0;
            if (offset > 0)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', 0, count);
                lineStart = newline < 0 ? count : newline + 1;
            }
            string? user = null;
            string? commentary = null;
            string? currentTurnId = null;
            for (var index = lineStart; index < count; index++)
            {
                if (bytes[index] != '\n') continue;
                token.ThrowIfCancellationRequested();
                try
                {
                    var record = ReadLine(bytes.AsSpan(lineStart, index - lineStart));
                    if (record.TurnId is not null && record.TurnId != currentTurnId)
                    {
                        currentTurnId = record.TurnId;
                        user = null;
                        commentary = null;
                    }
                    // Never attribute a prior or racing new turn's messages to this status snapshot.
                    if (expectedTurnId is not null && currentTurnId != expectedTurnId) continue;
                    if (record.UserText is not null)
                    {
                        var clean = CleanUserRequest(record.UserText);
                        if (clean.Length > 0) user = clean;
                    }
                    if (record.Commentary is not null)
                        commentary = Compact(record.Commentary, 220);
                }
                catch (JsonException) { /* Partial or incompatible content is not status evidence. */ }
                finally { lineStart = index + 1; }
            }
            return !string.IsNullOrWhiteSpace(commentary)
                ? new(expectedTurnId, commentary, "Latest saved update")
                : !string.IsNullOrWhiteSpace(user)
                    ? new(expectedTurnId, user, "Latest saved request")
                    : new(expectedTurnId, "", "Chat title · recent message unavailable");
        }
        finally { Array.Clear(bytes); }
    }

    private static MessageRecord ReadLine(ReadOnlySpan<byte> line)
    {
        // Skip tool results and reasoning without materializing their strings.
        var reader = new Utf8JsonReader(line);
        string? type = null;
        JsonElement payload = default;
        using var document = SelectMessagePayload(ref reader, ref type);
        if (document is null) return new(null, null, null);
        payload = document.RootElement;
        var payloadType = String(payload, "type");
        if (type == "turn_context" || (type == "event_msg" && payloadType == "task_started"))
            return new(String(payload, "turn_id"), null, null);
        if (type == "event_msg")
        {
            if (payloadType == "user_message") return new(null, String(payload, "message"), null);
            if (payloadType == "agent_message" && String(payload, "phase") == "commentary")
                return new(null, null, String(payload, "message"));
        }
        if (type != "response_item" || payloadType != "message") return new(null, null, null);
        var role = String(payload, "role");
        if (role != "user" && !(role == "assistant" && String(payload, "phase") == "commentary"))
            return new(null, null, null);
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return new(null, null, null);
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (String(part, "type") is not ("input_text" or "output_text" or "text")) continue;
            var value = String(part, "text");
            if (value is null) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(value);
            if (text.Length > MaximumMessageBytes) break;
        }
        return role == "user" ? new(null, text.ToString(), null) : new(null, null, text.ToString());
    }

    private static JsonDocument? SelectMessagePayload(ref Utf8JsonReader reader, ref string? type)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var isType = reader.ValueTextEquals("type"u8);
            var isPayload = reader.ValueTextEquals("payload"u8);
            if (!reader.Read()) return null;
            if (isType && reader.TokenType == JsonTokenType.String) type = reader.GetString();
            else if (isPayload && reader.TokenType == JsonTokenType.StartObject)
            {
                if (type is not ("response_item" or "event_msg" or "turn_context")) { reader.Skip(); continue; }
                // Peek discriminator fields first, without decoding unrelated content.
                var probe = reader;
                string? payloadType = null, role = null, phase = null;
                while (probe.Read() && !(probe.TokenType == JsonTokenType.EndObject && probe.CurrentDepth == 1))
                {
                    if (probe.TokenType != JsonTokenType.PropertyName || probe.CurrentDepth != 2) continue;
                    var property = probe.ValueTextEquals("type"u8) ? 1 : probe.ValueTextEquals("role"u8) ? 2 :
                        probe.ValueTextEquals("phase"u8) ? 3 : 0;
                    if (!probe.Read()) break;
                    if (property != 0 && probe.TokenType == JsonTokenType.String)
                    {
                        if (property == 1) payloadType = probe.GetString();
                        if (property == 2) role = probe.GetString();
                        if (property == 3) phase = probe.GetString();
                    }
                    else probe.Skip();
                }
                var allowed = type == "turn_context" || type == "event_msg" &&
                    (payloadType is "task_started" or "user_message" || payloadType == "agent_message" && phase == "commentary") ||
                    type == "response_item" && payloadType == "message" &&
                    (role == "user" || role == "assistant" && phase == "commentary");
                if (!allowed || probe.BytesConsumed - reader.TokenStartIndex > MaximumMessageBytes)
                {
                    reader.Skip();
                    continue;
                }
                return JsonDocument.ParseValue(ref reader);
            }
            else reader.Skip();
        }
        return null;
    }

    private bool IsSafeRollout(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return false;
        var path = Path.GetFullPath(candidate);
        if (!path.StartsWith(_codexHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(_codexHome, path);
        if (!(relative.StartsWith("sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
              relative.StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return false;
        for (var ancestor = path; !string.Equals(ancestor, _codexHome, StringComparison.OrdinalIgnoreCase);)
        {
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return false;
            ancestor = Path.GetDirectoryName(ancestor)!;
            if (string.IsNullOrEmpty(ancestor)) return false;
        }
        return true;
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string CleanUserRequest(string text)
    {
        var request = text.LastIndexOf("## My request:", StringComparison.OrdinalIgnoreCase);
        if (request >= 0) text = text[(request + "## My request:".Length)..];
        else if (text.TrimStart().StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
                 text.TrimStart().StartsWith("<external_codex_apps_open_page>", StringComparison.Ordinal)) return "";
        text = Regex.Replace(text, @"<(environment_context|INSTRUCTIONS|skills_instructions|system_reminder|image)\b[^>]*>[\s\S]*?</\1>",
            " ", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"<[^>]{1,256}>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return Compact(text, 220);
    }

    internal static string Compact(string text, int maximum)
    {
        text = SensitiveDataProtection.Redact(text);
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
        return text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "…";
    }

    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsReadFailure(Exception ex) => ex is IOException or InvalidOperationException or
        UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or
        JsonException or RegexMatchTimeoutException;
    private sealed record CacheEntry(string Path, long Length, DateTime Modified, CodexActivityFeedSummary Summary);
    private sealed record MessageRecord(string? TurnId, string? UserText, string? Commentary);
}
