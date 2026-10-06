using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.Services;

public sealed record ToolCredentialProfile(string Id, string Name, string UserName, string Kind,
    string? Origin, long Revision);

/// <summary>Owns the password returned by an explicit credential read.</summary>
public sealed class ToolCredentialValue : IDisposable
{
    private SecureString? _password;
    public ToolCredentialProfile Profile { get; }
    public SecureString Password => _password ?? throw new ObjectDisposedException(nameof(ToolCredentialValue));

    public ToolCredentialValue(ToolCredentialProfile profile, SecureString password)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(password);
        Profile = profile;
        _password = password;
    }

    public void Dispose() => Interlocked.Exchange(ref _password, null)?.Dispose();
}

public sealed class ToolCredentialStoreException : Exception
{
    public ToolCredentialStoreException(string message) : base(message) { }
}

/// <summary>
/// Stores developer-tool passwords in this Windows user's Credential Manager. Settings retain
/// only credential IDs. Listing reads application-owned metadata without copying passwords.
/// </summary>
public static class ToolCredentialStore
{
    private const string Prefix = "FullStackLauncher/DeveloperTools/Credentials/";
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;
    private const int MaximumBlobBytes = 2560;
    private const int MaximumNameLength = 80;
    private const int MaximumUserNameLength = 513;
    private const int MaximumCommentLength = 256;
    private const int NotFound = 1168;
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<ToolCredentialProfile> ListProfiles()
    {
        RequireWindows();
        if (!CredEnumerate(Prefix + "*", 0, out var count, out var buffer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return [];
            throw NativeFailure("read the saved credential list", error);
        }
        try
        {
            var profiles = new List<ToolCredentialProfile>();
            for (uint index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(buffer, checked((int)index * IntPtr.Size));
                if (pointer == IntPtr.Zero) throw InvalidStoredCredential();
                var native = Marshal.PtrToStructure<NativeCredential>(pointer);
                try { profiles.Add(ReadProfile(native)); }
                finally { ClearNativePassword(native); }
            }
            return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            // Enumeration returns one allocation including every credential blob. Clear all blobs
            // even when parsing an earlier entry fails, then free that allocation exactly once.
            try
            {
                for (uint index = 0; index < count; index++)
                {
                    var pointer = Marshal.ReadIntPtr(buffer, checked((int)index * IntPtr.Size));
                    if (pointer != IntPtr.Zero)
                        ClearNativePassword(Marshal.PtrToStructure<NativeCredential>(pointer));
                }
            }
            finally { CredFree(buffer); }
        }
    }

    public static ToolCredentialValue Read(string id)
    {
        RequireWindows();
        var target = TargetFor(id);
        var pointer = ReadNative(target, missingAllowed: false);
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            var profile = ReadProfile(native, target);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize == 0
                || native.CredentialBlobSize > MaximumBlobBytes || native.CredentialBlobSize % 2 != 0)
                throw InvalidStoredCredential();
            var password = new SecureString();
            try
            {
                for (var offset = 0; offset < native.CredentialBlobSize; offset += 2)
                {
                    var character = (char)Marshal.ReadInt16(native.CredentialBlob, offset);
                    if (character == '\0') throw InvalidStoredCredential();
                    password.AppendChar(character);
                }
                password.MakeReadOnly();
                return new ToolCredentialValue(profile, password);
            }
            catch
            {
                password.Dispose();
                throw;
            }
        }
        finally { ClearAndFree(pointer); }
    }

    public static ToolCredentialProfile Save(string? id, string name, string userName,
        SecureString password, string kind, string? origin, long? expectedRevision = null)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(password);
        name = ValidateText(name, MaximumNameLength, "Give this credential a name of 80 characters or fewer.");
        userName = ValidateText(userName, MaximumUserNameLength, "Enter a username of 513 characters or fewer.");
        kind = NormalizeKind(kind);
        origin = ValidateOrigin(kind, origin);
        if (password.Length is < 1 or > MaximumBlobBytes / 2)
            throw new ToolCredentialStoreException("Enter a password of 1 to 1280 characters.");
        var metadata = JsonSerializer.Serialize(new Metadata(1, name, kind, origin), MetadataJson);
        if (metadata.Length > MaximumCommentLength)
            throw new ToolCredentialStoreException("The credential name and website origin are too long. Shorten the credential name.");
        if (id is null && expectedRevision.HasValue)
            throw new ToolCredentialStoreException("Reload saved credentials before trying this operation again.");

        using var writer = AcquireWriter();
        var credentialId = id is null ? CreateUnusedId() : NormalizeId(id);
        var target = Prefix + credentialId;
        var existing = ReadNative(target, missingAllowed: true);
        try
        {
            if (id is not null)
            {
                if (existing == IntPtr.Zero || !expectedRevision.HasValue
                    || ReadProfile(Marshal.PtrToStructure<NativeCredential>(existing), target).Revision != expectedRevision.Value)
                    throw ChangedCredential();
            }
            else if (existing != IntPtr.Zero)
                throw ChangedCredential();
        }
        finally { ClearAndFree(existing); }

        IntPtr passwordBuffer = IntPtr.Zero;
        IntPtr targetBuffer = IntPtr.Zero;
        IntPtr metadataBuffer = IntPtr.Zero;
        IntPtr usernameBuffer = IntPtr.Zero;
        try
        {
            passwordBuffer = Marshal.SecureStringToGlobalAllocUnicode(password);
            for (var offset = 0; offset < password.Length * 2; offset += 2)
                if (Marshal.ReadInt16(passwordBuffer, offset) == 0)
                    throw new ToolCredentialStoreException("Passwords cannot contain a null character.");
            targetBuffer = Marshal.StringToHGlobalUni(target);
            metadataBuffer = Marshal.StringToHGlobalUni(metadata);
            usernameBuffer = Marshal.StringToHGlobalUni(userName);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetBuffer,
                Comment = metadataBuffer,
                CredentialBlobSize = checked((uint)password.Length * 2),
                CredentialBlob = passwordBuffer,
                Persist = LocalMachinePersistence,
                UserName = usernameBuffer
            };
            if (!CredWrite(ref native, 0))
                throw NativeFailure("save this credential", Marshal.GetLastWin32Error());
        }
        finally
        {
            if (passwordBuffer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(passwordBuffer);
            if (targetBuffer != IntPtr.Zero) Marshal.FreeHGlobal(targetBuffer);
            if (metadataBuffer != IntPtr.Zero) Marshal.FreeHGlobal(metadataBuffer);
            if (usernameBuffer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(usernameBuffer);
        }

        var saved = IntPtr.Zero;
        try
        {
            saved = ReadNative(target, missingAllowed: false);
            return ReadProfile(Marshal.PtrToStructure<NativeCredential>(saved), target);
        }
        catch (ToolCredentialStoreException)
        {
            throw new ToolCredentialStoreException("The credential was saved, but confirmation failed. Reload saved credentials before editing it again.");
        }
        finally { ClearAndFree(saved); }
    }

    public static void Delete(string id, long? expectedRevision = null)
    {
        RequireWindows();
        var target = TargetFor(id);
        using var writer = AcquireWriter();
        var pointer = ReadNative(target, missingAllowed: true);
        try
        {
            if (pointer == IntPtr.Zero || !expectedRevision.HasValue
                || ReadProfile(Marshal.PtrToStructure<NativeCredential>(pointer), target).Revision != expectedRevision.Value)
                throw ChangedCredential();
            if (!CredDelete(target, GenericCredential, 0))
                throw NativeFailure("remove this credential", Marshal.GetLastWin32Error());
        }
        finally { ClearAndFree(pointer); }
    }

    private static ToolCredentialProfile ReadProfile(NativeCredential native, string? expectedTarget = null)
    {
        if (native.Type != GenericCredential || native.Persist != LocalMachinePersistence)
            throw InvalidStoredCredential();
        var target = ReadBoundedString(native.TargetName, Prefix.Length + 32);
        if (!target.StartsWith(Prefix, StringComparison.Ordinal)
            || (expectedTarget is not null && !target.Equals(expectedTarget, StringComparison.Ordinal)))
            throw InvalidStoredCredential();
        var id = NormalizeId(target[Prefix.Length..]);
        var comment = ReadBoundedString(native.Comment, MaximumCommentLength);
        var username = ReadBoundedString(native.UserName, MaximumUserNameLength);
        Metadata? metadata;
        try { metadata = JsonSerializer.Deserialize<Metadata>(comment, MetadataJson); }
        catch (JsonException) { throw InvalidStoredCredential(); }
        if (metadata is null || metadata.Version != 1) throw InvalidStoredCredential();
        var name = ValidateText(metadata.Name, MaximumNameLength, "The saved credential metadata is invalid. Edit or remove it in Windows Credential Manager.");
        username = ValidateText(username, MaximumUserNameLength, "The saved credential metadata is invalid. Edit or remove it in Windows Credential Manager.");
        var kind = NormalizeKind(metadata.Kind);
        var origin = ValidateOrigin(kind, metadata.Origin);
        var revision = ((long)(uint)native.LastWritten.dwHighDateTime << 32) | (uint)native.LastWritten.dwLowDateTime;
        if (revision <= 0) throw InvalidStoredCredential();
        return new(id, name, username, kind, origin, revision);
    }

    private static string CreateUnusedId()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = Guid.NewGuid().ToString("N");
            var pointer = ReadNative(Prefix + id, missingAllowed: true);
            if (pointer == IntPtr.Zero) return id;
            ClearAndFree(pointer);
        }
        throw new ToolCredentialStoreException("A new credential could not be created safely. Try again.");
    }

    private static IntPtr ReadNative(string target, bool missingAllowed)
    {
        if (CredRead(target, GenericCredential, 0, out var pointer)) return pointer;
        var error = Marshal.GetLastWin32Error();
        if (error == NotFound)
        {
            if (missingAllowed) return IntPtr.Zero;
            throw new ToolCredentialStoreException("The saved credential is missing. Choose another credential or save it again.");
        }
        throw NativeFailure("read this credential", error);
    }

    private static string TargetFor(string id) => Prefix + NormalizeId(id);

    private static string NormalizeId(string? id)
    {
        if (id is null || !Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty)
            throw new ToolCredentialStoreException("The saved credential reference is invalid. Choose a saved credential again.");
        return parsed.ToString("N");
    }

    private static string NormalizeKind(string? kind)
    {
        if (string.Equals(kind, "Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (string.Equals(kind, "Website", StringComparison.OrdinalIgnoreCase)) return "Website";
        throw new ToolCredentialStoreException("Choose Windows account or Website login for this credential.");
    }

    private static string ValidateText(string? value, int maximumLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ToolCredentialStoreException(message);
        return value.Trim();
    }

    private static string? ValidateOrigin(string kind, string? origin)
    {
        if (kind == "Windows")
        {
            if (!string.IsNullOrEmpty(origin))
                throw new ToolCredentialStoreException("Windows account credentials cannot have a website origin.");
            return null;
        }
        if (origin is null || origin.Length > 2048 || !TryWebsiteOrigin(origin, out var canonicalOrigin, requireOriginOnly: true))
            throw new ToolCredentialStoreException("Use an HTTPS website origin without a username, path, query, or fragment. HTTP is allowed only for loopback sites.");
        return canonicalOrigin;
    }

    /// <summary>Validates website addresses without reading credentials and returns their exact canonical origin.</summary>
    public static bool TryWebsiteOrigin(string? value, out string origin, bool requireOriginOnly = false)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(uri.Host)
            || (requireOriginOnly && (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)))
            return false;
        try
        {
            var host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host) || (uri.Scheme == "http" && host is not ("localhost" or "127.0.0.1" or "::1")))
                return false;
            origin = uri.Scheme + "://" + (uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host)
                + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    private static string ReadBoundedString(IntPtr pointer, int maximumLength)
    {
        if (pointer == IntPtr.Zero) throw InvalidStoredCredential();
        var length = 0;
        while (length <= maximumLength && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
        if (length > maximumLength) throw InvalidStoredCredential();
        return Marshal.PtrToStringUni(pointer, length) ?? throw InvalidStoredCredential();
    }

    private static unsafe void ClearNativePassword(NativeCredential native)
    {
        if (native.CredentialBlob != IntPtr.Zero && native.CredentialBlobSize is > 0 and <= MaximumBlobBytes)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                new Span<byte>(native.CredentialBlob.ToPointer(), (int)native.CredentialBlobSize));
    }

    private static void ClearAndFree(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try { ClearNativePassword(Marshal.PtrToStructure<NativeCredential>(pointer)); }
        finally { CredFree(pointer); }
    }

    private static IDisposable AcquireWriter()
    {
        string? sid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
        }
        catch (SecurityException)
        {
            throw new ToolCredentialStoreException("The current Windows user could not be identified. Sign in again before saving credentials.");
        }
        if (sid is null)
            throw new ToolCredentialStoreException("The current Windows user could not be identified. Sign in again before saving credentials.");
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, @"Global\FullStackLauncher.ToolCredentials." + sid);
            var acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new ToolCredentialStoreException("Another launcher is saving credentials. Wait for it to finish, then try again.");
            return new WriterLock(mutex);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            throw new ToolCredentialStoreException("The credential save lock is unavailable. Close other launcher copies and try again.");
        }
        catch
        {
            mutex?.Dispose();
            throw;
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new ToolCredentialStoreException("Windows Credential Manager is available only on Windows.");
    }

    private static ToolCredentialStoreException ChangedCredential() => new(
        "This credential changed or was removed after it was opened. Reload saved credentials before saving or removing it.");

    private static ToolCredentialStoreException InvalidStoredCredential() => new(
        "The saved credential metadata is invalid. Edit or remove it in Windows Credential Manager.");

    private static ToolCredentialStoreException NativeFailure(string operation, int code) => new(code == 1312
        ? "Windows Credential Manager is unavailable in this logon session. Sign in interactively before managing credentials. Windows error 1312."
        : $"Windows Credential Manager could not {operation}. Check Windows access policies and try again. Windows error {code}.");

    private sealed record Metadata(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("o")] string? Origin);

    private sealed class WriterLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);
}
