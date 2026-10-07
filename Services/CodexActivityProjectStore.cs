using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.Services;

/// <summary>
/// Durable Launcher-owned chat ownership, scoped to one settings library. Only
/// exact chat IDs, project IDs and update times are saved; sessions and reply
/// authorization remain separate. This store never writes Codex data.
/// </summary>
public sealed class CodexActivityProjectStore
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumAssociations = 10_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _scope;

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "codex-project-associations.json");

    public string StorePath { get; }

    public CodexActivityProjectStore(string? settingsPath = null)
    {
        var settings = NormalizePath(settingsPath ?? new SettingsStore().SettingsPath);
        StorePath = string.Equals(settings, NormalizePath(SettingsStore.DefaultSettingsPath), StringComparison.OrdinalIgnoreCase)
            ? DefaultStorePath : settings + ".codex-project-associations.json";
        _scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ToUpperInvariant())));
        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = NormalizePath(!string.IsNullOrWhiteSpace(configuredCodexHome) ? configuredCodexHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        if (string.Equals(StorePath, codexHome, StringComparison.OrdinalIgnoreCase) ||
            StorePath.StartsWith(codexHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex project associations must be stored outside Codex's data folder.");
    }

    /// <summary>Read a complete validated snapshot on a background worker.</summary>
    public IReadOnlyDictionary<string, string> Read(CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return Parse(ReadBytes(token)).Associations.ToDictionary(entry => entry.ChatId, entry => entry.ProjectId,
                StringComparer.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Saved Codex project associations could not be read. Existing data was preserved; restore a valid supported file or resolve file access.");
        }
    }

    /// <summary>Save the newest explicit project assignment for this exact chat.</summary>
    public void Save(string chatId, string projectId, CancellationToken token = default)
    {
        chatId = CanonicalChatId(chatId);
        ValidateProjectId(projectId);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            using var writerLock = AcquireWriterLock(token);
            var original = ReadBytes(token);
            var document = Parse(original);
            var existing = document.Associations.FindIndex(entry => entry.ChatId == chatId);
            if (existing < 0 && document.Associations.Count >= MaximumAssociations)
                throw new InvalidOperationException("Codex project association capacity reached. Existing assignments were preserved.");
            var now = DateTimeOffset.UtcNow;
            if (existing >= 0 && document.Associations[existing].UpdatedAt > now)
                now = document.Associations[existing].UpdatedAt;
            var association = new StoredAssociation { ChatId = chatId, ProjectId = projectId, UpdatedAt = now };
            if (existing >= 0) document.Associations[existing] = association;
            else document.Associations.Add(association);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException();
            temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            var latest = ReadBytes(token);
            if (original is null ? latest is not null : latest is null || !original.AsSpan().SequenceEqual(latest))
                throw new InvalidOperationException("Codex project associations changed in another editor. Existing assignments were preserved; retry the same chat and project.");
            if (original is null) File.Move(temporary, StorePath, overwrite: false);
            else File.Replace(temporary, StorePath, StorePath + ".bak");
            temporary = null;
            // Atomic replacement is the commit point. Cancellation afterward cannot undo ownership.
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Codex project association could not be saved. Existing assignments were preserved; resolve storage access or restore a valid supported file, then retry the same chat and project.");
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string CanonicalChatId(string chatId) => Guid.TryParseExact(chatId, "D", out var parsed)
        ? parsed.ToString("D")
        : throw new ArgumentException("Supply your actual current CODEX_THREAD_ID as a UUID. Do not guess a chat ID.");

    private FileStream AcquireWriterLock(CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(75); }
        }
    }

    private byte[]? ReadBytes(CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException();
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (content.Length + count > MaximumFileBytes) throw new InvalidDataException();
                content.Write(buffer, 0, count);
            }
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private StoreDocument Parse(byte[]? bytes)
    {
        if (bytes is null) return new() { Version = 1, Scope = _scope, Associations = [] };
        using var parsed = JsonDocument.Parse(bytes);
        RequireUniqueProperties(parsed.RootElement);
        var document = parsed.RootElement.Deserialize<StoreDocument>(Json) ?? throw new InvalidDataException();
        if (document.Version != 1 || document.Scope != _scope || document.Associations is null ||
            document.Associations.Count > MaximumAssociations) throw new InvalidDataException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var association in document.Associations)
        {
            if (association is null || CanonicalChatId(association.ChatId) != association.ChatId ||
                !ids.Add(association.ChatId) || association.UpdatedAt == default || association.UpdatedAt.Offset != TimeSpan.Zero)
                throw new InvalidDataException();
            ValidateProjectId(association.ProjectId);
        }
        return document;
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RequireUniqueProperties(child);
    }

    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 200 || projectId.Any(char.IsControl) || projectId != projectId.Trim())
            throw new ArgumentException("Choose a configured project ID from launcher_projects.");
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsStoreFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException;

    private sealed class StoreDocument
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public string Scope { get; set; } = "";
        [JsonRequired] public List<StoredAssociation> Associations { get; set; } = [];
    }

    private sealed class StoredAssociation
    {
        [JsonRequired] public string ChatId { get; set; } = "";
        [JsonRequired] public string ProjectId { get; set; } = "";
        [JsonRequired] public DateTimeOffset UpdatedAt { get; set; }
    }
}
