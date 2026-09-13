using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FullStackLauncher.Services;

/// <summary>
/// Stores launcher API overrides with current-user DPAPI, independently of external .NET user-secrets.
/// Legacy files can be explicitly copied into a draft; they are never modified or removed here.
/// </summary>
public sealed class ApiSecretStore
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumProtectedFileBytes = MaximumFileBytes + 65536;
    private const string ProductionSuffix = "-launcher-production";
    private static readonly byte[] ProtectedHeader = Encoding.ASCII.GetBytes("FSLAPI01");
    private readonly string _workingDirectory;

    public string ProjectFilePath { get; }
    public string UserSecretsId { get; }

    public ApiSecretStore(string workingDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
                throw new ApiSecretStoreException("Choose an API working folder before managing configuration.");
            _workingDirectory = Path.GetFullPath(workingDirectory);
            (ProjectFilePath, UserSecretsId) = DiscoverProject(_workingDirectory);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The API project configuration could not be read. Check its folder and access permissions.");
        }
    }

    public ApiSecretSnapshot Load(bool production)
    {
        try
        {
            CheckProjectReference();
            var (storeId, path) = ResolveStore(production);
            var content = ReadContents(path, MaximumProtectedFileBytes);
            if (content is null)
                return CreateSnapshot(production, storeId, path, null, null, false, LegacyStoreExists(production));
            var plaintext = Unprotect(content, storeId, production);
            try { return CreateSnapshot(production, storeId, path, plaintext, RevisionOf(content), true, LegacyStoreExists(production)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The encrypted configuration could not be unlocked or read for this Windows user. Existing files were preserved. Check access permissions or restore a valid encrypted copy before reloading.");
        }
    }

    /// <summary>Explicitly reads this profile's external plaintext file into a draft without writing either store.</summary>
    public ApiSecretSnapshot ReadLegacyForImport(bool production, ApiSecretSnapshot expected)
    {
        byte[]? plaintext = null;
        try
        {
            CheckProjectReference();
            var (storeId, path) = ResolveStore(production);
            ValidateExpected(production, path, expected);
            EnsureUnchanged(path, expected);
            plaintext = ReadContents(ResolveLegacyPath(production));
            if (plaintext is null)
                throw new ApiSecretStoreException("There is no legacy .NET user-secrets file for this profile. Enter new values in the encrypted launcher configuration instead.");
            return CreateSnapshot(production, storeId, path, plaintext, expected.Revision, expected.HasProtectedStore,
                hasLegacyStore: true, isLegacyImport: true);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The legacy .NET user-secrets file could not be read. Neither store was changed; check its access permissions and JSON before importing.");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>The supplied dictionary is the complete edited snapshot, including unchanged entries.</summary>
    public ApiSecretSnapshot Save(bool production, ApiSecretSnapshot expected, IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string>? explicitTextKeys = null)
    {
        string? temporaryPath = null;
        byte[]? plaintext = null;
        try
        {
            CheckProjectReference();
            var (storeId, path) = ResolveStore(production);
            ValidateExpected(production, path, expected);

            var edited = ValidateValues(values);
            var textKeys = explicitTextKeys is null ? null : new HashSet<string>(explicitTextKeys, StringComparer.OrdinalIgnoreCase);
            plaintext = Serialize(edited, expected, textKeys);
            var encrypted = WindowsUserSecretProtection.Protect(plaintext, ProtectionPurpose(storeId, production));
            if (encrypted.Length + ProtectedHeader.Length > MaximumProtectedFileBytes)
                throw new ApiSecretStoreException("The encrypted configuration is too large to save safely.");
            var mutexName = @"Local\FullStackLauncher.ApiSecrets." +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            using var writerMutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                try { acquired = writerMutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                    throw new ApiSecretStoreException("Another launcher is saving this secret store. Wait and try again.", isConflict: true);

                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                // File sharing locks cover other Windows/RDP sessions using this same user-wide store.
                // The persistent sidecar is empty and never contains configuration values.
                using var fileLock = AcquireWriteLock(path);
                EnsureUnchanged(path, expected);
                temporaryPath = Path.Combine(directory, ".launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(ProtectedHeader);
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }

                // Recheck after preparing ciphertext to catch another launcher or external replacement.
                CheckProjectReference();
                EnsureUnchanged(path, expected);
                if (expected.Revision is null)
                    File.Move(temporaryPath, path, overwrite: false);
                else
                    File.Replace(temporaryPath, path, destinationBackupFileName: path + ".bak");
                temporaryPath = null;
                // Construct from the bytes just written, without a post-commit disk read that could imply a failed save.
                var storedContent = new byte[ProtectedHeader.Length + encrypted.Length];
                ProtectedHeader.CopyTo(storedContent, 0);
                encrypted.CopyTo(storedContent, ProtectedHeader.Length);
                return CreateSnapshot(production, storeId, path, plaintext, RevisionOf(storedContent), true, expected.HasLegacyStore);
            }
            finally
            {
                if (acquired) writerMutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The secret store could not be saved. Your draft is retained; check permissions and reload before retrying.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (IsFileError(ex)) { /* Never log a secret path or file contents. */ }
            }
        }
    }

    /// <summary>Discovers setting names and their last JSON source, without returning appsettings values.</summary>
    public IReadOnlyDictionary<string, ApiAppSetting> ReadAppSettings(bool production)
    {
        try
        {
            CheckProjectReference();
            var result = new Dictionary<string, ApiAppSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { "appsettings.json", production ? "appsettings.Production.json" : "appsettings.Development.json" })
            {
                var content = ReadContents(Path.Combine(_workingDirectory, source));
                if (content is null) continue;
                foreach (var key in Parse(content, "The appsettings configuration is invalid. Fix its JSON before reloading.").Keys)
                    result[key] = new ApiAppSetting(key, source);
            }
            return new ReadOnlyDictionary<string, ApiAppSetting>(result);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The appsettings configuration could not be read. Check its access permissions.");
        }
    }

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Split(':').Any(string.IsNullOrWhiteSpace))
            throw new ApiSecretStoreException("Enter a configuration key with nonempty sections separated by colons, such as ConnectionStrings:DefaultConnection.");
        if (key.Contains("__", StringComparison.Ordinal) || key.IndexOfAny(['=', '\0']) >= 0)
            throw new ApiSecretStoreException("Configuration keys cannot contain double underscores, an equals sign, or a null character. Use colons for nested settings.");
        if (!key.Replace(":", "__", StringComparison.Ordinal).Replace("__", ":", StringComparison.Ordinal).Equals(key, StringComparison.Ordinal))
            throw new ApiSecretStoreException("A configuration section cannot end with an underscore before a colon because its environment-variable mapping would be ambiguous.");
        if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
            new[] { "COMPLUS_", "CORECLR_", "COR_", "MSBUILD", "NUGET", "CUSTOMBEFORE", "CUSTOMAFTER", "DIRECTORYBUILD" }
                .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            new[] { "ENVIRONMENT", "URLS", "HTTP_PORTS", "HTTPS_PORTS", "CONTENTROOT", "WEBROOT", "APPLICATIONNAME",
                "USERSECRETSID", "PATH", "COMSPEC", "SYSTEMROOT", "WINDIR", "APPDATA", "LOCALAPPDATA", "USERPROFILE",
                "HOME", "HOMEDRIVE", "HOMEPATH", "TEMP", "TMP", "PATHEXT", "PSMODULEPATH", "PROGRAMFILES",
                "PROGRAMFILES(X86)", "PROGRAMW6432", "PROGRAMDATA", "ALLUSERSPROFILE", "SYSTEMDRIVE",
                "VSINSTALLDIR", "VCINSTALLDIR", "VISUALSTUDIOVERSION", "ROSLYNTARGETSPATH", "CSCTOOLPATH",
                "CSCTOOLEXE", "VBCTOOLPATH", "VBCTOOLEXE", "BASEINTERMEDIATEOUTPUTPATH", "INTERMEDIATEOUTPUTPATH",
                "OUTPUTPATH", "ARTIFACTSPATH", "PROJECTASSETSFILE", "GENERATEUSERSECRETSATTRIBUTE", "USEARTIFACTSOUTPUT" }
                .Contains(key, StringComparer.OrdinalIgnoreCase))
            throw new ApiSecretStoreException("Runtime, environment, listening URL, and operating-system settings are controlled by the launcher and cannot be entered as application secrets.");
        if (new[] { "SQLCONNSTR_", "SQLAZURECONNSTR_", "MYSQLCONNSTR_", "CUSTOMCONNSTR_" }
            .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new ApiSecretStoreException("Use ConnectionStrings:Name for a connection setting instead of a platform-specific connection-string environment prefix.");
    }

    private static Dictionary<string, string> ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        if (values is null) throw new ApiSecretStoreException("Configuration values are required before saving.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values)
        {
            ValidateKey(entry.Key);
            if (entry.Value is null || entry.Value.Contains('\0'))
                throw new ApiSecretStoreException("Configuration values must be text and cannot contain a null character.");
            if (!result.TryAdd(entry.Key, entry.Value))
                throw new ApiSecretStoreException("Configuration keys must be unique, without regard to letter case.");
        }
        return result;
    }

    private void CheckProjectReference()
    {
        var current = DiscoverProject(_workingDirectory);
        if (!current.Path.Equals(ProjectFilePath, StringComparison.OrdinalIgnoreCase) ||
            !current.Id.Equals(UserSecretsId, StringComparison.Ordinal))
            throw new ApiSecretStoreException("The API project or its user-secrets reference changed. Close and reopen configuration before continuing.");
    }

    private static (string Path, string Id) DiscoverProject(string directory)
    {
        if (!Directory.Exists(directory))
            throw new ApiSecretStoreException("The configured API working folder is unavailable.");
        var projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length != 1)
            throw new ApiSecretStoreException("Choose an API working folder containing exactly one .csproj file.");
        using var reader = XmlReader.Create(projects[0], new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "Project")
            throw new ApiSecretStoreException("The configured .csproj file is not a valid API project document.");
        var references = document.Descendants().Where(x => x.Name.LocalName == "UserSecretsId").ToArray();
        if (references.Length != 1)
            throw new ApiSecretStoreException("The API project must declare exactly one literal UserSecretsId. Initialize .NET user-secrets for the API, then reopen configuration.");
        var reference = references[0];
        var id = reference.Value.Trim();
        if (reference.HasElements || reference.Parent?.Name.LocalName != "PropertyGroup" ||
            reference.Parent?.Parent != document.Root ||
            reference.Name.Namespace != document.Root.Name.Namespace ||
            reference.Parent?.Name.Namespace != document.Root.Name.Namespace ||
            reference.AncestorsAndSelf().Any(x => x.Attributes().Any(a => a.Name.LocalName == "Condition")) ||
            id.Length is < 1 or > 180 || id is "." or ".." || id.EndsWith('.') || IsWindowsDeviceName(id) ||
            id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ApiSecretStoreException("The API UserSecretsId must be one unconditional, literal identifier containing only letters, digits, dots, hyphens, or underscores.");
        return (Path.GetFullPath(projects[0]), id);
    }

    private static bool IsWindowsDeviceName(string id)
    {
        var name = id.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 && name[3] is >= '1' and <= '9' &&
                (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private (string Id, string Path) ResolveStore(bool production)
    {
        var id = production ? UserSecretsId + ProductionSuffix : UserSecretsId;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new ApiSecretStoreException("The Windows user profile location for encrypted configuration is unavailable.");
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UserSecretsId)));
        return (id, Path.Combine(root, "FullStackLauncher", "api-secrets", scope, production ? "prod.dat" : "local.dat"));
    }

    private string ResolveLegacyPath(bool production)
    {
        var id = production ? UserSecretsId + ProductionSuffix : UserSecretsId;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new ApiSecretStoreException("The Windows user profile location for .NET user-secrets is unavailable.");
        return Path.Combine(root, "Microsoft", "UserSecrets", id, "secrets.json");
    }

    // Metadata only. An inaccessible legacy location is treated conservatively as possibly present,
    // while a valid protected store remains authoritative and readable.
    private bool LegacyStoreExists(bool production)
    {
        try { _ = File.GetAttributes(ResolveLegacyPath(production)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (IsFileError(ex) || ex is ApiSecretStoreException) { return true; }
    }

    private static string ProtectionPurpose(string storeId, bool production) =>
        $"FullStackLauncher.ApiSecrets.v1|{(production ? "Prod" : "Local")}|{storeId}";

    private static byte[] Unprotect(byte[] content, string storeId, bool production)
    {
        if (content.Length <= ProtectedHeader.Length || !content.AsSpan(0, ProtectedHeader.Length).SequenceEqual(ProtectedHeader))
            throw new ApiSecretStoreException("The encrypted configuration file has an unsupported or invalid format. It was preserved; restore a valid encrypted copy before saving.");
        var plaintext = WindowsUserSecretProtection.Unprotect(content[ProtectedHeader.Length..], ProtectionPurpose(storeId, production));
        if (plaintext.Length <= MaximumFileBytes) return plaintext;
        CryptographicOperations.ZeroMemory(plaintext);
        throw new ApiSecretStoreException("The encrypted configuration is too large to read safely. It was preserved.");
    }

    private static void ValidateExpected(bool production, string path, ApiSecretSnapshot expected)
    {
        if (expected is null || expected.IsProduction != production ||
            !string.Equals(expected.StorePath, path, StringComparison.OrdinalIgnoreCase))
            throw new ApiSecretStoreException("This draft belongs to a different configuration store. Reopen configuration before saving.");
    }

    private static FileStream AcquireWriteLock(string path)
    {
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ApiSecretStoreException("The configuration write lock is unavailable. Another Windows session may be saving, or the lock file may be inaccessible. Wait and retry, or check local file access. Nothing was overwritten.", isConflict: true);
        }
    }

    private static byte[]? ReadContents(string path, int maximumBytes = MaximumFileBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes)
                throw new ApiSecretStoreException("The configuration file is too large to edit safely in the launcher.");
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (content.Length + read > maximumBytes)
                    throw new ApiSecretStoreException("The configuration file is too large to edit safely in the launcher.");
                content.Write(buffer, 0, read);
            }
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static ApiSecretSnapshot CreateSnapshot(bool production, string storeId, string path, byte[]? content,
        string? revision, bool hasProtectedStore, bool hasLegacyStore, bool isLegacyImport = false)
    {
        var leaves = content is null ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) :
            Parse(content, "The secret store contains invalid or ambiguous JSON. It has not been changed; repair it before reloading.");
        var values = leaves.ToDictionary(x => x.Key, x => ValueText(x.Value), StringComparer.OrdinalIgnoreCase);
        return new ApiSecretSnapshot(production, storeId, path, revision, values, leaves,
            hasProtectedStore, hasLegacyStore, isLegacyImport);
    }

    private static Dictionary<string, JsonElement> Parse(byte[] content, string error)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ApiSecretStoreException(error);
            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) Visit(property.Name, property.Value);
            return result;

            void Visit(string path, JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
                {
                    foreach (var property in value.EnumerateObject()) Visit(path + ":" + property.Name, property.Value);
                    return;
                }
                if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray()) Visit(path + ":" + (index++).ToString(CultureInfo.InvariantCulture), item);
                    return;
                }
                if (!result.TryAdd(path, value.Clone())) throw new ApiSecretStoreException(error);
            }
        }
        catch (JsonException) { throw new ApiSecretStoreException(error); }
        catch (DecoderFallbackException) { throw new ApiSecretStoreException(error); }
    }

    private static string ValueText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array => string.Empty,
        _ => element.ToString()
    };

    private static byte[] Serialize(Dictionary<string, string> values, ApiSecretSnapshot expected, IReadOnlySet<string>? explicitTextKeys)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var entry in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(entry.Key);
                // Preserve untouched JSON types; explicitly staging text also replaces a null/container with empty text.
                if (explicitTextKeys?.Contains(entry.Key) != true && expected.OriginalLeaves.TryGetValue(entry.Key, out var original) &&
                    string.Equals(ValueText(original), entry.Value, StringComparison.Ordinal))
                    original.WriteTo(writer);
                else
                    writer.WriteStringValue(entry.Value);
            }
            writer.WriteEndObject();
        }
        if (stream.Length > MaximumFileBytes)
            throw new ApiSecretStoreException("The configuration is too large to save safely in the launcher.");
        return stream.ToArray();
    }

    private static string? RevisionOf(byte[]? content) => content is null ? null : Convert.ToHexString(SHA256.HashData(content));

    private static void EnsureUnchanged(string path, ApiSecretSnapshot expected)
    {
        if (!string.Equals(RevisionOf(ReadContents(path, MaximumProtectedFileBytes)), expected.Revision, StringComparison.Ordinal))
            throw new ApiSecretStoreException("The secret store changed outside this editor. Your draft is retained. Reload and review the latest values before saving.", isConflict: true);
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or XmlException or ArgumentException or NotSupportedException or CryptographicException;
}

/// <summary>In-memory secret values and a private revision used to reject stale saves.</summary>
public sealed class ApiSecretSnapshot
{
    public bool IsProduction { get; }
    public string StoreId { get; }
    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlySet<string> NullKeys { get; }
    public bool HasProtectedStore { get; }
    public bool HasLegacyStore { get; }
    public bool IsLegacyImport { get; }
    internal string StorePath { get; }
    internal string? Revision { get; }
    internal IReadOnlyDictionary<string, JsonElement> OriginalLeaves { get; }

    internal ApiSecretSnapshot(bool isProduction, string storeId, string storePath, string? revision,
        Dictionary<string, string> values, Dictionary<string, JsonElement> originalLeaves,
        bool hasProtectedStore, bool hasLegacyStore, bool isLegacyImport)
    {
        IsProduction = isProduction;
        StoreId = storeId;
        StorePath = storePath;
        Revision = revision;
        HasProtectedStore = hasProtectedStore;
        HasLegacyStore = hasLegacyStore;
        IsLegacyImport = isLegacyImport;
        Values = new ReadOnlyDictionary<string, string>(values);
        OriginalLeaves = new ReadOnlyDictionary<string, JsonElement>(originalLeaves);
        NullKeys = originalLeaves.Where(x => x.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array)
            .Select(x => x.Key).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ApiAppSetting(string Key, string Source);

/// <summary>Only fixed, value-free messages may be surfaced from secret store operations.</summary>
public sealed class ApiSecretStoreException : InvalidOperationException
{
    public bool IsConflict { get; }

    public ApiSecretStoreException(string message, bool isConflict = false) : base(message) => IsConflict = isConflict;
}
