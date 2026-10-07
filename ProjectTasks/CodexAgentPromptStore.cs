using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexAgentPromptPreferences(
    string ProjectId, string Context, string ModelId, string ReasoningEffort, string Revision)
{
    public CodexAgentAccessMode? AccessMode { get; init; }
}

/// <summary>Private prompt preferences, separate from settings and the automatic queue.</summary>
public sealed class CodexAgentPromptStore
{
    public const int MaximumContextCharacters = 8_000;
    private readonly string _path = CodexAgentStorage.ResolvePath("codex-agent-prompts.json");

    public CodexAgentPromptPreferences Load(string projectId)
    {
        CodexAgentStorage.RequireText(projectId, 200, "Select a project before opening an agent prompt.");
        var bytes = CodexAgentStorage.Read(_path, 4 * 1024 * 1024);
        var data = Parse(bytes);
        return new(projectId, data.Contexts.GetValueOrDefault(projectId, ""), data.ModelId,
            data.ReasoningEffort, Revision(bytes)) { AccessMode = data.AccessMode };
    }

    public CodexAgentPromptPreferences Save(CodexAgentPromptPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        CodexAgentStorage.RequireText(preferences.ProjectId, 200, "Select a project before saving agent context.");
        if (preferences.Context is null || preferences.Context.Length > MaximumContextCharacters
            || preferences.ModelId is null || preferences.ModelId.Length > 200
            || preferences.ReasoningEffort is null || preferences.ReasoningEffort.Length > 100
            || (preferences.AccessMode is { } mode && !Enum.IsDefined(mode)))
            throw new ArgumentException("Agent context or model preferences exceed their allowed length.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var gate = CodexAgentStorage.AcquireLock(_path + ".lock");
        var bytes = CodexAgentStorage.Read(_path, 4 * 1024 * 1024);
        var data = Parse(bytes);
        if (!string.Equals(Revision(bytes), preferences.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent prompt preferences changed in another window. Reopen this prompt before saving.");
        data.Contexts[preferences.ProjectId] = preferences.Context;
        data.ModelId = preferences.ModelId;
        data.ReasoningEffort = preferences.ReasoningEffort;
        data.AccessMode = preferences.AccessMode;
        data.Version = 2;
        var saved = CodexAgentStorage.Serialize(data);
        if (saved.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Saved agent context is too large. Shorten project context before saving.");
        CodexAgentStorage.WriteAtomic(_path, saved);
        return preferences with { Revision = Revision(saved) };
    }

    private static PreferenceData Parse(byte[]? bytes)
    {
        if (bytes is null) return new();
        var data = CodexAgentStorage.Deserialize<PreferenceData>(bytes);
        if (data.Version is not (1 or 2) || data.Contexts is null || data.Contexts.Count > 10_000
            || data.ModelId is null || data.ModelId.Length > 200
            || data.ReasoningEffort is null || data.ReasoningEffort.Length > 100
            || (data.AccessMode is { } mode && !Enum.IsDefined(mode))
            || data.Contexts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 200
                || pair.Value is null || pair.Value.Length > MaximumContextCharacters))
            throw new InvalidOperationException("Saved agent prompt preferences are invalid or unsupported. The existing file was preserved.");
        return data;
    }

    private static string Revision(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class PreferenceData
    {
        public int Version { get; set; } = 1;
        public string ModelId { get; set; } = "";
        public string ReasoningEffort { get; set; } = "";
        public CodexAgentAccessMode? AccessMode { get; set; }
        public Dictionary<string, string> Contexts { get; set; } = new(StringComparer.Ordinal);
    }
}

internal static class CodexAgentStorage
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static string ResolvePath(string name)
    {
        var settings = SettingsOverride();
        return settings is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullStackLauncher", name)
            : Path.GetFullPath(settings) + "." + name;
    }

    internal static string? SettingsOverride()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("--settings requires a settings JSON filename.");
                return Path.GetFullPath(args[index]);
            }
            if (args[index].StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var value = args[index]["--settings=".Length..];
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--settings requires a settings JSON filename.");
                return Path.GetFullPath(value);
            }
        }
        return null;
    }

    internal static byte[]? Read(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) throw new InvalidOperationException("Saved agent prompt data exceeds its supported size.");
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = stream.Read(block)) != 0)
            {
                if (buffer.Length + count > maximumBytes) throw new InvalidOperationException("Saved agent prompt data exceeds its supported size.");
                buffer.Write(block, 0, count);
            }
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static T Deserialize<T>(byte[] bytes) where T : class
    {
        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(offset), JsonOptions)
                ?? throw new InvalidOperationException("Saved agent prompt data is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Saved agent prompt data is invalid or unsupported. The existing file was preserved.");
        }
    }

    internal static byte[] Serialize<T>(T data) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOptions) + Environment.NewLine);

    internal static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another launcher is using this agent prompt. Wait briefly and try again."); }
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void RequireText(string? value, int maximum, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException(message);
    }
}
