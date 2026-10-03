using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.ProjectTasks;

internal sealed record SavedSnipTarget(
    Guid Id,
    string ProcessName,
    string WindowTitle,
    string WindowClass,
    string? TabTitle,
    long WindowHandle,
    uint ProcessId,
    uint ThreadId,
    long StartTicks,
    int[]? TabRuntimeId);

/// <summary>Stores the last selected app and saved snip targets outside launcher settings.</summary>
internal sealed class SnipTargetStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly List<SavedSnipTarget> _savedTargets = [];
    private string? _lastSelectedApp;
    private string? _loadedFingerprint;

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "window-snip-targets.json");

    public string StorePath { get; }

    public string? LastSelectedApp
    {
        get { lock (_gate) return _lastSelectedApp; }
    }

    public IReadOnlyList<SavedSnipTarget> SavedTargets
    {
        get { lock (_gate) return _savedTargets.ToArray(); }
    }

    public SnipTargetStore() : this(null) { }

    /// <param name="explicitSettingsPath">
    /// Optional launcher settings filename. The complete filename identifies its
    /// isolated snip target file, matching ProjectTaskStore's override behavior.
    /// </param>
    public SnipTargetStore(string? explicitSettingsPath)
    {
        explicitSettingsPath ??= ReadSettingsOverride();
        if (explicitSettingsPath != null && string.IsNullOrWhiteSpace(explicitSettingsPath))
            throw new ArgumentException("A settings file path is required.", nameof(explicitSettingsPath));
        StorePath = explicitSettingsPath == null
            ? DefaultStorePath
            : Path.GetFullPath(explicitSettingsPath) + ".window-snip-targets.json";

        try
        {
            var content = ReadExistingFile();
            if (content == null) return;
            var data = ParseAndValidate(content);
            _lastSelectedApp = data.LastSelectedApp;
            _savedTargets.AddRange(data.SavedTargets);
            _loadedFingerprint = Fingerprint(content);
        }
        catch (Exception error) when (IsStoreException(error))
        {
            throw new InvalidOperationException(
                $"Saved snip targets could not be loaded. The existing file was preserved. Repair it or use another --settings file, then reopen the picker. Target store: {StorePath}",
                error);
        }
    }

    public void RecordLastSelectedApp(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        processName = processName.Trim();
        lock (_gate)
        {
            if (string.Equals(_lastSelectedApp, processName, StringComparison.OrdinalIgnoreCase)) return;
            Persist(new StoreDocument
            {
                LastSelectedApp = processName,
                SavedTargets = [.. _savedTargets]
            });
        }
    }

    public void Add(SavedSnipTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);
        lock (_gate)
        {
            if (_savedTargets.Any(saved => saved.Id == target.Id))
                throw new ArgumentException("A saved snip target already has this ID.", nameof(target));
            Persist(new StoreDocument
            {
                LastSelectedApp = _lastSelectedApp,
                SavedTargets = [.. _savedTargets, target]
            });
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_savedTargets.All(target => target.Id != id)) return;
            Persist(new StoreDocument
            {
                LastSelectedApp = _lastSelectedApp,
                SavedTargets = _savedTargets.Where(target => target.Id != id).ToList()
            });
        }
    }

    private void Persist(StoreDocument candidate)
    {
        Validate(candidate);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(candidate, JsonOptions) + Environment.NewLine);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

        using var writerLock = AcquireWriterLock();
        var current = ReadExistingFile();
        if (current != null)
        {
            try { ParseAndValidate(current); }
            catch (Exception error) when (IsStoreException(error))
            {
                throw new InvalidOperationException(
                    "The saved snip target file is now invalid or unsupported. It was preserved. Repair the file and reopen the picker.",
                    error);
            }
        }
        var fingerprint = current == null ? null : Fingerprint(current);
        if (!string.Equals(fingerprint, _loadedFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Saved snip targets changed in another launcher or editor. Reopen the picker before saving again.");

        var temporaryPath = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (current != null) File.Replace(temporaryPath, StorePath, StorePath + ".bak");
            else File.Move(temporaryPath, StorePath, overwrite: false);

            _lastSelectedApp = candidate.LastSelectedApp;
            _savedTargets.Clear();
            _savedTargets.AddRange(candidate.SavedTargets);
            _loadedFingerprint = Fingerprint(bytes);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private FileStream AcquireWriterLock()
    {
        try
        {
            return new FileStream(StorePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another launcher is saving snip targets. Reopen the picker and retry.", error);
        }
    }

    private byte[]? ReadExistingFile()
    {
        try { return File.ReadAllBytes(StorePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static StoreDocument ParseAndValidate(byte[] content)
    {
        ReadOnlyMemory<byte> json = content;
        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf)
            json = json[3..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = document.RootElement;
        RequireUniqueProperties(root);
        var version = RequireProperty(root, "version", JsonValueKind.Number);
        if (!version.TryGetInt32(out var sourceVersion) || sourceVersion != CurrentVersion)
            throw new ArgumentException("Unsupported snip target store version.");
        RequireNullableProperty(root, "lastSelectedApp", JsonValueKind.String);
        foreach (var target in RequireProperty(root, "savedTargets", JsonValueKind.Array).EnumerateArray())
        {
            RequireProperty(target, "id", JsonValueKind.String);
            RequireProperty(target, "processName", JsonValueKind.String);
            RequireProperty(target, "windowTitle", JsonValueKind.String);
            RequireProperty(target, "windowClass", JsonValueKind.String);
            RequireNullableProperty(target, "tabTitle", JsonValueKind.String);
            RequireProperty(target, "windowHandle", JsonValueKind.Number);
            RequireProperty(target, "processId", JsonValueKind.Number);
            RequireProperty(target, "threadId", JsonValueKind.Number);
            RequireProperty(target, "startTicks", JsonValueKind.Number);
            RequireNullableProperty(target, "tabRuntimeId", JsonValueKind.Array);
        }
        var data = JsonSerializer.Deserialize<StoreDocument>(json.Span, JsonOptions)
            ?? throw new ArgumentException("Snip target store is empty.");
        Validate(data);
        return data;
    }

    private static void Validate(StoreDocument data)
    {
        if (data.Version != CurrentVersion)
            throw new ArgumentException("Unsupported snip target store version.");
        if (data.LastSelectedApp is not null && string.IsNullOrWhiteSpace(data.LastSelectedApp))
            throw new ArgumentException("Last selected app must be a process name or null.");
        if (data.SavedTargets == null)
            throw new ArgumentException("Saved snip targets must be an array.");
        var ids = new HashSet<Guid>();
        foreach (var target in data.SavedTargets)
        {
            if (target == null) throw new ArgumentException("A saved snip target cannot be null.");
            ValidateTarget(target);
            if (!ids.Add(target.Id)) throw new ArgumentException("Duplicate saved snip target ID.");
        }
    }

    private static void ValidateTarget(SavedSnipTarget target)
    {
        if (target.Id == Guid.Empty) throw new ArgumentException("Saved snip target ID is required.");
        if (string.IsNullOrWhiteSpace(target.ProcessName))
            throw new ArgumentException("Saved snip target process name is required.");
        if (string.IsNullOrWhiteSpace(target.WindowTitle))
            throw new ArgumentException("Saved snip target window title is required.");
        if (string.IsNullOrWhiteSpace(target.WindowClass))
            throw new ArgumentException("Saved snip target window class is required.");
        if (target.TabTitle is not null && string.IsNullOrWhiteSpace(target.TabTitle))
            throw new ArgumentException("Saved snip target tab title must be text or null.");
        if ((target.TabTitle == null) != (target.TabRuntimeId == null) ||
            target.TabRuntimeId is { Length: 0 or > 128 })
            throw new ArgumentException("Saved snip target tab identity is invalid.");
        if (target.WindowHandle == 0 || target.ProcessId == 0 || target.ThreadId == 0 || target.StartTicks <= 0)
            throw new ArgumentException("Saved snip target window identity is required.");
    }

    private static JsonElement RequireProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == kind)
                    return property.Value;
        throw new ArgumentException($"A required '{name}' field is missing or invalid.");
    }

    private static void RequireNullableProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    (property.Value.ValueKind == JsonValueKind.Null || property.Value.ValueKind == kind))
                    return;
        throw new ArgumentException($"A required '{name}' field is missing or invalid.");
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Duplicate JSON properties are not supported.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
    }

    private static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static string? ReadSettingsOverride()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("--settings requires a settings JSON filename.");
                return args[i + 1];
            }
            if (args[i].StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--settings=".Length..];
        }
        return null;
    }

    private static bool IsStoreException(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private sealed class StoreDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public string? LastSelectedApp { get; set; }
        public List<SavedSnipTarget> SavedTargets { get; set; } = [];
    }
}
