using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.CodexMonitor;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

internal sealed record CodexActivityFeedSummary(string? TurnId, string Text, string Source)
{
    public string? FeedbackIdentity { get; init; }
    public IReadOnlyList<CodexActivityFeedHistoryEntry> HistoryEntries { get; init; } = [];
    public IReadOnlyList<CodexActivityMessage> MessageHistory => HistoryEntries.Select(entry => entry.Message).ToArray();
}

internal sealed record CodexActivityFeedHistoryEntry(CodexActivityMessage Message, long Position)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>
/// The dashboard explicitly displays chat excerpts. Keep that opt-in content path
/// separate from the alerts reader, which remains strictly lifecycle/metadata-only.
/// No app server, model request, credential access, persistent cache, or diagnostic output.
/// </summary>
internal sealed partial class CodexActivityFeedSummaryReader(string codexHome)
{
    private const int MaximumTailBytes = 512 * 1024;
    private const int MaximumReadsPerPoll = 16;
    private const int MaximumMessageBytes = 48 * 1024;
    private const int MaximumHistoryMessages = 100;
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
                cached.Summary.TurnId == agent.LatestTurnId && !IsOpaqueEncodedText(cached.Summary.Text))
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
                        SetUnavailable(result, id, agent.LatestTurnId);
                        continue;
                    }
                    var path = NormalizePath(storedPath);
                    if (!IsSafeRollout(path))
                    {
                        SetUnavailable(result, id, agent.LatestTurnId);
                        continue;
                    }
                    var file = new FileInfo(path!);
                    if (_cache.TryGetValue(id, out var cached) && cached.Path == path &&
                        cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc &&
                        cached.Summary.TurnId == agent.LatestTurnId && !IsOpaqueEncodedText(cached.Summary.Text))
                    {
                        result[id] = cached.Summary;
                        continue;
                    }
                    var summary = ReadTail(path!, agent.LatestTurnId, token);
                    // Keep earlier messages as the bounded tail advances, including prior turns.
                    // A changed path or a truncated/rewritten file starts a separate history.
                    var history = MergeHistory(cached is not null && cached.Path == path &&
                        (file.Length > cached.Length || file.Length == cached.Length && file.LastWriteTimeUtc == cached.Modified)
                        ? cached.Summary.HistoryEntries : [], summary.HistoryEntries);
                    // A bounded tail can lose earlier same-turn content. Do not replace a known
                    // actual update with a request/start fallback and replay it after recovery.
                    if (cached is not null && cached.Summary.TurnId == agent.LatestTurnId &&
                        !IsOpaqueEncodedText(cached.Summary.Text) &&
                        (summary.Text.Length == 0 || summary.FeedbackIdentity is null && HasActualFeedback(cached.Summary)))
                    {
                        summary = cached.Summary;
                        // The retained latest excerpt remains reviewable even when its record left the tail.
                        history = MergeHistory(history, cached.Summary.HistoryEntries
                            .Where(entry => entry.Message.Identity == summary.FeedbackIdentity).ToArray());
                    }
                    if (summary.FeedbackIdentity is { } identity && history.FirstOrDefault(entry =>
                            entry.Message.Identity == identity || entry.Aliases.Contains(identity, StringComparer.Ordinal)) is { } latest)
                        summary = summary with { FeedbackIdentity = latest.Message.Identity };
                    summary = summary with { HistoryEntries = history };
                    result[id] = summary;
                    _cache[id] = new(path!, file.Length, file.LastWriteTimeUtc, summary);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    SetUnavailable(result, id, agent.LatestTurnId);
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

    private static bool HasActualFeedback(CodexActivityFeedSummary summary) =>
        summary.Text.Length > 0 && !IsOpaqueEncodedText(summary.Text) && summary.FeedbackIdentity is not null;

    private static IReadOnlyList<CodexActivityFeedHistoryEntry> MergeHistory(
        IReadOnlyList<CodexActivityFeedHistoryEntry> previous, IReadOnlyList<CodexActivityFeedHistoryEntry> current)
    {
        var merged = new List<CodexActivityFeedHistoryEntry>();
        foreach (var entry in previous.Concat(current).OrderBy(item => item.Position))
        {
            var index = merged.FindIndex(existing => existing.Position == entry.Position ||
                existing.Message.Identity == entry.Message.Identity ||
                existing.Aliases.Contains(entry.Message.Identity, StringComparer.Ordinal) ||
                entry.Aliases.Contains(existing.Message.Identity, StringComparer.Ordinal) ||
                existing.Aliases.Intersect(entry.Aliases, StringComparer.Ordinal).Any());
            if (index < 0) { merged.Add(entry); continue; }
            var earlier = merged[index];
            // Prefer a real message ID, otherwise retain the first observed legacy identity.
            var identity = earlier.Message.Identity.StartsWith("saved-update:", StringComparison.Ordinal) &&
                !entry.Message.Identity.StartsWith("saved-update:", StringComparison.Ordinal)
                ? entry.Message.Identity : earlier.Message.Identity;
            merged[index] = entry with
            {
                Message = new(identity, entry.Message.Text),
                Position = Math.Min(earlier.Position, entry.Position),
                Aliases = earlier.Aliases.Concat(entry.Aliases).Append(earlier.Message.Identity)
                    .Append(entry.Message.Identity).Where(alias => alias != identity).Distinct(StringComparer.Ordinal).TakeLast(4).ToArray()
            };
        }
        return merged.OrderBy(entry => entry.Position).TakeLast(MaximumHistoryMessages).ToArray();
    }

    private static void SetUnavailable(IDictionary<string, CodexActivityFeedSummary> result, string id, string? turnId)
    {
        // The preloaded cache is already restricted to the status snapshot's exact turn.
        // A transient file/path failure must not turn its update into a synthetic start.
        if (result.TryGetValue(id, out var previous) && previous.TurnId == turnId && HasActualFeedback(previous))
            return;
        result[id] = new(turnId, "", "Chat title · saved chat unavailable");
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
            string? feedbackIdentity = null;
            string? commentarySource = null;
            string? currentTurnId = null;
            var history = new Dictionary<string, CodexActivityFeedHistoryEntry>(StringComparer.Ordinal);
            var expectedTurnSeen = expectedTurnId is null;
            var historyEnded = false;
            MessageRecord? previousPublicMessage = null;
            string? previousHistoryIdentity = null;
            var previousWasPaired = false;
            for (var index = lineStart; index < count; index++)
            {
                if (bytes[index] != '\n') continue;
                token.ThrowIfCancellationRequested();
                try
                {
                    var record = ReadLine(bytes.AsSpan(lineStart, index - lineStart));
                    if (record.TurnId is not null && record.TurnId != currentTurnId)
                    {
                        if (expectedTurnSeen && expectedTurnId is not null && record.TurnId != expectedTurnId)
                            historyEnded = true;
                        currentTurnId = record.TurnId;
                        previousPublicMessage = null;
                        previousHistoryIdentity = null;
                        previousWasPaired = false;
                        user = null;
                        commentary = null;
                        feedbackIdentity = null;
                        commentarySource = null;
                    }
                    if (record.UserText is not null)
                    {
                        previousPublicMessage = null;
                        previousHistoryIdentity = null;
                        previousWasPaired = false;
                    }
                    if (currentTurnId == expectedTurnId) expectedTurnSeen = true;
                    string? cleanCommentary = null;
                    string? displayCommentary = null;
                    string? messageIdentity = null;
                    if (record.Commentary is not null)
                    {
                        cleanCommentary = CleanMessage(record.Commentary);
                        // Filter before both compaction and bounded bubble-history retention.
                        // Raw chat export reads the saved messages through a separate path.
                        displayCommentary = CodexBubbleTextFormatter.Format(cleanCommentary);
                        if (!string.IsNullOrWhiteSpace(displayCommentary))
                        {
                            messageIdentity = record.MessageId is { Length: > 0 } messageId
                                ? record.IdentityKind + ":" + currentTurnId + ":" + messageId
                                : "saved-update:" + currentTurnId + ":" +
                                    (offset + lineStart).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!historyEnded || currentTurnId == expectedTurnId)
                            {
                                var position = history.TryGetValue(messageIdentity, out var previous)
                                    ? previous.Position : offset + lineStart;
                                IReadOnlyList<string> aliases = previous?.Aliases ?? [];
                                var paired = !previousWasPaired && previousPublicMessage is not null &&
                                    previousHistoryIdentity is not null && previousPublicMessage.Commentary == record.Commentary &&
                                    previousPublicMessage.Representation != record.Representation &&
                                    previousPublicMessage.Representation != "send" && record.Representation != "send" &&
                                    (string.IsNullOrEmpty(previousPublicMessage.MessageId) || string.IsNullOrEmpty(record.MessageId)) &&
                                    history.TryGetValue(previousHistoryIdentity, out previous);
                                if (paired)
                                {
                                    var pairedEntry = previous!;
                                    position = pairedEntry.Position;
                                    var pairedIdentity = !string.IsNullOrEmpty(previousPublicMessage!.MessageId) ||
                                        string.IsNullOrEmpty(record.MessageId) ? previousHistoryIdentity! : messageIdentity;
                                    aliases = pairedEntry.Aliases.Append(previousHistoryIdentity!).Append(messageIdentity)
                                        .Where(alias => alias != pairedIdentity).Distinct(StringComparer.Ordinal).TakeLast(4).ToArray();
                                    history.Remove(previousHistoryIdentity!);
                                    messageIdentity = pairedIdentity;
                                }
                                history[messageIdentity] = new(new(messageIdentity, cleanCommentary), position) { Aliases = aliases };
                                previousPublicMessage = record;
                                previousHistoryIdentity = messageIdentity;
                                previousWasPaired = paired;
                                if (history.Count > MaximumHistoryMessages)
                                    history.Remove(history.MinBy(entry => entry.Value.Position).Key);
                            }
                        }
                    }
                    // Never attribute a prior or racing new turn's messages to this status snapshot.
                    if (expectedTurnId is not null && currentTurnId != expectedTurnId) continue;
                    if (record.UserText is not null)
                    {
                        var clean = CleanUserRequest(record.UserText);
                        if (clean.Length > 0) user = clean;
                    }
                    if (!string.IsNullOrWhiteSpace(displayCommentary))
                    {
                        commentary = Compact(displayCommentary, 220);
                        commentarySource = record.Source;
                        // Hidden payloads do not replace or replay the last readable update.
                        feedbackIdentity = messageIdentity;
                    }
                }
                catch (JsonException) { /* Partial or incompatible content is not status evidence. */ }
                finally { lineStart = index + 1; }
            }
            var summary = !string.IsNullOrWhiteSpace(commentary)
                ? new CodexActivityFeedSummary(expectedTurnId, commentary, commentarySource ?? "Latest saved update") { FeedbackIdentity = feedbackIdentity }
                : !string.IsNullOrWhiteSpace(user)
                    ? new CodexActivityFeedSummary(expectedTurnId, user, "Latest saved request")
                    : new(expectedTurnId, "", "Chat title · recent message unavailable");
            return summary with
            {
                // Without this turn's attribution, a racing future turn cannot supply its history.
                HistoryEntries = expectedTurnSeen ? history.Values.OrderBy(entry => entry.Position).ToArray() : []
            };
        }
        finally { Array.Clear(bytes); }
    }

    private static MessageRecord ReadLine(ReadOnlySpan<byte> line,
        int maximumMessageBytes = MaximumMessageBytes, bool preserveTextParts = false)
    {
        // Skip tool results and reasoning without materializing their strings.
        // Only the exact outgoing collaboration.send_message call is a public agent message.
        var reader = new Utf8JsonReader(line);
        string? type = null;
        if (preserveTextParts)
        {
            // Export accepts any JSON property order; the passive reader keeps its
            // existing inexpensive path for the current rollout format.
            var probe = reader;
            while (probe.Read())
            {
                if (probe.TokenType != JsonTokenType.PropertyName || probe.CurrentDepth != 1) continue;
                var isType = probe.ValueTextEquals("type"u8);
                if (!probe.Read()) break;
                if (isType && probe.TokenType == JsonTokenType.String) { type = probe.GetString(); break; }
                probe.Skip();
            }
        }
        JsonElement payload = default;
        using var document = SelectMessagePayload(ref reader, ref type, out var recordTurnId, maximumMessageBytes);
        if (document is null) return new(recordTurnId, null, null);
        payload = document.RootElement;
        var payloadType = String(payload, "type");
        if (type == "turn_context" || (type == "event_msg" && payloadType == "task_started"))
            return new(String(payload, "turn_id"), null, null);
        if (type == "event_msg")
        {
            if (payloadType == "user_message") return new(recordTurnId, ReadPublicText(payload, "message"), null,
                String(payload, "id"), Representation: "event");
            if (payloadType == "agent_message" && String(payload, "channel") != "analysis" &&
                IsPublicMessagePhase(String(payload, "phase")))
                return new(recordTurnId, null, ReadPublicText(payload, "message"), String(payload, "id"),
                    Representation: "event", Phase: String(payload, "phase"));
            if (payloadType == "item_completed" && payload.TryGetProperty("item", out var item) &&
                String(item, "type") == "AgentMessage" && IsPublicMessagePhase(String(item, "phase")))
                return new(recordTurnId, null, ReadMessageText(item, maximumMessageBytes, preserveTextParts), String(item, "id"),
                    Representation: "item", Phase: String(item, "phase"));
        }
        if (type == "response_item" && payloadType == "function_call" &&
            String(payload, "namespace") == "collaboration" && String(payload, "name") == "send_message")
        {
            var arguments = String(payload, "arguments");
            if (arguments is null || arguments.Length == 0 || arguments.Length > maximumMessageBytes)
                return new(recordTurnId, null, null);
            using var parsed = JsonDocument.Parse(arguments);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return new(recordTurnId, null, null);
            var sentMessage = ReadPublicText(parsed.RootElement, "message");
            return new(recordTurnId, null, sentMessage, String(payload, "call_id") ?? String(payload, "id"),
                "saved-send", "Latest saved agent message", "send");
        }
        if (type != "response_item" || payloadType != "message") return new(recordTurnId, null, null);
        var role = String(payload, "role");
        if (role != "user" && !(role == "assistant" && IsPublicMessagePhase(String(payload, "phase"))))
            return new(recordTurnId, null, null);
        var text = ReadMessageText(payload, maximumMessageBytes, preserveTextParts);
        return role == "user" ? new(recordTurnId, text, null, String(payload, "id"))
            : new(recordTurnId, null, text, String(payload, "id"), Phase: String(payload, "phase"));
    }

    private static string? ReadMessageText(JsonElement payload,
        int maximumMessageBytes = MaximumMessageBytes, bool preserveTextParts = false)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return preserveTextParts ? ReadPublicText(payload, "text") : null;
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (String(part, "type") is not ("input_text" or "output_text" or "text" or "Text")) continue;
            var value = ReadPublicText(part, "text");
            if (value is null) continue;
            // Preserve part boundaries until chat presentation filters code and metadata.
            if (text.Length > 0) text.Append("\n\n");
            text.Append(value);
            if (!preserveTextParts && text.Length > maximumMessageBytes) break;
        }
        return text.ToString();
    }

    private static JsonDocument? SelectMessagePayload(ref Utf8JsonReader reader, ref string? type,
        out string? recordTurnId, int maximumMessageBytes = MaximumMessageBytes)
    {
        recordTurnId = null;
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
                string? payloadType = null, role = null, phase = null, channel = null, callNamespace = null, callName = null;
                string? explicitTurnId = null, metadataTurnId = null;
                var publicMessageItem = false;
                while (probe.Read() && !(probe.TokenType == JsonTokenType.EndObject && probe.CurrentDepth == 1))
                {
                    if (probe.TokenType != JsonTokenType.PropertyName || probe.CurrentDepth != 2) continue;
                    var property = probe.ValueTextEquals("type"u8) ? 1 : probe.ValueTextEquals("role"u8) ? 2 :
                        probe.ValueTextEquals("phase"u8) ? 3 : probe.ValueTextEquals("item"u8) ? 4 :
                        probe.ValueTextEquals("turn_id"u8) ? 5 : probe.ValueTextEquals("namespace"u8) ? 6 :
                        probe.ValueTextEquals("name"u8) ? 7 :
                        probe.ValueTextEquals("internal_chat_message_metadata_passthrough"u8) ? 8 :
                        probe.ValueTextEquals("channel"u8) ? 9 : 0;
                    if (!probe.Read()) break;
                    if (property is 1 or 2 or 3 or 5 or 6 or 7 or 9 && probe.TokenType == JsonTokenType.String)
                    {
                        if (property == 1) payloadType = probe.GetString();
                        if (property == 2) role = probe.GetString();
                        if (property == 3) phase = probe.GetString();
                        if (property == 5) explicitTurnId = probe.GetString();
                        if (property == 6) callNamespace = probe.GetString();
                        if (property == 7) callName = probe.GetString();
                        if (property == 9) channel = probe.GetString();
                    }
                    else if (property == 4 && probe.TokenType == JsonTokenType.StartObject)
                        publicMessageItem = IsPublicAgentMessageItem(ref probe);
                    else if (property == 8 && probe.TokenType == JsonTokenType.StartObject)
                        metadataTurnId = ReadTurnMetadata(ref probe);
                    else probe.Skip();
                }
                // Explicit item completion and saved message metadata retain attribution
                // when a long turn's start record has left the bounded tail. Tool content stays skipped.
                if (type == "event_msg" && payloadType is "task_started" or "item_completed" or "user_message" or "agent_message")
                    recordTurnId = explicitTurnId;
                else if (type == "turn_context") recordTurnId = explicitTurnId;
                else if (type == "response_item") recordTurnId = metadataTurnId;
                var allowed = type == "turn_context" || type == "event_msg" &&
                    (payloadType is "task_started" or "user_message" ||
                        payloadType == "agent_message" && channel != "analysis" && IsPublicMessagePhase(phase) ||
                        payloadType == "item_completed" && publicMessageItem) ||
                    type == "response_item" && channel != "analysis" &&
                    (payloadType == "message" && (role == "user" || role == "assistant" && IsPublicMessagePhase(phase)) ||
                        payloadType == "function_call" && callNamespace == "collaboration" && callName == "send_message");
                if (!allowed || probe.BytesConsumed - reader.TokenStartIndex > maximumMessageBytes)
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

    private static bool IsPublicAgentMessageItem(ref Utf8JsonReader reader)
    {
        string? type = null, phase = null, channel = null;
        var depth = reader.CurrentDepth;
        while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != depth + 1) continue;
            var isType = reader.ValueTextEquals("type"u8);
            var isPhase = reader.ValueTextEquals("phase"u8);
            var isChannel = reader.ValueTextEquals("channel"u8);
            if (!reader.Read()) return false;
            if ((isType || isPhase || isChannel) && reader.TokenType == JsonTokenType.String)
            {
                if (isType) type = reader.GetString();
                if (isPhase) phase = reader.GetString();
                if (isChannel) channel = reader.GetString();
            }
            else reader.Skip();
        }
        return type == "AgentMessage" && channel != "analysis" && IsPublicMessagePhase(phase);
    }

    private static bool IsPublicMessagePhase(string? phase) => phase is null or "commentary" or "final_answer";

    private static string? ReadTurnMetadata(ref Utf8JsonReader reader)
    {
        string? turnId = null;
        var depth = reader.CurrentDepth;
        while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != depth + 1) continue;
            var isTurnId = reader.ValueTextEquals("turn_id"u8);
            if (!reader.Read()) break;
            if (isTurnId && reader.TokenType == JsonTokenType.String) turnId = reader.GetString();
            else reader.Skip();
        }
        return turnId;
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
        text = CodexBubbleTextFormatter.Format(text);
        text = Regex.Replace(text, @"<[^>]{1,256}>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return Compact(text, 220);
    }

    private static string CleanMessage(string text)
    {
        if (IsOpaqueEncodedText(text)) return "";
        text = SensitiveDataProtection.Redact(text).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text.Length <= MaximumMessageBytes ? text : text[..(MaximumMessageBytes - 1)].TrimEnd() + "…";
    }

    internal static string Compact(string text, int maximum)
    {
        if (IsOpaqueEncodedText(text)) return "";
        text = SensitiveDataProtection.Redact(text);
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
        return text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "…";
    }

    private static string? ReadPublicText(JsonElement parent, string property)
    {
        var text = String(parent, property);
        return text is null || IsOpaqueEncodedText(text) ? null : text;
    }

    private static bool IsOpaqueEncodedText(string text)
    {
        // Skip only a standalone long encoded token. Prose that mentions its prefix stays readable.
        var value = text.AsSpan().Trim();
        if (value.Length < 80 || !value.StartsWith("gAAAA".AsSpan(), StringComparison.Ordinal)) return false;
        foreach (var character in value)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or
                  '_' or '-' or '+' or '/' or '=')) return false;
        return true;
    }

    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsReadFailure(Exception ex) => ex is IOException or InvalidOperationException or
        UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or
        JsonException or RegexMatchTimeoutException;
    private sealed record CacheEntry(string Path, long Length, DateTime Modified, CodexActivityFeedSummary Summary);
    private sealed record MessageRecord(string? TurnId, string? UserText, string? Commentary, string? MessageId = null,
        string IdentityKind = "saved-message", string Source = "Latest saved update", string Representation = "response",
        string? Phase = null);
}
