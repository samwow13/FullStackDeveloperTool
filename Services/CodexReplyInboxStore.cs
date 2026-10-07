using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Durable Launcher-owned replies, scoped to an exact settings library and chat UUID.
/// This inbox does not submit Codex prompts, steer turns, or start queued work.
/// Agent registration and authorization remain the responsibility of the service bridge.
/// </summary>
public sealed class CodexReplyInboxStore
{
    public const int MaximumMessageLength = 24000;
    public const int MaximumReadEntries = 8;
    private const int MaximumReadCharacters = 190000;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumReceipts = 10000;
    private const int MaximumOutstanding = 256;
    private const int MaximumOutstandingPerChat = 100;
    private const int MaximumRecentAcknowledgments = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _scope;

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "codex-reply-inbox.json");

    public string StorePath { get; }

    /// <param name="settingsPath">The active SettingsStore.SettingsPath; null resolves the current settings library.</param>
    public CodexReplyInboxStore(string? settingsPath = null)
    {
        var settings = NormalizePath(settingsPath ?? new SettingsStore().SettingsPath);
        StorePath = string.Equals(settings, NormalizePath(SettingsStore.DefaultSettingsPath), StringComparison.OrdinalIgnoreCase)
            ? DefaultStorePath : settings + ".codex-reply-inbox.json";
        _scope = Hash(settings.ToUpperInvariant());
    }

    public Task<CodexReplyInboxReceipt> SaveAsync(string chatId, string replyId, string text,
        CancellationToken token = default)
    {
        var chat = CanonicalId(chatId, "chatId");
        var reply = CanonicalId(replyId, "replyId");
        ValidateText(text);
        var payloadHash = PayloadHash(chat, text);
        return AccessAsync(true, document =>
        {
            var existing = document.Entries.SingleOrDefault(entry => entry.ReplyId == reply);
            if (existing is not null)
            {
                if (existing.ChatId != chat || existing.TextHash != payloadHash)
                    throw new CodexReplyInboxConflictException("This replyId already identifies a different reply. Retry the original reply unchanged, or use a new replyId for a separate reply.");
                // Acknowledged tombstones remain authoritative and never recreate reply bodies.
                return (Receipt(existing), false);
            }
            if (document.Entries.Count >= MaximumReceipts)
                throw new CodexReplyInboxConflictException("Reply inbox receipt capacity reached. Existing replies and acknowledgment history were preserved; no new reply was saved.");
            var outstanding = document.Entries.Where(entry => entry.State != CodexReplyInboxState.Acknowledged).ToArray();
            if (outstanding.Length >= MaximumOutstanding || outstanding.Count(entry => entry.ChatId == chat) >= MaximumOutstandingPerChat)
                throw new CodexReplyInboxConflictException("Reply inbox has too many unacknowledged replies. Let agents read and acknowledge existing replies before saving more.");
            var entry = new StoredEntry
            {
                ChatId = chat, ReplyId = reply, Text = text, TextHash = payloadHash,
                CreatedAt = DateTimeOffset.UtcNow, State = CodexReplyInboxState.Pending
            };
            document.Entries.Add(entry);
            return (Receipt(entry), true);
        }, token);
    }

    public Task<IReadOnlyList<CodexReplyInboxEntry>> ReadAsync(string chatId, CancellationToken token = default)
    {
        var chat = CanonicalId(chatId, "chatId");
        return AccessAsync<IReadOnlyList<CodexReplyInboxEntry>>(true, document =>
        {
            var page = new List<CodexReplyInboxEntry>();
            var selected = new List<StoredEntry>();
            var now = DateTimeOffset.UtcNow;
            var textCharacters = 0;
            foreach (var entry in document.Entries.Where(entry => entry.ChatId == chat &&
                         entry.State != CodexReplyInboxState.Acknowledged).OrderBy(entry => entry.CreatedAt)
                         .ThenBy(entry => entry.ReplyId, StringComparer.Ordinal).Take(MaximumReadEntries))
            {
                if (textCharacters + entry.Text!.Length > MaximumMessageLength) break;
                var readAt = entry.ReadAt ?? Later(now, entry.CreatedAt);
                var view = new CodexReplyInboxEntry(entry.ReplyId, chat, entry.Text!, entry.CreatedAt,
                    CodexReplyInboxState.Read, readAt, null);
                page.Add(view);
                // Match safe JSON escaping used by the bridge. Only returned entries may change state.
                if (JsonSerializer.Serialize(page, Json).Length > MaximumReadCharacters)
                {
                    page.RemoveAt(page.Count - 1);
                    break;
                }
                selected.Add(entry);
                textCharacters += entry.Text.Length;
            }
            var changed = false;
            foreach (var entry in selected)
            {
                if (entry.State == CodexReplyInboxState.Read) continue;
                entry.State = CodexReplyInboxState.Read;
                entry.ReadAt = Later(now, entry.CreatedAt);
                changed = true;
            }
            // Read replies remain retrievable until explicit acknowledgment, including after response loss.
            return (page.ToArray(), changed);
        }, token);
    }

    public Task<CodexReplyInboxStatus> AcknowledgeAsync(string chatId, IReadOnlyList<string> replyIds,
        CancellationToken token = default)
    {
        var chat = CanonicalId(chatId, "chatId");
        if (replyIds is null || replyIds.Count is < 1 or > MaximumOutstandingPerChat)
            throw new CodexReplyInboxConflictException("Acknowledge between 1 and 100 reply IDs returned by the inbox read.");
        var replies = replyIds.Select(id => CanonicalId(id, "replyId")).ToArray();
        if (replies.Distinct(StringComparer.Ordinal).Count() != replies.Length)
            throw new CodexReplyInboxConflictException("Each acknowledgment replyId must appear only once.");
        return AccessAsync(true, document =>
        {
            var selected = replies.Select(reply => document.Entries.SingleOrDefault(entry => entry.ReplyId == reply && entry.ChatId == chat))
                .ToArray();
            if (selected.Any(entry => entry is null || entry.State == CodexReplyInboxState.Pending))
                throw new CodexReplyInboxConflictException("Acknowledge only replies already read from this exact chat inbox. No acknowledgments were changed.");
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var entry in selected)
            {
                if (entry!.State == CodexReplyInboxState.Acknowledged) continue;
                entry.State = CodexReplyInboxState.Acknowledged;
                entry.AcknowledgedAt = Later(now, entry.ReadAt!.Value);
                entry.Text = null;
                changed = true;
            }
            return (Status(document, chat), changed);
        }, token);
    }

    public Task<CodexReplyInboxStatus> GetStatusAsync(string chatId, CancellationToken token = default)
    {
        var chat = CanonicalId(chatId, "chatId");
        return AccessAsync(false, document => (Status(document, chat), false), token);
    }

    private Task<T> AccessAsync<T>(bool write, Func<StoreDocument, (T Result, bool Changed)> action, CancellationToken token) =>
        Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (write) Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                await using var writerLock = write ? await AcquireWriterLockAsync(token).ConfigureAwait(false) : null;
                var original = await ReadBytesAsync(token).ConfigureAwait(false);
                var document = Parse(original);
                var (result, changed) = action(document);
                if (changed)
                {
                    if (!write) throw new InvalidDataException();
                    Validate(document);
                    await SaveDocumentAsync(document, original, token).ConfigureAwait(false);
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (CodexReplyInboxConflictException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // Reply bodies and raw filesystem/configuration diagnostics never enter errors or service logs.
                throw new InvalidOperationException("Codex reply inbox could not be read or updated. Existing data was preserved. Restore a valid supported inbox or resolve file access, then retry the same replyId.");
            }
        }, token);

    private async Task<FileStream> AcquireWriterLockAsync(CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline)
            { await Task.Delay(75, token).ConfigureAwait(false); }
            catch (IOException)
            { throw new CodexReplyInboxConflictException("Another Launcher is updating reply inbox. Wait briefly, then retry the same replyId."); }
        }
    }

    private async Task<byte[]?> ReadBytesAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException();
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaximumFileBytes) throw new InvalidDataException();
                content.Write(buffer, 0, read);
            }
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private StoreDocument Parse(byte[]? bytes)
    {
        if (bytes is null) return new() { Scope = _scope };
        var document = JsonSerializer.Deserialize<StoreDocument>(bytes, Json) ?? throw new InvalidDataException();
        Validate(document);
        return document;
    }

    private void Validate(StoreDocument document)
    {
        if (document.Version != 1 || document.Scope != _scope || document.Entries is null ||
            document.Entries.Count > MaximumReceipts) throw new InvalidDataException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var outstanding = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalOutstanding = 0;
        foreach (var entry in document.Entries)
        {
            if (entry is null || CanonicalId(entry.ChatId, "chatId") != entry.ChatId ||
                CanonicalId(entry.ReplyId, "replyId") != entry.ReplyId || !ids.Add(entry.ReplyId) ||
                entry.CreatedAt == default || entry.CreatedAt.Offset != TimeSpan.Zero ||
                entry.TextHash is not { Length: 64 } || !entry.TextHash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F') ||
                !Enum.IsDefined(entry.State)) throw new InvalidDataException();
            if (entry.ReadAt is { } read && (read < entry.CreatedAt || read.Offset != TimeSpan.Zero) ||
                entry.AcknowledgedAt is { } acknowledged && (entry.ReadAt is null || acknowledged < entry.ReadAt || acknowledged.Offset != TimeSpan.Zero))
                throw new InvalidDataException();
            switch (entry.State)
            {
                case CodexReplyInboxState.Pending when entry.ReadAt is null && entry.AcknowledgedAt is null:
                case CodexReplyInboxState.Read when entry.ReadAt is not null && entry.AcknowledgedAt is null:
                    ValidateText(entry.Text!);
                    if (PayloadHash(entry.ChatId, entry.Text!) != entry.TextHash) throw new InvalidDataException();
                    totalOutstanding++;
                    outstanding[entry.ChatId] = outstanding.GetValueOrDefault(entry.ChatId) + 1;
                    break;
                case CodexReplyInboxState.Acknowledged when entry.ReadAt is not null && entry.AcknowledgedAt is not null && entry.Text is null:
                    break;
                default: throw new InvalidDataException();
            }
        }
        if (totalOutstanding > MaximumOutstanding || outstanding.Values.Any(count => count > MaximumOutstandingPerChat))
            throw new InvalidDataException();
    }

    private async Task SaveDocumentAsync(StoreDocument document, byte[]? original, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumFileBytes)
            throw new CodexReplyInboxConflictException("Reply inbox storage limit reached. Existing replies were preserved; no change was saved.");
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var latest = await ReadBytesAsync(token).ConfigureAwait(false);
            if ((original is null) != (latest is null) || original is not null && latest is not null &&
                !SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(latest)))
                throw new CodexReplyInboxConflictException("Reply inbox changed in another editor. No change was saved. Retry the same replyId after refreshing.");
            token.ThrowIfCancellationRequested();
            // Atomic replacement is the commit point. No backup retains bodies erased by acknowledgment.
            if (original is null) File.Move(temporary, StorePath, overwrite: false);
            else File.Replace(temporary, StorePath, null);
            // Do not check cancellation after the commit point or report a durable save as canceled.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static CodexReplyInboxStatus Status(StoreDocument document, string chatId)
    {
        var entries = document.Entries.Where(entry => entry.ChatId == chatId).ToArray();
        var acknowledged = entries.Where(entry => entry.State == CodexReplyInboxState.Acknowledged)
            .OrderByDescending(entry => entry.AcknowledgedAt).ThenBy(entry => entry.ReplyId, StringComparer.Ordinal).ToArray();
        var receipts = entries.Where(entry => entry.State != CodexReplyInboxState.Acknowledged)
            .Concat(acknowledged.Take(MaximumRecentAcknowledgments)).OrderByDescending(entry => entry.CreatedAt)
            .ThenBy(entry => entry.ReplyId, StringComparer.Ordinal).Select(Receipt).ToArray();
        return new(chatId, entries.Count(entry => entry.State == CodexReplyInboxState.Pending),
            entries.Count(entry => entry.State == CodexReplyInboxState.Read), acknowledged.Length,
            receipts, acknowledged.Length > MaximumRecentAcknowledgments);
    }

    private static CodexReplyInboxReceipt Receipt(StoredEntry entry) =>
        new(entry.ReplyId, entry.ChatId, entry.CreatedAt, entry.State, entry.ReadAt, entry.AcknowledgedAt);

    private static string CanonicalId(string value, string label) =>
        Guid.TryParseExact(value, "D", out var parsed) ? parsed.ToString("D")
            : throw new CodexReplyInboxConflictException($"{label} must be an exact UUID; chat titles cannot identify an inbox.");

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumMessageLength)
            throw new ArgumentException("Reply must contain between 1 and 24,000 characters, including a non-whitespace character.");
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                throw new ArgumentException("Reply contains unsupported control characters. Remove them before saving.");
            if (char.IsHighSurrogate(character))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i]))
                    throw new ArgumentException("Reply contains invalid Unicode text. Replace the invalid character before saving.");
            }
            else if (char.IsLowSurrogate(character))
                throw new ArgumentException("Reply contains invalid Unicode text. Replace the invalid character before saving.");
        }
    }

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset previous) => now < previous ? previous : now;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string PayloadHash(string chatId, string text) => Hash(chatId + "\n" + text);
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Active settings path is required.");
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private sealed class StoreDocument
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string Scope { get; set; } = "";
        [JsonRequired] public List<StoredEntry> Entries { get; set; } = [];
    }

    private sealed class StoredEntry
    {
        [JsonRequired] public string ReplyId { get; set; } = "";
        [JsonRequired] public string ChatId { get; set; } = "";
        [JsonRequired] public string? Text { get; set; }
        [JsonRequired] public string TextHash { get; set; } = "";
        [JsonRequired] public DateTimeOffset CreatedAt { get; set; }
        [JsonRequired] public CodexReplyInboxState State { get; set; }
        [JsonRequired] public DateTimeOffset? ReadAt { get; set; }
        [JsonRequired] public DateTimeOffset? AcknowledgedAt { get; set; }
    }
}

public sealed class CodexReplyInboxConflictException(string message) : InvalidOperationException(message);
