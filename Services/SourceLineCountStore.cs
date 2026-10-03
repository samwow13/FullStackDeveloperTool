using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.Services;

public sealed record SourceLineCountSnapshot(
    SourceLineCountResult Current, long? PreviousLines, int ScopeVersion = SourceLineCounter.ScopeVersion)
{
    public bool HasCurrentScope => ScopeVersion == SourceLineCounter.ScopeVersion;
}

/// <summary>Stores successful counts separately from project settings and published application files.</summary>
public sealed class SourceLineCountStore
{
    private const int SchemaVersion = 2;
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _settingsPath;
    private readonly string _storageDirectory;

    public SourceLineCountStore(string settingsPath)
    {
        _settingsPath = NormalizePath(settingsPath);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new InvalidOperationException("The local application data folder is unavailable for line count history.");
        _storageDirectory = Path.Combine(localData, "FullStackLauncher", "source-line-counts");
    }

    // These synchronous APIs must be called on a worker, never the WPF dispatcher.
    public SourceLineCountSnapshot? Load(string projectId, string serviceId, string directory)
    {
        var key = GetKey(projectId, serviceId, directory);
        using var lease = AcquireLease(key);
        return Read(key);
    }

    public SourceLineCountSnapshot Save(
        string projectId, string serviceId, string directory, SourceLineCountResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result = result with { CountedAtUtc = result.CountedAtUtc.ToUniversalTime() };
        ValidateResult(result);
        var key = GetKey(projectId, serviceId, directory);
        using var lease = AcquireLease(key);
        // Read again under the lease so another launcher instance cannot lose its latest count.
        var previous = Read(key);
        if (previous is not null && previous.Current.CountedAtUtc >= result.CountedAtUtc)
        {
            if (previous.HasCurrentScope && previous.Current == result)
                return previous;
            throw new InvalidOperationException("A newer line count is already saved. Recount to update it.");
        }

        // Different exclusion rules establish a new baseline rather than a misleading code delta.
        var snapshot = new SourceLineCountSnapshot(result, previous?.HasCurrentScope == true ? previous.Current.Lines : null);
        var document = new StoreDocument
        {
            Version = SchemaVersion,
            ScopeVersion = SourceLineCounter.ScopeVersion,
            Identity = key,
            Lines = result.Lines,
            Files = result.Files,
            CountedAtUtc = result.CountedAtUtc,
            PreviousLines = snapshot.PreviousLines
        };
        var content = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (content.Length > MaximumBytes)
            throw new InvalidDataException("Line count history exceeds its storage limit.");

        var path = GetPath(key);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }
            if (previous is null)
                File.Move(temporaryPath, path);
            else
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
        }
        finally
        {
            // A cleanup failure must not hide the original error or a successful atomic replacement.
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return snapshot;
    }

    private SourceLineCountSnapshot? Read(string key)
    {
        byte[] content;
        try
        {
            using var input = new FileStream(GetPath(key), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumBytes)
                throw InvalidHistory();
            content = new byte[(int)input.Length];
            input.ReadExactly(content);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        StoreDocument? document;
        try { document = JsonSerializer.Deserialize<StoreDocument>(content, JsonOptions); }
        catch (JsonException) { throw InvalidHistory(); }
        if (document is null || document.Version is not (1 or SchemaVersion) || document.Identity != key
            || (document.Version == 1 ? document.ScopeVersion is not null : document.ScopeVersion != SourceLineCounter.ScopeVersion)
            || document.PreviousLines < 0 || document.CountedAtUtc.Offset != TimeSpan.Zero)
            throw InvalidHistory();

        var current = new SourceLineCountResult(document.Lines, document.Files, document.CountedAtUtc);
        ValidateResult(current);
        return new SourceLineCountSnapshot(current, document.PreviousLines, document.ScopeVersion ?? 1);
    }

    private FileStream AcquireLease(string key)
    {
        Directory.CreateDirectory(_storageDirectory);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // The persistent empty lock file excludes every process, including other Windows sessions.
                return new FileStream(GetPath(key) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (timer.Elapsed >= TimeSpan.FromSeconds(5))
                    throw new IOException("Line count history is busy in another launcher. Try again.", exception);
                Thread.Sleep(25);
            }
        }
    }

    private string GetKey(string projectId, string serviceId, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        // Serialize boundaries before hashing; no paths, IDs, or source contents are stored in the payload.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            _settingsPath, projectId, serviceId, NormalizePath(directory)
        });
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant();
    }

    private string GetPath(string key) => Path.Combine(_storageDirectory, key + ".json");

    private static void ValidateResult(SourceLineCountResult result)
    {
        if (result.Lines < 0 || result.Files < 0 || (result.Files == 0 && result.Lines != 0)
            || result.CountedAtUtc == default)
            throw InvalidHistory();
    }

    private static InvalidDataException InvalidHistory() => new(
        "Line count history is invalid or unsupported and has been preserved. Repair or remove its local history file before saving.");

    private sealed class StoreDocument
    {
        public required int Version { get; init; }
        public int? ScopeVersion { get; init; }
        public required string Identity { get; init; }
        public required long Lines { get; init; }
        public required int Files { get; init; }
        public required DateTimeOffset CountedAtUtc { get; init; }
        public required long? PreviousLines { get; init; }
    }
}
