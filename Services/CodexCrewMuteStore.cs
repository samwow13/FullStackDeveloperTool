using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.Services;

/// <summary>Saved presentation preferences for exact chats in one settings library.</summary>
internal sealed class CodexCrewMuteStore
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumChats = 10_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path = new SettingsStore().SettingsPath + ".codex-crew-mute.json";

    public IReadOnlySet<string> Read(CancellationToken token = default)
    {
        try { return Parse(ReadBytes(token)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Saved Mute preferences could not be read. Existing data and last known preferences were preserved.");
        }
    }

    public IReadOnlySet<string> SetMuted(string chatId, bool muted, CancellationToken token) =>
        SetMuted(new[] { chatId }, muted, token);

    public IReadOnlySet<string> SetMuted(IReadOnlyCollection<string> chatIds, bool muted, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            ArgumentNullException.ThrowIfNull(chatIds);
            if (chatIds.Count is 0 or > MaximumChats) throw new InvalidDataException();
            var targets = chatIds.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var chatId in targets) ValidateId(chatId);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var writer = AcquireWriter(token);
            var original = ReadBytes(token);
            var chats = Parse(original);
            // Merge every captured target in one transaction, retaining preferences
            // for other projects and chats no longer present in the current view.
            foreach (var chatId in targets)
            {
                if (muted) chats.Add(chatId);
                else chats.Remove(chatId);
            }
            if (chats.Count > MaximumChats) throw new InvalidDataException();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, chats.Order(StringComparer.Ordinal).ToArray()), Json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException();
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            var latest = ReadBytes(token);
            if (original is null ? latest is not null : latest is null || !original.AsSpan().SequenceEqual(latest))
                throw new InvalidOperationException("Mute preferences changed elsewhere. Retry the same action.");
            if (original is null) File.Move(temporary, _path, overwrite: false);
            else File.Replace(temporary, _path, _path + ".bak");
            temporary = null;
            return chats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Mute preference could not be saved. Previous preferences and existing data were preserved. Resolve file access or restore valid preferences, then retry.");
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private FileStream AcquireWriter(CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(75); }
        }
    }

    private byte[]? ReadBytes(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException();
            using var content = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (content.Length + count > MaximumBytes) throw new InvalidDataException();
                content.Write(buffer, 0, count);
            }
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static HashSet<string> Parse(byte[]? bytes)
    {
        if (bytes is null) return new(StringComparer.Ordinal);
        using var parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in parsed.RootElement.EnumerateObject())
            if (!properties.Add(property.Name)) throw new InvalidDataException();
        var document = parsed.RootElement.Deserialize<Document>(Json);
        if (document is null || document.Version != 1 || document.MutedChats is null || document.MutedChats.Length > MaximumChats)
            throw new InvalidDataException();
        var chats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in document.MutedChats)
        {
            ValidateId(id);
            if (!chats.Add(id)) throw new InvalidDataException();
        }
        return chats;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new InvalidDataException();
    }

    private static bool IsStoreFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException;
    private sealed record Document(int Version, string[] MutedChats);
}
