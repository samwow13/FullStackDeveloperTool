using System.Data.Common;

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
            if (!TryReadSafeName(value, out var name)) return null;
            if (database is not null && !database.Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
            database = name;
        }
        return database;
    }

    private static bool TryReadSafeName(string connectionString, out string name)
    {
        name = "";
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var candidates = builder.Keys.Cast<string>().Where(key =>
                key.Equals("Database", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) return false;
            name = SafeName(builder[candidates[0]]?.ToString()) ?? "";
            return name.Length > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unknown provider syntax must not leak a raw connection value into the UI.
            return false;
        }
    }

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
