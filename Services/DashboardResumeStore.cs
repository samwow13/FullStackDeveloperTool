using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

/// <summary>Encrypted, user-local editor checkpoints, isolated from applied settings and queued work.</summary>
internal sealed class DashboardResumeStore
{
    private const int MaximumBytes = 256 * 1024 * 1024;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("FSLRESUME1\n");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly string _purpose;
    private readonly string _mutexName;

    internal DashboardResumeStore(string settingsPath)
    {
        var scope = Path.GetFullPath(settingsPath);
        _path = scope.Equals(SettingsStore.DefaultSettingsPath, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(scope)!, "dashboard-resume.dat")
            : scope + ".dashboard-resume.dat";
        _purpose = "FullStackLauncher.DashboardResume.v1:" + scope.ToUpperInvariant();
        _mutexName = @"Local\FullStackLauncher.DashboardResume." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_purpose)));
    }

    internal T? Load<T>(string section)
    {
        ValidateSection(section);
        return WithLock(() =>
        {
            var checkpoint = Read();
            return checkpoint.Sections.TryGetValue(section, out var value)
                ? value.Deserialize<T>(Json) : default;
        });
    }

    internal void Save<T>(string section, T value)
    {
        ValidateSection(section);
        WithLock(() =>
        {
            var checkpoint = Read();
            checkpoint.Sections[section] = JsonSerializer.SerializeToElement(value, Json);
            Write(checkpoint);
            return true;
        });
    }

    internal void Remove(string section)
    {
        ValidateSection(section);
        WithLock(() =>
        {
            var checkpoint = Read();
            if (checkpoint.Sections.Remove(section)) Write(checkpoint);
            return true;
        });
    }

    internal void Clear()
    {
        WithLock(() =>
        {
            // An encrypted empty checkpoint stays authoritative over the previous backup.
            if (File.Exists(_path)) { _ = Read(); Write(new()); }
            return true;
        });
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException();
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new IOException("Launcher drafts could not be checkpointed or restored. Existing drafts and saved files were preserved. Check local storage access and retry.");
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private Checkpoint Read()
    {
        if (!File.Exists(_path)) return new();
        var length = new FileInfo(_path).Length;
        if (length <= Header.Length || length > MaximumBytes) throw new IOException();
        var protectedBytes = File.ReadAllBytes(_path);
        if (!protectedBytes.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new IOException();
        var plaintext = WindowsUserSecretProtection.Unprotect(protectedBytes[Header.Length..], _purpose);
        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(plaintext, Json);
            if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Sections is null || checkpoint.Sections.Count > 512)
                throw new JsonException();
            foreach (var section in checkpoint.Sections.Keys) ValidateSection(section);
            return checkpoint;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Write(Checkpoint checkpoint)
    {
        if (checkpoint.Sections.Count > 512) throw new IOException();
        byte[]? plaintext = null;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(checkpoint, Json);
            if (plaintext.Length > MaximumBytes - 4096) throw new IOException();
            var ciphertext = WindowsUserSecretProtection.Protect(plaintext, _purpose);
            if (ciphertext.Length + Header.Length > MaximumBytes) throw new IOException();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                output.Write(Header);
                output.Write(ciphertext);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section) || section.Length > 128 || section.Any(char.IsControl))
            throw new ArgumentException("Invalid Launcher draft checkpoint section.");
    }

    private sealed class Checkpoint
    {
        public Checkpoint() { }
        public int Version { get; set; } = 1;
        public Dictionary<string, JsonElement> Sections { get; set; } = new(StringComparer.Ordinal);
    }
}
