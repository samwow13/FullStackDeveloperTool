using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

internal sealed record CodexActivityResetMarker(string AgentId, string? ActivityIdentity);
internal sealed record CodexActivityResetBaseline(DateTimeOffset? ResetAt,
    IReadOnlyList<CodexActivityResetMarker> Markers)
{
    public static CodexActivityResetBaseline Empty { get; } = new(null, []);
}

/// <summary>
/// Launcher-owned suppression receipts only. Never stores chat text, titles, working folders,
/// credentials, or commands, and never writes into Codex's state directory.
/// </summary>
internal sealed class CodexActivityResetStore
{
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumMarkers = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly string _mutexName;
    private readonly string _scope;

    public static string DefaultPath => SettingsStore.DefaultSettingsPath + ".codex-crew-reset.json";

    public CodexActivityResetStore(string codexHome, string? path)
    {
        _path = NormalizePath(path ?? DefaultPath);
        var home = NormalizePath(codexHome);
        if (string.Equals(_path, home, StringComparison.OrdinalIgnoreCase) ||
            _path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Crew reset history must be stored outside Codex's data folder.");
        _scope = Hash(home.ToUpperInvariant());
        _mutexName = @"Local\FullStackLauncher.CodexCrewReset." + Hash(_path.ToUpperInvariant());
    }

    public CodexActivityResetBaseline Load(CancellationToken token = default)
    {
        try
        {
            using var lease = Acquire(token);
            return LoadCore();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Saved crew reset history could not be read. Tracking was not reset.");
        }
    }

    public CodexActivityResetBaseline SaveMerged(IReadOnlyList<CodexActivityResetMarker> markers,
        DateTimeOffset resetAt, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            using var lease = Acquire(token);
            var previous = LoadCore();
            var combined = previous.Markers.Concat(markers).Distinct().ToArray();
            ValidateMarkers(combined);
            var effectiveResetAt = previous.ResetAt is { } previousTime && previousTime > resetAt ? previousTime : resetAt;
            var document = new SavedReset(1, _scope, effectiveResetAt, combined);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidOperationException("Crew reset history reached its storage limit.");
            var folder = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(folder);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            // The durable replacement is the commit point. Cancellation after it does not undo a reset.
            return new(effectiveResetAt, combined);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException("Crew reset history could not be saved. Tracking was not reset.");
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private CodexActivityResetBaseline LoadCore()
    {
        try { _ = File.GetAttributes(_path); }
        catch (FileNotFoundException) { return CodexActivityResetBaseline.Empty; }
        catch (DirectoryNotFoundException) { return CodexActivityResetBaseline.Empty; }
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidOperationException();
        var document = JsonSerializer.Deserialize<SavedReset>(stream, JsonOptions);
        if (document is null || document.Version != 1 || document.Scope != _scope || document.ResetAt == default ||
            document.Markers is null)
            throw new InvalidOperationException();
        ValidateMarkers(document.Markers);
        if (document.Markers.Distinct().Count() != document.Markers.Length) throw new InvalidOperationException();
        return new(document.ResetAt, document.Markers);
    }

    private MutexLease Acquire(CancellationToken token)
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < 2000)
            {
                token.ThrowIfCancellationRequested();
                try { if (mutex.WaitOne(100)) return new(mutex); }
                catch (AbandonedMutexException) { return new(mutex); }
            }
            throw new InvalidOperationException();
        }
        catch { mutex.Dispose(); throw; }
    }

    private static void ValidateMarkers(IReadOnlyList<CodexActivityResetMarker> markers)
    {
        if (markers.Count > MaximumMarkers) throw new InvalidOperationException();
        foreach (var marker in markers)
            if (marker is null || string.IsNullOrWhiteSpace(marker.AgentId) || marker.AgentId.Length > 256 ||
                marker.AgentId.Any(char.IsControl) || marker.ActivityIdentity is { } identity &&
                (string.IsNullOrWhiteSpace(identity) || identity.Length > 2048 || identity.Any(char.IsControl)))
                throw new InvalidOperationException();
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsStoreFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException;
    private sealed record SavedReset(int Version, string Scope, DateTimeOffset ResetAt, CodexActivityResetMarker[] Markers);
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
