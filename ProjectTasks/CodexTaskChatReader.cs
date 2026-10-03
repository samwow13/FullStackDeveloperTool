using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

internal sealed record CodexTaskChatEntry(string Role, string Text, string? Phase = null);

internal sealed record CodexTaskChatResult(IReadOnlyList<CodexTaskChatEntry> Entries, string Status);

/// <summary>Reads a queue turn and later turns without resuming or changing its task.</summary>
internal static class CodexTaskChatReader
{
    private const int PageSize = 25;
    private const int MaximumPages = 20;
    private const int MaximumItems = 2048;
    private const int MaximumEntries = 200;
    private const int MaximumEntryCharacters = 100_000;
    private const int MaximumDisplayedCharacters = 500_000;

    public static async Task<CodexTaskChatResult> ReadAsync(ProjectTaskExecutionReceipt receipt,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(receipt.ThreadId) || string.IsNullOrWhiteSpace(receipt.TurnId))
            return Empty("Codex task and turn IDs have not been recorded yet. Refresh after submission starts.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (string.IsNullOrWhiteSpace(receipt.Snapshot.Folder) ||
                !Path.IsPathFullyQualified(receipt.Snapshot.Folder) || !Directory.Exists(receipt.Snapshot.Folder))
                return Empty("The task's saved working folder is unavailable. Codex chat cannot be read.");
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipt.Snapshot.Folder));
            await using var connection = await CodexAppServerConnection.StartAsync(folder, timeout.Token,
                experimentalApi: true).ConfigureAwait(false);

            var response = await connection.RequestAsync("thread/read", new
            {
                threadId = receipt.ThreadId,
                includeTurns = false
            }, timeout.Token).ConfigureAwait(false);
            if (!TryObject(response, "thread", out var thread) ||
                !string.Equals(OptionalString(thread, "id"), receipt.ThreadId, StringComparison.Ordinal))
                return Empty("Codex task identity differs from this saved attempt.");

            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var seenTurnIds = new HashSet<string>(StringComparer.Ordinal);
            var newerTurns = new List<JsonElement>();
            string? cursor = null;
            for (var page = 0; page < MaximumPages; page++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var turns = await connection.RequestAsync("thread/turns/list", new
                {
                    threadId = receipt.ThreadId,
                    limit = PageSize,
                    sortDirection = "desc",
                    itemsView = "full",
                    cursor
                }, timeout.Token).ConfigureAwait(false);
                if (!turns.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() > PageSize)
                    return Empty(CodexAppServerConnection.IncompatibleMessage);

                foreach (var turn in data.EnumerateArray())
                {
                    var id = OptionalString(turn, "id");
                    if (string.IsNullOrWhiteSpace(id) || !seenTurnIds.Add(id))
                        return Empty(CodexAppServerConnection.IncompatibleMessage);
                    newerTurns.Add(turn.Clone());
                    if (!string.Equals(id, receipt.TurnId, StringComparison.Ordinal)) continue;
                    newerTurns.Reverse();
                    return ReadTurns(newerTurns);
                }

                cursor = OptionalString(turns, "nextCursor");
                if (cursor is null)
                    return Empty("The exact Codex turn is not in stored history yet. Refresh later.");
                if (!seenCursors.Add(cursor)) return Empty(CodexAppServerConnection.IncompatibleMessage);
            }
            return Empty("The exact Codex turn was not found within 500 stored turns.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Empty("Reading Codex chat timed out. Refresh to try again.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return Empty("Codex chat is unavailable. Refresh to try again.");
        }
    }

    private static CodexTaskChatResult ReadTurns(IReadOnlyList<JsonElement> turns)
    {
        var entries = new List<CodexTaskChatEntry>();
        var characterCount = 0;
        var omitted = false;
        var missingTurnItems = false;
        var running = false;
        foreach (var turn in turns)
        {
            if (OptionalString(turn, "status") == "inProgress") running = true;
            if (turn.TryGetProperty("itemsView", out var view) &&
                (view.ValueKind != JsonValueKind.String || view.GetString() != "full"))
            {
                missingTurnItems = true;
                continue;
            }
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() > MaximumItems)
            {
                missingTurnItems = true;
                continue;
            }
            foreach (var item in items.EnumerateArray())
            {
                var type = OptionalString(item, "type");
                CodexTaskChatEntry? entry = type switch
                {
                    "userMessage" => UserEntry(item),
                    "agentMessage" => AgentEntry(item),
                    _ => null
                };
                if (entry is null || entry.Text.Length == 0) continue;
                if (entry.Text.Length > MaximumEntryCharacters)
                {
                    entry = entry with
                    {
                        Text = entry.Text[..MaximumEntryCharacters] + "\n[Message truncated in this view]"
                    };
                    omitted = true;
                }
                entries.Add(entry);
                characterCount += entry.Text.Length;
                while (entries.Count > MaximumEntries || characterCount > MaximumDisplayedCharacters)
                {
                    characterCount -= entries[0].Text.Length;
                    entries.RemoveAt(0);
                    omitted = true;
                }
            }
        }

        var status = omitted
            ? "Some earlier messages or portions of long messages are omitted from this view."
            : entries.Count == 0
                ? "No user or assistant messages are stored for this conversation yet."
                : "Showing saved user and assistant messages from this Codex conversation.";
        if (missingTurnItems) status += " Some turns lack full stored item history.";
        if (running) status += " Stored chat may lack current streamed text; refresh for updates.";
        return new(entries, status);
    }

    private static CodexTaskChatEntry? UserEntry(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            var type = OptionalString(part, "type");
            var value = type switch
            {
                "text" => OptionalString(part, "text"),
                "image" or "localImage" => "[Image attached]",
                _ => null
            };
            if (string.IsNullOrEmpty(value)) continue;
            if (text.Length > 0) text.AppendLine().AppendLine();
            text.Append(value);
        }
        return text.Length == 0 ? null : new("User", text.ToString());
    }

    private static CodexTaskChatEntry? AgentEntry(JsonElement item)
    {
        var text = OptionalString(item, "text");
        if (string.IsNullOrEmpty(text)) return null;
        var phase = OptionalString(item, "phase");
        return new("Assistant", text, phase);
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static CodexTaskChatResult Empty(string status) => new([], status);
}
