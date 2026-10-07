using System.IO;
using System.Text.Json;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Services;

/// <summary>
/// Reads only identity fields from Launcher-owned Start Agent and task receipts.
/// The cache retains no prompts, titles, context, tokens or responses. Call on a
/// background worker; unknown versions and unreadable snapshots fail closed.
/// </summary>
public sealed class CodexActivityProjectReceiptReader
{
    private const int MaximumChatReceiptBytes = 1024 * 1024;
    private const int MaximumTaskFileBytes = 64 * 1024 * 1024;
    private const int MaximumChatFiles = 4096;
    private const int MaximumCombinedChatBytes = 32 * 1024 * 1024;
    private const int MaximumTaskReceipts = 10_000;
    private readonly string _chatDirectory;
    private readonly string _taskPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CodexActivityProjectReceiptReader(string? settingsPath = null)
    {
        var currentSettings = Path.GetFullPath(new SettingsStore().SettingsPath);
        var settings = Path.GetFullPath(settingsPath ?? currentSettings);
        if (string.Equals(settings, currentSettings, StringComparison.OrdinalIgnoreCase))
        {
            // These stores distinguish an explicit --settings override, including
            // an override that names the default settings filename.
            _chatDirectory = CodexAgentStorage.ResolvePath("codex-agent-chats");
            _taskPath = new ProjectTaskStore().StorePath;
        }
        else if (string.Equals(settings, Path.GetFullPath(SettingsStore.DefaultSettingsPath), StringComparison.OrdinalIgnoreCase))
        {
            _chatDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FullStackLauncher", "codex-agent-chats");
            _taskPath = ProjectTaskStore.DefaultStorePath;
        }
        else
        {
            _chatDirectory = settings + ".codex-agent-chats";
            _taskPath = new ProjectTaskStore(settings).StorePath;
        }
    }

    public IReadOnlyDictionary<string, string> Read(CancellationToken token = default)
    {
        lock (_gate)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var retainedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _taskPath };
                var identities = new List<ReceiptIdentity>();
                FileInfo[] files;
                try { files = new DirectoryInfo(_chatDirectory).EnumerateFiles("*.json").Take(MaximumChatFiles + 1).ToArray(); }
                catch (DirectoryNotFoundException) { files = []; }
                if (files.Length > MaximumChatFiles) throw new InvalidDataException();
                long totalBytes = 0;
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var attemptId = Path.GetFileNameWithoutExtension(file.Name);
                    if (!Guid.TryParseExact(attemptId, "N", out _)) continue;
                    totalBytes += file.Length;
                    if (totalBytes > MaximumCombinedChatBytes) throw new InvalidDataException();
                    retainedPaths.Add(file.FullName);
                    identities.AddRange(ReadCached(file.FullName, MaximumChatReceiptBytes,
                        root => ReadChatReceipt(root, attemptId), token));
                }
                identities.AddRange(ReadCached(_taskPath, MaximumTaskFileBytes, ReadTaskReceipts, token));
                foreach (var obsolete in _cache.Keys.Where(path => !retainedPaths.Contains(path)).ToArray()) _cache.Remove(obsolete);

                var newest = new Dictionary<string, ReceiptIdentity>(StringComparer.Ordinal);
                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                foreach (var identity in identities)
                {
                    if (!newest.TryGetValue(identity.ChatId, out var prior) || identity.UpdatedAt > prior.UpdatedAt)
                    {
                        newest[identity.ChatId] = identity;
                        ambiguous.Remove(identity.ChatId);
                    }
                    else if (identity.UpdatedAt == prior.UpdatedAt && identity.ProjectId != prior.ProjectId)
                        ambiguous.Add(identity.ChatId);
                }
                return newest.Where(pair => !ambiguous.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ProjectId, StringComparer.Ordinal);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // Do not reuse stale ownership when a receipt source becomes unavailable.
                _cache.Clear();
                throw new InvalidOperationException("Saved Launcher chat ownership could not be read. Existing receipts were preserved; ownership from these receipts is unavailable.");
            }
        }
    }

    private IReadOnlyList<ReceiptIdentity> ReadCached(string path, int maximumBytes,
        Func<JsonElement, ReceiptIdentity[]> parse, CancellationToken token)
    {
        FileInfo file;
        try
        {
            _ = File.GetAttributes(path);
            file = new FileInfo(path);
            if (file.Length is <= 0 || file.Length > maximumBytes) throw new InvalidDataException();
        }
        catch (FileNotFoundException) { _cache.Remove(path); return []; }
        catch (DirectoryNotFoundException) { _cache.Remove(path); return []; }
        var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
        if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Identities;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (content.Length + count > maximumBytes) throw new InvalidDataException();
            content.Write(buffer, 0, count);
        }
        var bytes = content.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        var identities = parse(document.RootElement);
        _cache[path] = new(stamp, identities);
        return identities;
    }

    private static ReceiptIdentity[] ReadChatReceipt(JsonElement root, string attemptId)
    {
        RequireUniqueProperties(root);
        if (Required(root, "version", JsonValueKind.Number).GetInt32() != 1 ||
            Required(root, "attemptId", JsonValueKind.String).GetString() != attemptId) throw new InvalidDataException();
        var request = Required(root, "request", JsonValueKind.Object);
        RequireUniqueProperties(request);
        var projectId = ProjectId(request);
        var updatedAt = UpdatedAt(root);
        var chatId = ChatId(root);
        return chatId is null ? [] : [new(chatId, projectId, updatedAt)];
    }

    private static ReceiptIdentity[] ReadTaskReceipts(JsonElement root)
    {
        RequireUniqueProperties(root);
        var version = Required(root, "version", JsonValueKind.Number).GetInt32();
        if (version is < 1 or > 9) throw new InvalidDataException();
        var receipts = Required(root, "receipts", JsonValueKind.Array);
        if (receipts.GetArrayLength() > MaximumTaskReceipts) throw new InvalidDataException();
        var identities = new List<ReceiptIdentity>();
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts.EnumerateArray())
        {
            RequireUniqueProperties(receipt);
            var attemptId = Required(receipt, "attemptId", JsonValueKind.String).GetString();
            if (!Guid.TryParseExact(attemptId, "N", out _) || !attempts.Add(attemptId!)) throw new InvalidDataException();
            var snapshot = Required(receipt, "snapshot", JsonValueKind.Object);
            RequireUniqueProperties(snapshot);
            var projectId = ProjectId(snapshot);
            var updatedAt = UpdatedAt(receipt);
            var chatId = ChatId(receipt);
            if (chatId is not null) identities.Add(new(chatId, projectId, updatedAt));
        }
        return identities.ToArray();
    }

    private static string ProjectId(JsonElement element)
    {
        var id = Required(element, "projectId", JsonValueKind.String).GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) || id != id.Trim())
            throw new InvalidDataException();
        return id;
    }

    private static DateTimeOffset UpdatedAt(JsonElement element)
    {
        if (!Required(element, "createdAt", JsonValueKind.String).TryGetDateTimeOffset(out var created) ||
            !Required(element, "updatedAt", JsonValueKind.String).TryGetDateTimeOffset(out var updated) ||
            created == default || updated < created || created.Offset != TimeSpan.Zero || updated.Offset != TimeSpan.Zero)
            throw new InvalidDataException();
        return updated;
    }

    private static string? ChatId(JsonElement element)
    {
        if (!element.TryGetProperty("threadId", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "D", out var parsed))
            throw new InvalidDataException();
        return parsed.ToString("D");
    }

    private static JsonElement Required(JsonElement element, string name, JsonValueKind kind) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value : throw new InvalidDataException();

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException();
    }

    private sealed record ReceiptIdentity(string ChatId, string ProjectId, DateTimeOffset UpdatedAt);
    private sealed record FileStamp(long Length, long LastWriteTicks, long CreationTicks);
    private sealed record CachedFile(FileStamp Stamp, IReadOnlyList<ReceiptIdentity> Identities);
}
