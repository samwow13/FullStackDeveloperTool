using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

/// <summary>Windows-user protected connection library, separate from settings and publish seeds.</summary>
public sealed class SavedDatabaseConnectionStore
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("FSLDBC01");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string StorePath { get; }

    public SavedDatabaseConnectionStore(SettingsStore? settings = null)
    {
        settings ??= new SettingsStore();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullStackLauncher");
        // Explicit settings files have independent libraries, still outside the checkout and publish folder.
        if (!Path.GetFullPath(settings.SettingsPath).Equals(Path.GetFullPath(SettingsStore.DefaultSettingsPath), StringComparison.OrdinalIgnoreCase))
        {
            var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settings.SettingsPath).ToUpperInvariant())));
            directory = Path.Combine(directory, "database-connections", scope);
        }
        StorePath = Path.Combine(directory, "database-connections.dat");
    }

    public DatabaseDiscovery Load()
    {
        try { return new(Read(out _).Connections.Select(ToSource).ToArray(), ""); }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return new([], "Saved connections could not be unlocked or read for this Windows user. The existing file has been preserved; restore a valid copy before saving another connection.");
        }
    }

    /// <summary>Called only after an explicit successful connection. Re-read under the writer lock to retain other windows' saves.</summary>
    public DatabaseConnectionSource Save(string label, string connectionString)
    {
        try
        {
            connectionString = DatabaseConnectionSecurity.NormalizePostgresConnectionString(connectionString);
            var candidate = new SavedConnection { Id = $"saved/{Guid.NewGuid():N}", Label = label.Trim(), ConnectionString = connectionString };
            Validate(candidate);
            var lockName = "Local\\FullStackLauncher.DatabaseConnections." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StorePath.ToUpperInvariant())));
            using var mutex = new Mutex(false, lockName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new SavedDatabaseConnectionException("Another launcher is saving connections. Try again shortly.");
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                // This file is user-wide, while a Local mutex covers only one Windows session.
                // Hold a filesystem lock through the final revision check and atomic replacement.
                using var writerLock = new FileStream(StorePath + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                var data = Read(out var revision); // Malformed/unreadable libraries must never be overwritten.
                var builder = new NpgsqlConnectionStringBuilder(connectionString);
                var existing = data.Connections.FirstOrDefault(item => item.Label.Equals(candidate.Label, StringComparison.OrdinalIgnoreCase)
                    && new NpgsqlConnectionStringBuilder(item.ConnectionString).EquivalentTo(builder));
                if (existing is not null) return ToSource(existing);
                if (data.Connections.Count >= 500)
                    throw new SavedDatabaseConnectionException("The saved connection library has reached its 500-connection limit.");
                data.Connections.Add(candidate);
                Write(data, revision);
                return ToSource(candidate);
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (SavedDatabaseConnectionException) { throw; }
        catch (Exception ex) when (IsStoreError(ex))
        {
            throw new SavedDatabaseConnectionException("Connected, but the connection could not be saved securely. Existing saved connections were preserved. Check local file access or restore a valid connection library, then try again.");
        }
    }

    private byte[]? ReadBytes()
    {
        // File.Exists hides access failures; reading directly distinguishes a missing library from an unreadable one.
        byte[] content;
        try
        {
            using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
            content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return content;
    }

    private SavedLibrary Read(out byte[]? revision)
    {
        var content = ReadBytes();
        revision = content is null ? null : SHA256.HashData(content);
        if (content is null) return new();
        if (content.Length <= Header.Length || !content.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new InvalidDataException();
        var plaintext = Transform(content[Header.Length..], protect: false);
        try
        {
            var library = JsonSerializer.Deserialize<SavedLibrary>(plaintext, JsonOptions) ?? throw new InvalidDataException();
            if (library.Version != 1 || library.Connections is null || library.Connections.Count > 500) throw new InvalidDataException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in library.Connections)
            {
                Validate(entry);
                if (!ids.Add(entry.Id)) throw new InvalidDataException();
            }
            return library;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Write(SavedLibrary library, byte[]? revision)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(library, JsonOptions);
        byte[] ciphertext;
        try { ciphertext = Transform(plaintext, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (ciphertext.Length + Header.Length > MaximumFileBytes) throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temporaryPath = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Header);
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            var current = ReadBytes();
            var currentRevision = current is null ? null : SHA256.HashData(current);
            if (revision is null ? currentRevision is not null : currentRevision is null || !revision.AsSpan().SequenceEqual(currentRevision))
                throw new SavedDatabaseConnectionException("The saved connection library changed in another program. Nothing was overwritten. Try saving again.");
            if (current is not null) File.Replace(temporaryPath, StorePath, StorePath + ".bak");
            else File.Move(temporaryPath, StorePath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(SavedConnection? entry)
    {
        if (entry is null || entry.Id is null || !entry.Id.StartsWith("saved/", StringComparison.Ordinal)
            || !Guid.TryParseExact(entry.Id[6..], "N", out _) || string.IsNullOrWhiteSpace(entry.Label)
            || entry.Label.Length > 100 || entry.Label.Any(char.IsControl) || string.IsNullOrWhiteSpace(entry.ConnectionString)
            || entry.ConnectionString.Length > 32768) throw new InvalidDataException();
        _ = ToSource(entry);
    }

    private static DatabaseConnectionSource ToSource(SavedConnection entry) => new(entry.Id, "", $"{entry.Label} · Saved connection", entry.ConnectionString);

    private static bool IsStoreError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or CryptographicException or ArgumentException or System.Security.SecurityException or OverflowException;

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var outputBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            // No LOCAL_MACHINE flag: only the current Windows user can decrypt. UI is never permitted.
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!success) throw new CryptographicException();
            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[inputBlob.Length], 0, inputBlob.Data, inputBlob.Length);
            Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[outputBlob.Length], 0, outputBlob.Data, outputBlob.Length);
                LocalFree(outputBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class SavedLibrary
    {
        public SavedLibrary() { }
        [JsonRequired]
        public int Version { get; set; } = 1;
        [JsonRequired]
        public List<SavedConnection> Connections { get; set; } = [];
    }

    private sealed class SavedConnection
    {
        public SavedConnection() { }
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string ConnectionString { get; set; } = "";
    }
}

public sealed class SavedDatabaseConnectionException(string message) : Exception(message);
