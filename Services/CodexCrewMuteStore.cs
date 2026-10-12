using System.Collections.Frozen;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

internal sealed record CodexCrewMutePreferences(bool MuteAll, IReadOnlySet<string> MutedChats)
{
    public static CodexCrewMutePreferences Empty { get; } =
        new(false, Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal));
}

/// <summary>Current-user protected presentation preferences, local to this PC and settings library.</summary>
internal sealed class CodexCrewMuteStore
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumProtectedBytes = MaximumBytes + 65536;
    private const int MaximumChats = 10_000;
    private static readonly byte[] ProtectedHeader = Encoding.ASCII.GetBytes("FSLMUTE2");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _purpose;

    public CodexCrewMuteStore()
    {
        var settingsPath = Path.GetFullPath(new SettingsStore().SettingsPath).Replace('/', '\\');
        var canonical = settingsPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + settingsPath[8..]
            : settingsPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? settingsPath[4..] : settingsPath;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData) || !Path.IsPathFullyQualified(localData))
            throw new InvalidOperationException("Local Windows preferences folder is unavailable. Mute preferences cannot be accessed securely.");
        _path = Path.Combine(localData, "FullStackLauncher", "preferences", scope, "codex-crew-mute.dat");
        _legacyPath = settingsPath + ".codex-crew-mute.json";
        _purpose = "FullStackLauncher.CodexCrewMute.v2:" + scope;
    }

    public CodexCrewMutePreferences Read(CancellationToken token = default) => WithWriter(() => Load(token).Preferences, token, saving: false);

    public CodexCrewMutePreferences SetMuted(string chatId, bool muted, CancellationToken token = default) =>
        SetMuted(new[] { chatId }, muted, token);

    public CodexCrewMutePreferences SetMuted(IReadOnlyCollection<string> chatIds, bool muted, CancellationToken token = default) =>
        WithWriter(() =>
        {
            ArgumentNullException.ThrowIfNull(chatIds);
            if (chatIds.Count is 0 or > MaximumChats) throw new InvalidDataException();
            var targets = chatIds.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var chatId in targets) ValidateId(chatId);
            var snapshot = Load(token);
            var chats = new HashSet<string>(snapshot.Preferences.MutedChats, StringComparer.Ordinal);
            foreach (var chatId in targets)
            {
                if (muted) chats.Add(chatId);
                else chats.Remove(chatId);
            }
            if (chats.Count > MaximumChats) throw new InvalidDataException();
            return Save(snapshot, new(snapshot.Preferences.MuteAll, chats.ToFrozenSet(StringComparer.Ordinal)), token);
        }, token, saving: true);

    public CodexCrewMutePreferences SetMuteAll(bool muted, CancellationToken token = default) => WithWriter(() =>
    {
        var snapshot = Load(token);
        return Save(snapshot, snapshot.Preferences with { MuteAll = muted }, token);
    }, token, saving: true);

    private T WithWriter<T>(Func<T> action, CancellationToken token, bool saving)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var writer = AcquireWriter(_path, token);
            return action();
        }
        catch (OperationCanceledException) { throw; }
        catch (MuteStoreException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new InvalidOperationException(saving
                ? "Mute preference could not be saved. Previous preferences and existing data were preserved. Resolve file access or restore valid protected preferences for this Windows user, then retry."
                : "Saved Mute preferences could not be read. Existing data and last known preferences were preserved. Resolve file access or restore valid protected preferences for this Windows user, then retry.");
        }
    }

    private Snapshot Load(CancellationToken token)
    {
        var original = ReadBytes(_path, MaximumProtectedBytes, token);
        var backup = ReadBytes(_path + ".bak", MaximumProtectedBytes, token);
        if (original is null && backup is not null)
            throw new MuteStoreException("Protected Mute preferences are missing while their encrypted backup remains. Restore valid protected preferences before retrying.");
        var preferences = original is null ? CodexCrewMutePreferences.Empty : Unprotect(original);
        var backupPreferences = backup is null ? null : Unprotect(backup);

        // A valid protected document, including an empty one, is authoritative.
        // Legacy data is used only for the first migration, never as a fallback.
        if (!Exists(_legacyPath) && !Exists(_legacyPath + ".bak"))
            return new(original, backup, preferences);

        using var legacyWriter = AcquireWriter(_legacyPath, token);
        var legacy = ReadLegacy(token);
        if (original is null)
        {
            if (legacy.Current is null && legacy.Backup is null) return new(null, null, preferences);
            if (legacy.Current is null)
                throw new MuteStoreException("Legacy Mute preferences are missing while their backup remains. Restore valid preferences before retrying; existing files were preserved.");
            return Migrate(legacy, token);
        }

        // Resume cleanup after an interrupted migration only when each legacy
        // snapshot is already represented by the protected current or backup.
        if (legacy.CurrentPreferences is not null && !SameChats(legacy.CurrentPreferences, preferences) &&
            (backupPreferences is null || !SameChats(legacy.CurrentPreferences, backupPreferences)))
            throw new MuteStoreException("Legacy Mute preferences changed after protected migration. Existing data was preserved. Review the legacy and protected preferences before retrying.");
        if (backup is null && legacy.BackupPreferences is not null)
        {
            // Preserve encrypted history if migration stopped after the current commit.
            backup = Protect(legacy.BackupPreferences);
            WriteNewBackup(original, backup, legacy, token);
            backupPreferences = legacy.BackupPreferences;
        }
        if (legacy.BackupPreferences is not null && !SameChats(legacy.BackupPreferences, preferences) &&
            (backupPreferences is null || !SameChats(legacy.BackupPreferences, backupPreferences)))
            throw new MuteStoreException("Legacy Mute backup changed after protected migration. Existing data was preserved. Review the legacy and protected preferences before retrying.");
        VerifyProtected(original, backup, token);
        DeleteLegacy(legacy, token);
        return new(original, backup, preferences);
    }

    private Snapshot Migrate(LegacySnapshot legacy, CancellationToken token)
    {
        var current = Protect(legacy.CurrentPreferences!);
        var backup = Protect(legacy.BackupPreferences ?? legacy.CurrentPreferences!);
        string? currentTemporary = null;
        string? backupTemporary = null;
        var committed = false;
        try
        {
            currentTemporary = WriteTemporary(_path, current, token);
            backupTemporary = WriteTemporary(_path + ".bak", backup, token);
            EnsureUnchanged(_path, null, MaximumProtectedBytes, token);
            EnsureUnchanged(_path + ".bak", null, MaximumProtectedBytes, token);
            EnsureLegacyUnchanged(legacy, token);
            File.Move(currentTemporary, _path, overwrite: false);
            currentTemporary = null;
            committed = true;
            File.Move(backupTemporary, _path + ".bak", overwrite: false);
            backupTemporary = null;
            VerifyProtected(current, backup, token);
            DeleteLegacy(legacy, token);
            return new(current, backup, legacy.CurrentPreferences!);
        }
        catch (OperationCanceledException) { throw; }
        catch (MuteStoreException) { throw; }
        catch (Exception ex) when (committed && IsStoreFailure(ex))
        {
            throw new MuteStoreException("Protected Mute migration could not finish. Legacy files were preserved unless their verified encrypted copy was already saved. Resolve file access and restore valid protected preferences before retrying.");
        }
        finally
        {
            DeleteTemporary(currentTemporary);
            DeleteTemporary(backupTemporary);
        }
    }

    private void WriteNewBackup(byte[] current, byte[] backup, LegacySnapshot legacy, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            temporary = WriteTemporary(_path + ".bak", backup, token);
            EnsureUnchanged(_path, current, MaximumProtectedBytes, token);
            EnsureUnchanged(_path + ".bak", null, MaximumProtectedBytes, token);
            EnsureLegacyUnchanged(legacy, token);
            File.Move(temporary, _path + ".bak", overwrite: false);
            temporary = null;
        }
        finally { DeleteTemporary(temporary); }
    }

    private CodexCrewMutePreferences Save(Snapshot snapshot, CodexCrewMutePreferences preferences, CancellationToken token)
    {
        string? temporary = null;
        string? backupTemporary = null;
        try
        {
            var bytes = Protect(preferences);
            temporary = WriteTemporary(_path, bytes, token);
            if (snapshot.Current is null)
                backupTemporary = WriteTemporary(_path + ".bak", Protect(snapshot.Preferences), token);
            EnsureUnchanged(_path, snapshot.Current, MaximumProtectedBytes, token);
            EnsureUnchanged(_path + ".bak", snapshot.Backup, MaximumProtectedBytes, token);
            // Reject plaintext reintroduced by a previous launcher during a protected save.
            if (Exists(_legacyPath) || Exists(_legacyPath + ".bak"))
                throw new MuteStoreException("Legacy Mute preferences changed during this save. Existing data was preserved. Retry after reviewing the legacy preferences.");
            if (snapshot.Current is null)
            {
                // Retain encrypted previous defaults even on the first save.
                // An interrupted first commit then fails closed instead of
                // silently falling back to an absent or stale plaintext store.
                File.Move(backupTemporary!, _path + ".bak", overwrite: false);
                backupTemporary = null;
                File.Move(temporary, _path, overwrite: false);
            }
            else File.Replace(temporary, _path, _path + ".bak");
            temporary = null;
            return preferences;
        }
        finally
        {
            DeleteTemporary(temporary);
            DeleteTemporary(backupTemporary);
        }
    }

    private LegacySnapshot ReadLegacy(CancellationToken token)
    {
        var current = ReadBytes(_legacyPath, MaximumBytes, token);
        var backup = ReadBytes(_legacyPath + ".bak", MaximumBytes, token);
        return new(current, backup, current is null ? null : Parse(current, legacy: true),
            backup is null ? null : Parse(backup, legacy: true));
    }

    private void EnsureLegacyUnchanged(LegacySnapshot snapshot, CancellationToken token)
    {
        EnsureUnchanged(_legacyPath, snapshot.Current, MaximumBytes, token);
        EnsureUnchanged(_legacyPath + ".bak", snapshot.Backup, MaximumBytes, token);
    }

    private void VerifyProtected(byte[] current, byte[]? backup, CancellationToken token)
    {
        EnsureUnchanged(_path, current, MaximumProtectedBytes, token);
        EnsureUnchanged(_path + ".bak", backup, MaximumProtectedBytes, token);
        _ = Unprotect(current);
        if (backup is not null) _ = Unprotect(backup);
    }

    private void DeleteLegacy(LegacySnapshot snapshot, CancellationToken token)
    {
        try
        {
            DeleteLegacyIfUnchanged(_legacyPath, snapshot.Current, token);
            DeleteLegacyIfUnchanged(_legacyPath + ".bak", snapshot.Backup, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            throw new MuteStoreException("Mute preferences were encrypted locally, but legacy plaintext cleanup could not finish. Remaining legacy files were preserved. Resolve file access or legacy conflicts, then retry.");
        }
    }

    private static void DeleteLegacyIfUnchanged(string path, byte[]? expected, CancellationToken token)
    {
        if (expected is null)
        {
            EnsureUnchanged(path, null, MaximumBytes, token);
            return;
        }
        token.ThrowIfCancellationRequested();
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        // The same handle denies writes and replacement through verification and deletion.
        using var handle = CreateFile(nativePath, 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Legacy preference cleanup could not open the existing file.", new Win32Exception(Marshal.GetLastWin32Error()));
        using var stream = new FileStream(handle, FileAccess.Read);
        var actual = ReadContents(stream, MaximumBytes, token);
        try
        {
            if (!expected.AsSpan().SequenceEqual(actual)) throw new MuteStoreException("Mute preferences changed elsewhere. Retry the same action.");
            token.ThrowIfCancellationRequested();
            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
                throw new IOException("Legacy preference cleanup could not remove the existing file.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private byte[] Protect(CodexCrewMutePreferences preferences)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Document(2, preferences.MuteAll,
            preferences.MutedChats.Order(StringComparer.Ordinal).ToArray()), Json);
        try
        {
            if (plaintext.Length is 0 or > MaximumBytes) throw new InvalidDataException();
            var ciphertext = WindowsUserSecretProtection.Protect(plaintext, _purpose);
            if (ciphertext.Length + ProtectedHeader.Length > MaximumProtectedBytes) throw new InvalidDataException();
            var bytes = new byte[ProtectedHeader.Length + ciphertext.Length];
            ProtectedHeader.CopyTo(bytes, 0);
            ciphertext.CopyTo(bytes, ProtectedHeader.Length);
            return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private CodexCrewMutePreferences Unprotect(byte[] bytes)
    {
        if (bytes.Length <= ProtectedHeader.Length || !bytes.AsSpan(0, ProtectedHeader.Length).SequenceEqual(ProtectedHeader))
            throw new InvalidDataException();
        var plaintext = WindowsUserSecretProtection.Unprotect(bytes[ProtectedHeader.Length..], _purpose);
        try
        {
            if (plaintext.Length is 0 or > MaximumBytes) throw new InvalidDataException();
            return Parse(plaintext, legacy: false);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static CodexCrewMutePreferences Parse(byte[] bytes, bool legacy)
    {
        using var parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parsed.RootElement.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value)) throw new InvalidDataException();
        if (properties.Count != (legacy ? 2 : 3) || !properties.TryGetValue("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != (legacy ? 1 : 2) ||
            !properties.TryGetValue("mutedChats", out var mutedChats) || mutedChats.ValueKind != JsonValueKind.Array ||
            mutedChats.GetArrayLength() > MaximumChats)
            throw new InvalidDataException();
        var muteAll = false;
        if (!legacy)
        {
            if (!properties.TryGetValue("muteAll", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException();
            muteAll = flag.GetBoolean();
        }
        var chats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in mutedChats.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            var id = element.GetString()!;
            ValidateId(id);
            if (!chats.Add(id)) throw new InvalidDataException();
        }
        return new(muteAll, chats.ToFrozenSet(StringComparer.Ordinal));
    }

    private static bool SameChats(CodexCrewMutePreferences left, CodexCrewMutePreferences right) => left.MutedChats.SetEquals(right.MutedChats);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new InvalidDataException();
    }

    private static FileStream AcquireWriter(string path, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(75); }
        }
    }

    private static byte[]? ReadBytes(string path, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadContents(stream, maximum, token);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static byte[] ReadContents(FileStream stream, int maximum, CancellationToken token)
    {
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException();
        using var content = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (content.Length + count > maximum) throw new InvalidDataException();
            content.Write(buffer, 0, count);
        }
        if (content.Length == 0) throw new InvalidDataException();
        return content.ToArray();
    }

    private static void EnsureUnchanged(string path, byte[]? expected, int maximum, CancellationToken token)
    {
        var latest = ReadBytes(path, maximum, token);
        if (expected is null ? latest is not null : latest is null || !expected.AsSpan().SequenceEqual(latest))
            throw new MuteStoreException("Mute preferences changed elsewhere. Retry the same action.");
    }

    private static string WriteTemporary(string path, byte[] ciphertext, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(ciphertext);
            stream.Flush(flushToDisk: true);
            return temporary;
        }
        catch { DeleteTemporary(temporary); throw; }
    }

    private static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsStoreFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        System.Security.SecurityException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or CryptographicException;
    private sealed record Document(int Version, bool MuteAll, string[] MutedChats);
    private sealed record Snapshot(byte[]? Current, byte[]? Backup, CodexCrewMutePreferences Preferences);
    private sealed record LegacySnapshot(byte[]? Current, byte[]? Backup,
        CodexCrewMutePreferences? CurrentPreferences, CodexCrewMutePreferences? BackupPreferences);
    private sealed class MuteStoreException(string message) : InvalidOperationException(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint attributes, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDispositionInfo information, uint size);
}
