using System.Data.Common;
using System.IO;

namespace FullStackLauncher.Services;

/// <summary>
/// Extracts only a safe, short database name from explicit API launch overrides.
/// This describes launch configuration, not a verified database connection.
/// </summary>
internal static class ApiDatabaseIdentifier
{
    public const string ConnectionPrefix = "ConnectionStrings:";
    public const string DefaultConnectionStringKey = "DefaultConnectionString";

    /// <summary>Includes the conventional root setting used by work APIs and standard .NET sections.</summary>
    public static bool IsConnectionKey(string key) =>
        key.Equals(DefaultConnectionStringKey, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(ConnectionPrefix, StringComparison.OrdinalIgnoreCase);

    public static int ConnectionKeyPriority(string key) => key.Equals(DefaultConnectionStringKey, StringComparison.OrdinalIgnoreCase) ? 0
        : key.Equals(ConnectionPrefix + DefaultConnectionStringKey, StringComparison.OrdinalIgnoreCase) ? 1
        : key.Equals(ConnectionPrefix + "DefaultConnection", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    public static string? FromConnectionOverrides(IReadOnlyDictionary<string, string> values)
    {
        var connections = values.Where(pair => IsConnectionKey(pair.Key))
            .Select(pair => pair.Value).ToArray();
        if (connections.Length == 0) return null;

        string? database = null;
        foreach (var value in connections)
        {
            var name = FromConnectionString(value);
            if (name is null) return null;
            if (database is not null && !database.Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
            database = name;
        }
        return database;
    }

    /// <summary>Returns a display name only; SQLite file roots are never editable database identifiers.</summary>
    public static string? FromConnectionString(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var candidates = builder.Keys.Cast<string>().Where(key =>
                key.Equals("Database", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length > 0)
                return candidates.Length == 1 ? SafeName(builder[candidates[0]]?.ToString()) : null;

            return SqliteDisplayName(builder);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unknown provider syntax must not leak a raw connection value into the UI.
            return null;
        }
    }

    private static string? SqliteDisplayName(DbConnectionStringBuilder builder)
    {
        var keys = builder.Keys.Cast<string>().ToArray();
        var sources = keys.Where(key => key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Filename", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sources.Length != 1) return null;

        // Data Source is also a server address in SQL Server connection strings. Limit this
        // fallback to SQLite keywords, then require a file reference or explicit SQLite mode.
        if (keys.Any(key => !SqliteKeywords.Contains(key))) return null;
        var mode = Value(builder, "Mode");
        if (mode is not null && !SqliteModes.Contains(mode)) return null;
        var cache = Value(builder, "Cache");
        if (cache is not null && !SqliteCaches.Contains(cache)) return null;
        var version = Value(builder, "Version");
        if (version is not null && version != "3") return null;

        var source = builder[sources[0]]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(source) || source.Any(char.IsControl)) return null;
        if (source.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return "Memory";

        var fileUri = source.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (fileUri)
        {
            // Strip URI options before decoding so neither query settings nor directories
            // can appear in the service title. This parses text only, never the file itself.
            source = source[5..].Split('?', 2)[0];
            source = Uri.UnescapeDataString(source);
            if (source.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return "Memory";
        }

        var fileName = source.Replace('\\', '/').Split('/')[^1];
        if (fileName.Length == 0 || fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var extension = Path.GetExtension(fileName);
        var recognizableFile = SqliteExtensions.Contains(extension);
        if (!recognizableFile && !fileUri && mode is null && version is null &&
            !sources[0].Equals("Filename", StringComparison.OrdinalIgnoreCase)) return null;
        var root = mode?.Equals("Memory", StringComparison.OrdinalIgnoreCase) == true
            ? fileName : Path.GetFileNameWithoutExtension(fileName);
        return SafeName(root);
    }

    private static string? Value(DbConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;

    private static readonly HashSet<string> SqliteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "DataSource", "Filename", "Mode", "Cache", "Password", "Foreign Keys",
        "Recursive Triggers", "Default Timeout", "Command Timeout", "Pooling", "Vfs", "Version",
        // System.Data.SQLite uses additional file, journaling, encryption and conversion
        // options. They must not prevent display-name discovery or enter the service title.
        "Read Only", "Journal Mode", "Synchronous", "UseUTF16Encoding", "BinaryGUID", "Enlist",
        "FailIfMissing", "Legacy Format", "Page Size", "Max Page Count", "Cache Size", "Max Pool Size",
        "HexPassword", "TextPassword", "TextHexPassword", "DateTimeFormat", "DateTimeKind",
        "DateTimeFormatString", "BaseSchemaName", "Default IsolationLevel", "DefaultDbType",
        "DefaultTypeName", "VfsName", "ZipVfsVersion", "Flags", "SetDefaults", "ToFullPath",
        "NoDefaultFlags", "NoSharedFlags", "DefaultMaximumSleepTime", "BusyTimeout", "WaitTimeout",
        "PrepareRetries", "StepRetries", "ProgressOps"
    };
    private static readonly HashSet<string> SqliteModes = new(StringComparer.OrdinalIgnoreCase)
    { "ReadWriteCreate", "ReadWrite", "ReadOnly", "Memory" };
    private static readonly HashSet<string> SqliteCaches = new(StringComparer.OrdinalIgnoreCase)
    { "Default", "Private", "Shared" };
    private static readonly HashSet<string> SqliteExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".db", ".db3", ".sqlite", ".sqlite3", ".s3db" };

    public static string? SafeName(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            !IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.' or ' ')))
            return null;
        return value;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
