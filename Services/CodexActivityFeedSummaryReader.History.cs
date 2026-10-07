using System.IO;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Services;

internal sealed record CodexChatHistoryExport(string Text, int MessageCount, int SkippedRecords);

internal sealed partial class CodexActivityFeedSummaryReader
{
    private const long MaximumExportFileBytes = 256L * 1024 * 1024;
    private const int MaximumExportRecordBytes = 4 * 1024 * 1024;
    private const int MaximumExportCharacters = 16 * 1024 * 1024;
    private const int MaximumExportMessages = 100_000;
    private const int MaximumExportRecords = 1_000_000;

    /// <summary>
    /// Reads only the selected thread's saved public transcript. Call on a worker thread;
    /// this operation never changes the passive summary cache or Codex state.
    /// </summary>
    internal CodexChatHistoryExport ReadChatHistory(string agentId, CancellationToken token)
    {
        if (!Guid.TryParseExact(agentId, "D", out var threadId))
            throw new InvalidOperationException("The selected Codex chat identity is invalid. Refresh the crew and try again.");
        token.ThrowIfCancellationRequested();
        var canonicalId = threadId.ToString("D");
        string path;
        using (var database = new NativeSqlite(Path.Combine(_codexHome, "state_5.sqlite")))
        {
            // The validated canonical GUID is the only SQL input. Do not enumerate other chats.
            var rows = database.Query($"SELECT id, rollout_path FROM threads WHERE id = '{canonicalId}' LIMIT 2");
            if (rows.Count != 1 || !Guid.TryParseExact(rows[0][0], "D", out var storedId) || storedId != threadId ||
                string.IsNullOrWhiteSpace(rows[0][1]))
                throw new InvalidOperationException("Saved history for this exact Codex chat is unavailable. Refresh and try again.");
            path = NormalizePath(rows[0][1]!);
            if (!IsSafeRollout(path))
                throw new InvalidOperationException("Saved history for this Codex chat is outside its allowed local history folder.");
        }

        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var snapshotLength = stream.Length;
            if (snapshotLength > MaximumExportFileBytes)
                throw new InvalidOperationException("This saved chat exceeds the 256 MiB history read limit. Nothing was copied.");
            if (snapshotLength == 0)
                throw new InvalidOperationException("No saved text history is available for this Codex chat yet.");
            var state = new ChatHistoryExportState(threadId, token);
            var buffer = new byte[64 * 1024];
            using var line = new MemoryStream(64 * 1024);
            try
            {
                var remaining = snapshotLength;
                var oversized = false;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                        throw new InvalidOperationException("Saved chat changed while its history was being read. Nothing was copied; try again.");
                    remaining -= read;
                    var position = 0;
                    while (position < read)
                    {
                        token.ThrowIfCancellationRequested();
                        var newline = buffer.AsSpan(position, read - position).IndexOf((byte)'\n');
                        var length = newline < 0 ? read - position : newline;
                        if (!oversized)
                        {
                            if (line.Length + length > MaximumExportRecordBytes)
                            {
                                oversized = true;
                                line.SetLength(0);
                                line.Position = 0;
                            }
                            else line.Write(buffer, position, length);
                        }
                        position += length;
                        if (newline < 0) continue;
                        if (oversized) state.SkipOversizedRecord();
                        else state.ReadRecord(line.GetBuffer().AsSpan(0, (int)line.Length));
                        line.SetLength(0);
                        line.Position = 0;
                        oversized = false;
                        position++;
                    }
                }
                // A complete final JSON record need not end with a newline. A partial record
                // is counted explicitly instead of copying truncated text.
                if (oversized) state.SkipOversizedRecord();
                else if (line.Length > 0) state.ReadRecord(line.GetBuffer().AsSpan(0, (int)line.Length));
                if (stream.Length < snapshotLength)
                    throw new InvalidOperationException("Saved chat changed while its history was being read. Nothing was copied; try again.");
                return state.CreateExport(snapshotLength);
            }
            finally
            {
                Array.Clear(buffer);
                Array.Clear(line.GetBuffer());
            }
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Saved history for this Codex chat could not be read. Nothing was copied; try again.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Saved history for this Codex chat cannot be accessed. Nothing was copied.");
        }
    }

    private sealed class ChatHistoryExportState(Guid threadId, CancellationToken token)
    {
        private readonly List<ChatHistoryExportEntry> _entries = [];
        private readonly Dictionary<ChatHistoryMessageKey, int> _messageIds = [];
        private string? _currentTurnId;
        private ChatHistoryPair? _previous;
        private bool _identityConfirmed;
        private int _records;
        private int _skippedRecords;
        private int _characters;

        internal void SkipOversizedRecord()
        {
            CountRecord();
            if (!_identityConfirmed)
                throw new InvalidOperationException("The saved Codex chat header exceeds the 4 MiB record limit. Nothing was copied.");
            _skippedRecords++;
            _previous = null;
        }

        internal void ReadRecord(ReadOnlySpan<byte> line)
        {
            token.ThrowIfCancellationRequested();
            if (_records == 0 && line.StartsWith("\uFEFF"u8)) line = line[3..];
            if (IsBlankHistoryLine(line)) return;
            CountRecord();
            try
            {
                ValidateHistoryRecord(line, token);
                if (!_identityConfirmed)
                {
                    if (!MatchesHistoryIdentity(line, threadId))
                        throw new InvalidOperationException("The saved history header does not match this exact Codex chat. Nothing was copied.");
                    _identityConfirmed = true;
                    return;
                }
                var record = ReadLine(line, MaximumExportRecordBytes, preserveTextParts: true);
                if (record.TurnId is { } turnId && turnId != _currentTurnId)
                {
                    _currentTurnId = turnId;
                    _previous = null;
                }
                var role = record.UserText is not null ? "User" : record.Representation == "send" ? "Agent message" : "Assistant";
                var rawText = record.UserText ?? record.Commentary;
                if (string.IsNullOrWhiteSpace(rawText))
                    return;
                var text = SensitiveDataProtection.Redact(rawText);
                var entry = new ChatHistoryExportEntry(role, _currentTurnId, text, record.Phase);
                ChatHistoryMessageKey? key = string.IsNullOrEmpty(record.MessageId) ? null :
                    new(role, _currentTurnId, record.IdentityKind, record.MessageId);
                if (key is not null && _messageIds.TryGetValue(key, out var existing))
                {
                    ReplaceEntry(existing, entry);
                    var representations = _previous is { } prior && prior.Index == existing && prior.RawText == rawText
                        ? prior.Representations : new HashSet<string>(StringComparer.Ordinal);
                    representations.Add(record.Representation);
                    _previous = new(existing, rawText, role, _currentTurnId, record.MessageId, representations);
                    return;
                }
                // Only adjacent public representations may form a legacy pair. Repeating
                // the same representation or using distinct real IDs remains a new message.
                if (_previous is { } previous && previous.Role == role && previous.TurnId == _currentTurnId &&
                    previous.RawText == rawText && record.Representation != "send" &&
                    !previous.Representations.Contains("send") && !previous.Representations.Contains(record.Representation) &&
                    (string.IsNullOrEmpty(previous.MessageId) || string.IsNullOrEmpty(record.MessageId)))
                {
                    ReplaceEntry(previous.Index, entry);
                    if (key is not null) _messageIds[key] = previous.Index;
                    previous.Representations.Add(record.Representation);
                    _previous = previous with { MessageId = record.MessageId ?? previous.MessageId };
                    return;
                }
                if (_entries.Count >= MaximumExportMessages)
                    throw new InvalidOperationException("This saved chat exceeds the 100,000-message history limit. Nothing was copied.");
                CheckCharacters(text.Length);
                var index = _entries.Count;
                _entries.Add(entry);
                _characters += text.Length;
                if (key is not null) _messageIds[key] = index;
                _previous = new(index, rawText, role, _currentTurnId, record.MessageId, [record.Representation]);
            }
            catch (JsonException)
            {
                if (!_identityConfirmed)
                    throw new InvalidOperationException("The saved Codex chat header is incomplete or invalid. Nothing was copied; try again.");
                _skippedRecords++;
                _previous = null;
            }
        }

        internal CodexChatHistoryExport CreateExport(long snapshotLength)
        {
            token.ThrowIfCancellationRequested();
            if (!_identityConfirmed)
                throw new InvalidOperationException("The saved history header is unavailable for this Codex chat. Nothing was copied.");
            if (_entries.Count == 0)
                throw new InvalidOperationException(_skippedRecords == 0
                    ? "No saved public user or assistant text is available for this Codex chat yet."
                    : $"No readable public text was found; {_skippedRecords} malformed, oversized, or incomplete saved records were skipped. Nothing was copied.");
            var result = new StringBuilder();
            AppendExport(result, $"Saved public chat history ({_entries.Count} messages; {snapshotLength:N0} saved bytes at copy time).\nRecognizable credentials are redacted.\n");
            if (_skippedRecords > 0)
                AppendExport(result, $"Partial history: skipped {_skippedRecords} malformed, oversized, or incomplete saved record{(_skippedRecords == 1 ? "" : "s")}.\n");
            foreach (var entry in _entries)
            {
                token.ThrowIfCancellationRequested();
                var phase = entry.Role == "Assistant" ? entry.Phase switch
                {
                    "commentary" => " (progress)",
                    "final_answer" => " (final)",
                    _ => ""
                } : "";
                AppendExport(result, $"\n[{entry.Role}{phase}]\n");
                AppendExport(result, entry.Text);
                AppendExport(result, "\n");
            }
            return new(result.ToString(), _entries.Count, _skippedRecords);
        }

        private void CountRecord()
        {
            token.ThrowIfCancellationRequested();
            if (++_records > MaximumExportRecords)
                throw new InvalidOperationException("This saved chat exceeds the 1,000,000-record history limit. Nothing was copied.");
        }

        private void ReplaceEntry(int index, ChatHistoryExportEntry entry)
        {
            var previous = _entries[index];
            CheckCharacters(entry.Text.Length - previous.Text.Length);
            _characters += entry.Text.Length - previous.Text.Length;
            _entries[index] = entry with { Phase = entry.Phase ?? previous.Phase };
        }

        private void CheckCharacters(int additional)
        {
            if ((long)_characters + additional > MaximumExportCharacters)
                throw new InvalidOperationException("This saved chat exceeds the 16 Mi-character clipboard history limit. Nothing was copied.");
        }

        private static void AppendExport(StringBuilder target, string value)
        {
            if ((long)target.Length + value.Length > MaximumExportCharacters)
                throw new InvalidOperationException("This saved chat exceeds the 16 Mi-character clipboard history limit. Nothing was copied.");
            target.Append(value);
        }
    }

    private static bool IsBlankHistoryLine(ReadOnlySpan<byte> line)
    {
        foreach (var value in line)
            if (value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r') return false;
        return true;
    }

    private static void ValidateHistoryRecord(ReadOnlySpan<byte> line, CancellationToken token)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A saved history record must be an object.");
        var count = 0;
        while (reader.Read())
            if ((++count & 255) == 0) token.ThrowIfCancellationRequested();
    }

    private static bool MatchesHistoryIdentity(ReadOnlySpan<byte> line, Guid expectedId)
    {
        var reader = new Utf8JsonReader(line);
        string? type = null;
        string? id = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var isType = reader.ValueTextEquals("type"u8);
            var isPayload = reader.ValueTextEquals("payload"u8);
            if (!reader.Read()) return false;
            if (isType && reader.TokenType == JsonTokenType.String) type = reader.GetString();
            else if (isPayload && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1))
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2) continue;
                    var isId = reader.ValueTextEquals("id"u8);
                    if (!reader.Read()) return false;
                    if (isId && reader.TokenType == JsonTokenType.String) id = reader.GetString();
                    else reader.Skip();
                }
            }
            else reader.Skip();
        }
        return type == "session_meta" && Guid.TryParseExact(id, "D", out var storedId) && storedId == expectedId;
    }

    private sealed record ChatHistoryExportEntry(string Role, string? TurnId, string Text, string? Phase);
    private sealed record ChatHistoryMessageKey(string Role, string? TurnId, string Kind, string MessageId);
    private sealed record ChatHistoryPair(int Index, string RawText, string Role, string? TurnId,
        string? MessageId, HashSet<string> Representations);
}
