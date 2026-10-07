using System.IO;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Services;

/// <summary>
/// Reads bounded current-turn tool arguments for chats whose saved workspace is
/// outside the configured projects. Only literal exec_command working folders
/// are retained, in memory. Never interpret prose, tool output, or shell commands.
/// </summary>
internal sealed class CodexActivityWorkingFolderReader(string codexHome)
{
    private const int MaximumTailBytes = 512 * 1024;
    private const int MaximumReadsPerPoll = 16;
    private const int MaximumScriptCharacters = 64 * 1024;
    private const int MaximumScriptTokens = 16 * 1024;
    private readonly string _codexHome = NormalizePath(codexHome);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private int _nextOffset;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Read(IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<string> requestedIds, CancellationToken token)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var requested = requestedIds.Distinct(StringComparer.Ordinal).ToArray();
        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _cache.Keys.Where(id => !requestedSet.Contains(id)).ToArray()) _cache.Remove(stale);
        if (requested.Length == 0) return result;
        var current = agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        foreach (var id in requested)
            if (current.TryGetValue(id, out var agent) && !string.IsNullOrWhiteSpace(agent.LatestTurnId) &&
                _cache.TryGetValue(id, out var cached) && cached.TurnId == agent.LatestTurnId)
                result[id] = cached.Folders;

        try
        {
            token.ThrowIfCancellationRequested();
            using var database = new NativeSqlite(Path.Combine(_codexHome, "state_5.sqlite"));
            var paths = database.Query("SELECT id, rollout_path FROM threads")
                .Where(row => row[0] is not null && requestedSet.Contains(row[0]!))
                .ToDictionary(row => row[0]!, row => row[1], StringComparer.Ordinal);
            var readCount = Math.Min(MaximumReadsPerPoll, requested.Length);
            var start = _nextOffset % requested.Length;
            for (var index = 0; index < readCount; index++)
            {
                token.ThrowIfCancellationRequested();
                var id = requested[(start + index) % requested.Length];
                if (!current.TryGetValue(id, out var agent) || string.IsNullOrWhiteSpace(agent.LatestTurnId)) continue;
                try
                {
                    if (!paths.TryGetValue(id, out var storedPath) || string.IsNullOrWhiteSpace(storedPath))
                    {
                        result.Remove(id);
                        _cache.Remove(id);
                        continue;
                    }
                    var path = NormalizePath(storedPath);
                    if (!IsSafeRollout(path))
                    {
                        result.Remove(id);
                        _cache.Remove(id);
                        continue;
                    }
                    var file = new FileInfo(path);
                    var length = file.Length;
                    var modified = file.LastWriteTimeUtc;
                    if (_cache.TryGetValue(id, out var cached) && cached.Path == path &&
                        cached.Length == length && cached.Modified == modified && cached.TurnId == agent.LatestTurnId)
                    {
                        result[id] = cached.Folders;
                        continue;
                    }
                    var observation = ReadTail(path, agent.LatestTurnId!, token);
                    file.Refresh();
                    if (file.Length != length || file.LastWriteTimeUtc != modified) continue;
                    var folders = observation.Folders;
                    // A growing bounded tail may lose the earlier same-turn call.
                    // A new turn or rewritten file must never inherit that evidence.
                    if (folders.Count == 0 && !observation.ConflictingTurn && cached is not null &&
                        cached.Path == path && cached.TurnId == agent.LatestTurnId && length > cached.Length)
                        folders = cached.Folders;
                    result[id] = folders;
                    _cache[id] = new(path, length, modified, agent.LatestTurnId!, folders);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    result.Remove(id);
                }
            }
            _nextOffset = (start + readCount) % requested.Length;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // Unavailable metadata is not evidence of project ownership.
            result.Clear();
        }
        return result;
    }

    private static TailObservation ReadTail(string path, string expectedTurnId, CancellationToken token)
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
            string? currentTurnId = null;
            string? latestFolder = null;
            long latestPosition = -1;
            var pending = new Dictionary<string, PendingCall>(StringComparer.Ordinal);
            for (var index = lineStart; index < count; index++)
            {
                if (bytes[index] != '\n') continue;
                token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(bytes.AsMemory(lineStart, index - lineStart));
                var root = document.RootElement;
                var type = String(root, "type");
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    lineStart = index + 1;
                    continue;
                }
                var payloadType = String(payload, "type");
                if (type == "turn_context" || type == "event_msg" &&
                    payloadType is "task_started" or "task_complete" or "turn_aborted")
                {
                    var turnId = String(payload, "turn_id");
                    if (turnId is not null && turnId != currentTurnId)
                    {
                        currentTurnId = turnId;
                        pending.Clear();
                        latestFolder = null;
                        latestPosition = -1;
                    }
                }
                else if (type == "response_item" && payloadType is "function_call" or "custom_tool_call")
                {
                    var turnId = currentTurnId;
                    if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var metadata))
                        turnId = String(metadata, "turn_id") ?? turnId;
                    if (turnId is not null && turnId != currentTurnId)
                    {
                        currentTurnId = turnId;
                        pending.Clear();
                        latestFolder = null;
                        latestPosition = -1;
                    }
                    var callId = String(payload, "call_id");
                    if (turnId == expectedTurnId && callId is { Length: > 0 and <= 256 } &&
                        ReadWorkingFolder(String(payload, "name"), payloadType == "custom_tool_call"
                            ? String(payload, "input") : String(payload, "arguments"), token) is { } folder)
                        pending[callId] = new(folder, offset + lineStart);
                }
                else if (type == "response_item" && payloadType is "function_call_output" or "custom_tool_call_output" &&
                         String(payload, "call_id") is { } completedId && pending.Remove(completedId, out var completed) &&
                         completed.Position > latestPosition)
                {
                    latestFolder = completed.Folder;
                    latestPosition = completed.Position;
                }
                lineStart = index + 1;
            }
            var conflicting = currentTurnId is not null && currentTurnId != expectedTurnId;
            return new(conflicting || latestFolder is null ? [] : [latestFolder], conflicting);
        }
        finally { Array.Clear(bytes); }
    }

    private static string? ReadWorkingFolder(string? toolName, string? arguments, CancellationToken token)
    {
        if (arguments is not { Length: > 0 and <= MaximumScriptCharacters }) return null;
        if (toolName is "exec_command" or "functions.exec_command")
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var properties = root.EnumerateObject().Where(property => property.Name == "workdir").ToArray();
            return properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String
                ? ReadFolder(properties[0].Value.GetString()) : null;
        }
        if (toolName is not ("exec" or "functions.exec")) return null;
        // The orchestration tool uses raw JavaScript. Never evaluate it. Accept
        // only ordinary string literals in direct, unconditional command calls.
        var tokens = Tokenize(arguments, token);
        if (tokens is null) return null;
        string? latestFolder = null;
        var braces = 0;
        var parentheses = 0;
        var brackets = 0;
        var statementStart = 0;
        for (var index = 0; index + 4 < tokens.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (tokens[index].IsString) continue;
            if (tokens[index].Text == ";" && braces == 0 && parentheses == 0 && brackets == 0) statementStart = index + 1;
            if (tokens[index].Text == "{") braces++;
            else if (tokens[index].Text == "}") braces--;
            else if (tokens[index].Text == "(") parentheses++;
            else if (tokens[index].Text == ")") parentheses--;
            else if (tokens[index].Text == "[") brackets++;
            else if (tokens[index].Text == "]") brackets--;
            if (braces != 0 || tokens[index].Text != "tools" || tokens[index].IsString ||
                index > 0 && !tokens[index - 1].IsString && tokens[index - 1].Text == "." ||
                tokens[index + 1].Text != "." || tokens[index + 2].Text != "exec_command" ||
                tokens[index + 3].Text != "(" || tokens[index + 4].Text != "{") continue;
            var prefix = tokens.GetRange(statementStart, index - statementStart);
            if (!prefix.Any(item => !item.IsString && item.Text == "await") || prefix.Any(item => !item.IsString &&
                item.Text is "if" or "for" or "while" or "switch" or "function" or "class" or "return" or
                    "throw" or "try" or "catch" or "finally" or "?" or "&&" or "||" or "=>")) continue;
            if (ReadLiteralCommandFolder(tokens, index + 4, out var folder, out var end))
            {
                latestFolder = folder;
                parentheses++; // The skipped literal includes this call's opening parenthesis.
                index = end;
            }
        }
        return latestFolder;
    }

    private static bool ReadLiteralCommandFolder(List<ScriptToken> tokens, int start, out string? folder, out int end)
    {
        folder = null;
        end = start;
        var index = start + 1;
        var found = false;
        while (index < tokens.Count)
        {
            if (!tokens[index].IsString && tokens[index].Text == "}")
            {
                end = index;
                return found && folder is not null && index + 1 < tokens.Count && tokens[index + 1].Text == ")";
            }
            var key = tokens[index++];
            if (key.Text == "..." || key.Text == "[" || index >= tokens.Count || tokens[index++].Text != ":") return false;
            if (index >= tokens.Count) return false;
            if (key.Text == "workdir")
            {
                if (found || !tokens[index].IsString) return false;
                found = true;
                folder = ReadFolder(tokens[index++].Text);
            }
            else
            {
                // Other properties may be expressions. Balance their delimiters
                // without treating nested properties or string content as workdir.
                var depth = 0;
                for (; index < tokens.Count; index++)
                {
                    var value = tokens[index];
                    if (value.IsString) continue;
                    if (depth == 0 && value.Text is "," or "}") break;
                    if (value.Text is "(" or "[" or "{") depth++;
                    else if (value.Text is ")" or "]" or "}")
                    {
                        if (--depth < 0) return false;
                    }
                }
                if (depth != 0) return false;
            }
            if (index >= tokens.Count) return false;
            if (tokens[index].Text == ",") index++;
            else if (tokens[index].Text != "}") return false;
        }
        return false;
    }

    private static List<ScriptToken>? Tokenize(string script, CancellationToken token)
    {
        var tokens = new List<ScriptToken>();
        for (var index = 0; index < script.Length;)
        {
            token.ThrowIfCancellationRequested();
            var character = script[index];
            if (char.IsWhiteSpace(character)) { index++; continue; }
            if (character == '/' && index + 1 < script.Length && script[index + 1] == '/')
            {
                index += 2;
                while (index < script.Length && script[index] != '\n') index++;
                continue;
            }
            if (character == '/' && index + 1 < script.Length && script[index + 1] == '*')
            {
                var close = script.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0) return null;
                index = close + 2;
                continue;
            }
            // Template interpolation and regular expressions require a full JS
            // parser. Omit these scripts rather than guess at their call content.
            if (character is '`' or '/') return null;
            if (character is '\'' or '"')
            {
                if (!ReadStringLiteral(script, ref index, out var value)) return null;
                tokens.Add(new(value!, true));
            }
            else if (char.IsAsciiLetterOrDigit(character) || character is '_' or '$')
            {
                var start = index++;
                while (index < script.Length && (char.IsAsciiLetterOrDigit(script[index]) || script[index] is '_' or '$')) index++;
                tokens.Add(new(script[start..index], false));
            }
            else
            {
                var width = index + 2 < script.Length && script.AsSpan(index, 3).SequenceEqual("...".AsSpan()) ? 3 :
                    index + 1 < script.Length && (script.AsSpan(index, 2).SequenceEqual("&&".AsSpan()) ||
                    script.AsSpan(index, 2).SequenceEqual("||".AsSpan()) || script.AsSpan(index, 2).SequenceEqual("=>".AsSpan())) ? 2 : 1;
                tokens.Add(new(script.Substring(index, width), false));
                index += width;
            }
            if (tokens.Count > MaximumScriptTokens) return null;
        }
        return tokens;
    }

    private static bool ReadStringLiteral(string script, ref int index, out string? value)
    {
        value = null;
        var quote = script[index++];
        var builder = new StringBuilder();
        while (index < script.Length)
        {
            var character = script[index++];
            if (character == quote) { value = builder.ToString(); return true; }
            if (character is '\r' or '\n') return false;
            if (character != '\\') { builder.Append(character); continue; }
            if (index >= script.Length) return false;
            var escape = script[index++];
            switch (escape)
            {
                case '\\': case '\'': case '"': case '/': builder.Append(escape); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'v': builder.Append('\v'); break;
                case '\r': if (index < script.Length && script[index] == '\n') index++; break;
                case '\n': break;
                case 'x': case 'u':
                    var digits = escape == 'x' ? 2 : 4;
                    if (index + digits > script.Length || !ushort.TryParse(script.AsSpan(index, digits),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)) return false;
                    builder.Append((char)code);
                    index += digits;
                    break;
                default: return false;
            }
        }
        return false;
    }

    private static string? ReadFolder(string? value)
    {
        if (value is not { Length: > 0 and <= 32768 } || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value)) return null;
        try { return NormalizePath(value); }
        catch (Exception ex) when (IsReadFailure(ex)) { return null; }
    }

    private bool IsSafeRollout(string path)
    {
        if (!path.StartsWith(_codexHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(_codexHome, path);
        if (!(relative.StartsWith("sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
              relative.StartsWith("archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
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

    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsReadFailure(Exception ex) => ex is IOException or InvalidOperationException or
        UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or
        JsonException or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception;

    private sealed record ScriptToken(string Text, bool IsString);
    private sealed record PendingCall(string Folder, long Position);
    private sealed record TailObservation(IReadOnlyList<string> Folders, bool ConflictingTurn);
    private sealed record CacheEntry(string Path, long Length, DateTime Modified, string TurnId, IReadOnlyList<string> Folders);
}
