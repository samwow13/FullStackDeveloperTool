using System.Collections;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Reads configured service folders and referenced Local/Prod secrets without changing API configuration.</summary>
public static class DatabaseConnectionDiscovery
{
    public static DatabaseDiscovery Discover(IEnumerable<ProjectProfile> projects, SettingsStore store)
    {
        var sources = new List<DatabaseConnectionSource>();
        var warnings = new List<string>();
        var environment = ReadEnvironment();
        foreach (var project in projects)
        foreach (var service in project.Services)
        {
            try
            {
                var folder = store.ResolveWorkingDirectory(project, service);
                if (!Directory.Exists(folder)) continue;
                var projectFiles = Directory.GetFiles(folder, "*.csproj");
                if (projectFiles.Length == 0) continue;
                foreach (var projectFile in projectFiles)
                {
                    var selection = service.ApiConfiguration;
                    var prefix = $"{service.Id}/{Path.GetFileName(projectFile)}";
                    var label = $"{project.Name} / {service.Name}";
                    var local = ReadProfile(false);
                    var production = ReadProfile(true);
                    if (selection is null && local is not null)
                    {
                        // Retain the legacy reference ID, but do not equate discovery with a running process's configuration.
                        var effective = new Dictionary<string, (string Value, string Origin)>(local, StringComparer.OrdinalIgnoreCase);
                        foreach (var item in environment) effective[item.Key] = (item.Value, "inherited environment");
                        AddSources(effective, prefix, $"{label} · Unmanaged discovery");
                        warnings.Add("Unmanaged discovery combines saved sources with inherited connection overrides. It does not identify a running API's configuration: ordinary dotnet commands still use their external .NET settings. Select Local or Prod on the dashboard to apply encrypted launcher overrides.");
                    }
                    else if (selection is not null)
                    {
                        // Managed launches strip inherited connection overrides. Keep the Prod suffix so a saved
                        // Local database selection never silently changes into a production connection on switch.
                        if (selection.Environment == "Local") AddSources(local, prefix, $"{label} · Local selected");
                        else if (selection.Environment == "Prod") AddSources(production, prefix, $"{label} · Prod selected", "/Prod");
                        else warnings.Add($"Choose a valid Local or Prod configuration for {service.Name}.");
                        warnings.Add("Database sources show saved API configuration; a running API may still require a restart. Database selection here does not change API settings.");
                    }
                    // Fixed profile references expose both configurations without switching the running API.
                    AddSources(local, $"{prefix}/local", $"{label} · Local configuration");
                    AddSources(production, $"{prefix}/prod", $"{label} · Prod configuration");

                    Dictionary<string, (string Value, string Origin)>? ReadProfile(bool isProduction)
                    {
                        try { return ReadProfileConnections(folder, projectFile, selection is not null, isProduction, projectFiles.Length == 1); }
                        catch (Exception ex) when (IsConfigurationError(ex))
                        {
                            warnings.Add($"Could not read {(isProduction ? "Prod" : "Local")} connection settings for {service.Name}.");
                            return null;
                        }
                    }

                    void AddSources(Dictionary<string, (string Value, string Origin)>? values, string sourcePrefix,
                        string sourceLabel, string suffix = "")
                    {
                        if (values is null) return;
                        foreach (var item in values)
                        {
                            try
                            {
                                sources.Add(new($"{sourcePrefix}/{item.Key.ToUpperInvariant()}{suffix}", project.Id,
                                    $"{sourceLabel} · {item.Key} ({item.Value.Origin})", item.Value.Value));
                            }
                            catch (ArgumentException) { /* Other database providers are not PostgreSQL sources. */ }
                        }
                    }
                }
            }
            catch (Exception ex) when (IsConfigurationError(ex))
            {
                // Configuration content and parser messages can contain passwords. Show only the source label.
                warnings.Add($"Could not read connection settings for {service.Name}.");
            }
        }
        // Environment connections are also available when there are no .NET projects.
        foreach (var item in environment)
        {
            try { sources.Add(new($"environment/{item.Key.ToUpperInvariant()}", "", $"Environment · {item.Key}", item.Value)); }
            catch (ArgumentException) { }
        }
        var saved = new SavedDatabaseConnectionStore(store).Load();
        sources.AddRange(saved.Sources);
        if (!string.IsNullOrWhiteSpace(saved.Notice)) warnings.Add(saved.Notice);
        return new(sources, string.Join(" ", warnings.Distinct()));
    }

    private static bool IsConfigurationError(Exception exception) => exception is IOException or UnauthorizedAccessException or
        JsonException or System.Xml.XmlException or ArgumentException or ApiSecretStoreException or System.Security.SecurityException;

    private static Dictionary<string, (string Value, string Origin)> ReadProfileConnections(string folder, string projectFile,
        bool managed, bool production, bool singleProject)
    {
        var values = new Dictionary<string, (string Value, string Origin)>(StringComparer.OrdinalIgnoreCase);
        ReadConnections(Path.Combine(folder, "appsettings.json"), "appsettings.json", values);
        var settingsFile = production ? "appsettings.Production.json" : "appsettings.Development.json";
        ReadConnections(Path.Combine(folder, settingsFile), settingsFile, values);
        var profileName = production ? "Prod" : "Local";
        if (singleProject)
        {
            ApiSecretStore? secretStore = null;
            try { secretStore = new ApiSecretStore(folder); }
            catch (ApiSecretStoreException) when (!managed) { /* Generic projects can retain their existing discovery. */ }
            if (secretStore is not null)
            {
                // A protected store is authoritative even when empty. Never fall back after unlock/parse failure.
                var snapshot = secretStore.Load(production);
                if (managed || snapshot.HasProtectedStore)
                {
                    ApplySecrets(snapshot);
                    return values;
                }
            }
        }
        else if (managed) throw new ApiSecretStoreException("Managed API configuration requires one project file.");

        var secretsId = XDocument.Load(projectFile).Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "UserSecretsId")?.Value.Trim();
        if (string.IsNullOrEmpty(secretsId)) return values;
        // A project file is data, not a request to evaluate MSBuild or an arbitrary path.
        if (secretsId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || secretsId is "." or "..")
            throw new InvalidDataException("Invalid user-secrets reference.");
        if (!production)
        {
            var secretFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", secretsId, "secrets.json");
            ReadConnections(secretFile, "Local legacy plaintext .NET user-secrets", values);
        }
        else if (singleProject)
        {
            // Unmanaged legacy profiles remain readable until the user explicitly saves protected configuration.
            var secretFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", secretsId + "-launcher-production", "secrets.json");
            ReadConnections(secretFile, "Prod legacy plaintext .NET user-secrets", values);
        }
        return values;

        void ApplySecrets(ApiSecretSnapshot snapshot)
        {
            foreach (var entry in snapshot.Values)
                if (entry.Key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
                {
                    var key = entry.Key["ConnectionStrings:".Length..];
                    if (snapshot.NullKeys.Contains(entry.Key) || string.IsNullOrWhiteSpace(entry.Value)) values.Remove(key);
                    else values[key] = (entry.Value, $"{profileName} encrypted launcher configuration");
                }
        }
    }

    private static Dictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            var key = item.Key.ToString()!;
            if (key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) &&
                item.Value is string value && !string.IsNullOrWhiteSpace(value))
                result[key["ConnectionStrings__".Length..]] = value;
        }
        return result;
    }

    private static void ReadConnections(string path, string origin, Dictionary<string, (string Value, string Origin)> values)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                foreach (var connection in property.Value.EnumerateObject()) Set(connection.Name, connection.Value);
            else if (property.Name.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
                Set(property.Name["ConnectionStrings:".Length..], property.Value);
        }
        void Set(string key, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) values[key] = (element.GetString()!, origin);
            else if (element.ValueKind == JsonValueKind.Null) values.Remove(key);
        }
    }
}
