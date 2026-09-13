using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>A validated, in-memory launch snapshot. It is never serialized into launcher settings.</summary>
public sealed class ApiLaunchConfiguration
{
    private readonly string[] _redactions;

    private ApiLaunchConfiguration(ProcessStartInfo startInfo, string environment, string fingerprint,
        IEnumerable<string> redactions)
    {
        StartInfo = startInfo;
        Environment = environment;
        Fingerprint = fingerprint;
        _redactions = redactions.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray();
    }

    public ProcessStartInfo StartInfo { get; }
    public string Environment { get; }
    // Only used to compare prepared launches in memory; never display or persist this value.
    public string Fingerprint { get; }

    /// <summary>Read and validate everything before the caller stops an existing API process.</summary>
    public static ApiLaunchConfiguration Prepare(ServiceProfile profile, string directory, string environment)
    {
        if (environment is not ("Local" or "Prod"))
            throw new InvalidOperationException("Choose Local or Prod API configuration.");
        if (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("Set a loopback HTTP or HTTPS API URL in project settings.");

        var store = new ApiSecretStore(directory);
        var selected = store.Load(environment == "Prod");
        if (!selected.HasProtectedStore && selected.HasLegacyStore)
            throw new InvalidOperationException("Import and save this profile's existing .NET user-secrets in API configuration before using Local or Prod. Direct dotnet commands continue using the original files.");
        if (selected.NullKeys.Count > 0)
            throw new InvalidOperationException("This profile contains JSON null or container overrides that cannot be passed through the process environment. Remove them or save text values before starting the API.");
        ApiSecretSnapshot? inactive = null;
        try { inactive = store.Load(environment != "Prod"); }
        catch (ApiSecretStoreException) { /* An invalid inactive store must not prevent recovery to a valid profile. */ }
        var local = environment == "Local" ? selected : inactive;
        if (environment == "Prod")
        {
            if (selected.Values.Count == 0)
                throw new InvalidOperationException("Add and save Prod configuration values before switching to Prod.");
            // A saved local database connection must never become a production fallback.
            // Each production connection is an explicit choice, even when its name matches Local.
            foreach (var key in (local?.Values.Keys ?? []).Where(key =>
                         key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)))
            {
                if (!selected.Values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("Save a non-empty Prod value for every connection string configured in Local before switching to Prod.");
            }
        }

        var configuredKeys = selected.Values.Keys.Concat(inactive?.Values.Keys ?? [])
            .Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in selected.Values)
        {
            ApiSecretStore.ValidateKey(value.Key);
            if (value.Value.Contains('\0'))
                throw new InvalidOperationException("A configuration value contains a null character and cannot be passed to the API. Edit that value before starting.");
        }
        var npgsqlMajorVersion = DatabaseConnectionSecurity.ReadNpgsqlMajorVersion(store.ProjectFilePath);
        var configurationValues = selected.Values.ToDictionary(pair => pair.Key,
            pair => pair.Key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                ? DatabaseConnectionSecurity.NormalizeApiConnectionString(pair.Value, npgsqlMajorVersion) : pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetFullPath(directory),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        // Disable the standard Development user-secrets provider only in this managed build.
        // Isolated artifacts preserve ordinary dotnet/EF builds and their original secrets ID.
        // Otherwise deleting an encrypted override could silently resurrect its plaintext value.
        var runtimeRoot = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(runtimeRoot))
            throw new InvalidOperationException("The Windows local application data folder is unavailable. The API cannot start with isolated configuration.");
        var runtimeScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.ProjectFilePath.ToUpperInvariant())));
        var artifactsPath = Path.Combine(runtimeRoot, "FullStackLauncher", "api-runtime", runtimeScope);
        foreach (var argument in new[] { "run", "--project", store.ProjectFilePath,
                     "--no-launch-profile", "--artifacts-path", artifactsPath,
                     "--property:GenerateUserSecretsAttribute=false", "--property:UserSecretsId=",
                     "--", "--urls", origin })
            startInfo.ArgumentList.Add(argument);

        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (IsInheritedConfiguration(key) || configuredKeys.Contains(NormalizeKey(key)))
                startInfo.Environment.Remove(key);
        }
        foreach (var value in configurationValues)
        {
            startInfo.Environment[value.Key.Replace(":", "__", StringComparison.Ordinal)] = value.Value;
        }
        var hostEnvironment = environment == "Prod" ? "Production" : "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = hostEnvironment;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = hostEnvironment;
        startInfo.Environment["ASPNETCORE_URLS"] = origin;

        // The fingerprint contains no exposed value; secrets themselves stay solely in memory.
        var fingerprintContent = JsonSerializer.Serialize(new
        {
            Project = store.ProjectFilePath,
            Environment = environment,
            Origin = origin,
            Values = configurationValues.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Key = pair.Key.ToUpperInvariant(), pair.Value }),
            NullKeys = selected.NullKeys.Order(StringComparer.OrdinalIgnoreCase)
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintContent)));
        return new ApiLaunchConfiguration(startInfo, environment, fingerprint,
            FindRedactions(selected.Values).Concat(FindRedactions(configurationValues))
                .Concat(inactive is null ? [] : FindRedactions(inactive.Values)));
    }

    public string Redact(string text)
    {
        if (_redactions.Length == 0 || text.Length == 0) return text;
        var result = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var next = -1;
            var length = 0;
            foreach (var value in _redactions)
            {
                var found = text.IndexOf(value, position, StringComparison.Ordinal);
                if (found < 0 || (next >= 0 && found >= next)) continue;
                next = found;
                length = value.Length;
            }
            if (next < 0)
            {
                result.Append(text, position, text.Length - position);
                break;
            }
            result.Append(text, position, next - position);
            result.Append("[redacted]");
            position = next + length;
        }
        return result.ToString();
    }

    private static string NormalizeKey(string key) => key.Replace("__", ":", StringComparison.Ordinal);

    private static bool IsInheritedConfiguration(string key) =>
        key.Contains(':') || key.Contains("__", StringComparison.Ordinal) ||
        key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
        new[] { "CUSTOMCONNSTR_", "SQLCONNSTR_", "SQLAZURECONNSTR_", "MYSQLCONNSTR_" }
            .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
        new[] { "ENVIRONMENT", "URLS", "HTTP_PORTS", "HTTPS_PORTS", "CONTENTROOT", "WEBROOT",
                "APPLICATIONNAME", "USERSECRETSID", "DOTNET_ENVIRONMENT", "DOTNET_URLS", "DOTNET_HTTP_PORTS",
                "DOTNET_HTTPS_PORTS", "DOTNET_CONTENTROOT", "DOTNET_WEBROOT", "DOTNET_APPLICATIONNAME",
                "DOTNET_USER_SECRETS_ID", "DOTNET_LAUNCH_PROFILE" }
            .Contains(key, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindRedactions(IReadOnlyDictionary<string, string> values)
    {
        var redactions = new List<string>();
        foreach (var pair in values)
        {
            if (string.IsNullOrEmpty(pair.Value)) continue;
            AddRedactionVariants(redactions, pair.Value);
            if (!pair.Key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var connection = new DbConnectionStringBuilder { ConnectionString = pair.Value };
                foreach (string key in connection.Keys)
                {
                    if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("Pwd", StringComparison.OrdinalIgnoreCase) ||
                        key.Replace(" ", "", StringComparison.Ordinal).Equals("SslPassword", StringComparison.OrdinalIgnoreCase))
                    {
                        var password = connection[key]?.ToString();
                        if (!string.IsNullOrEmpty(password)) AddRedactionVariants(redactions, password);
                    }
                }
            }
            catch (ArgumentException) { /* An arbitrary connection value is already redacted as a whole. */ }
        }
        return redactions;
    }

    private static void AddRedactionVariants(List<string> redactions, string value)
    {
        var terminalText = ServiceRunner.StripTerminalCodes(value);
        foreach (var variant in new[] { value, terminalText }
                     .Concat(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                     .Concat(terminalText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
        {
            redactions.Add(variant);
            var escaped = JsonSerializer.Serialize(variant);
            redactions.Add(escaped[1..^1]);
            // Some structured loggers use JSON's less aggressive escaping for quotes and Unicode.
            var relaxed = JsonSerializer.Serialize(variant, new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            redactions.Add(relaxed[1..^1]);
        }
    }
}
