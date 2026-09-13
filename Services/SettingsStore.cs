using System.IO;
using System.Text.Json;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>User-local, validated settings with an atomic save and a previous-version backup.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private bool _blocked;
    private string? _loadedContent;
    private readonly bool _usesDefaultLocation;

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "launcher.settings.json");
    public string SettingsPath { get; }
    public string BaseDirectory => Path.GetDirectoryName(SettingsPath)!;
    public string? LoadWarning { get; private set; }

    public SettingsStore() : this(null) { }

    public SettingsStore(string? settingsPath)
    {
        if (settingsPath != null)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
                throw new ArgumentException("A settings file path is required.", nameof(settingsPath));
            SettingsPath = Path.GetFullPath(settingsPath);
            return;
        }
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("--settings requires the path to a settings JSON file.");
                SettingsPath = Path.GetFullPath(args[i + 1]);
                return;
            }
            if (args[i].StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var path = args[i]["--settings=".Length..];
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("--settings requires the path to a settings JSON file.");
                SettingsPath = Path.GetFullPath(path);
                return;
            }
        }

        SettingsPath = DefaultSettingsPath;
        _usesDefaultLocation = true;
    }

    public LauncherSettings Load()
    {
        _blocked = false;
        LoadWarning = null;
        _loadedContent = null;
        try
        {
            if (_usesDefaultLocation && !File.Exists(SettingsPath)) InitializeDefaultSettings();
            if (!File.Exists(SettingsPath)) return new LauncherSettings();
            var content = File.ReadAllText(SettingsPath);
            var settings = ParseAndValidate(content);
            _loadedContent = content;
            if (!settings.Projects.Any(p => p.Id == settings.SelectedProjectId && !p.IsArchived))
                settings.SelectedProjectId = settings.Projects.FirstOrDefault(p => !p.IsArchived)?.Id;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _blocked = true;
            var backupMessage = File.Exists(SettingsPath) ? BackupInvalidSettings() : "No existing settings were overwritten.";
            LoadWarning = $"Cannot load settings at {SettingsPath}: {ex.Message}\n\n" +
                $"The original file has been preserved. {backupMessage} " +
                "Saving is disabled to protect it. Fix the JSON or choose another file with --settings, then restart the launcher.";
            return new LauncherSettings();
        }
    }

    private void InitializeDefaultSettings()
    {
        // A fresh installation always starts with the user's own empty library.
        // Never import profiles or commands from the checkout, adjacent files,
        // or resources embedded in an older build.
        var settings = new LauncherSettings
        {
            SelectedProjectId = null,
            Projects = [],
            DeveloperTools = []
        };

        Validate(settings, BaseDirectory);
        Directory.CreateDirectory(BaseDirectory);
        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine);
            try { File.Move(temporaryPath, SettingsPath, overwrite: false); }
            catch (IOException) when (File.Exists(SettingsPath))
            {
                // Another launcher initialized the library first. Load its saved
                // version below instead of replacing it with our initial copy.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Save(LauncherSettings settings)
    {
        if (_blocked)
            throw new InvalidOperationException(LoadWarning ?? "Saving is disabled because the settings file could not be loaded.");
        Validate(settings, BaseDirectory);
        if (File.Exists(SettingsPath))
        {
            var currentContent = File.ReadAllText(SettingsPath);
            try { ParseAndValidate(currentContent); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                _blocked = true;
                var backupMessage = BackupInvalidSettings();
                LoadWarning = $"The settings file is now invalid: {ex.Message} {backupMessage} Fix the file and restart before saving.";
                throw new InvalidOperationException(LoadWarning, ex);
            }
            if (_loadedContent != null && currentContent != _loadedContent)
                throw new InvalidOperationException("The settings file changed outside this launcher. Restart to load those changes before saving.");
        }

        Directory.CreateDirectory(BaseDirectory);
        var content = JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine;
        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            if (File.Exists(SettingsPath)) File.Replace(temporaryPath, SettingsPath, SettingsPath + ".bak");
            else File.Move(temporaryPath, SettingsPath);
            _loadedContent = content;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public string ResolveRoot(ProjectProfile project) => Path.GetFullPath(project.RootPath, BaseDirectory);

    public string ResolveWorkingDirectory(ProjectProfile project, ServiceProfile service) =>
        Path.GetFullPath(service.WorkingDirectory, ResolveRoot(project));

    private string BackupInvalidSettings()
    {
        var backup = SettingsPath + ".invalid." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".bak";
        try
        {
            File.Copy(SettingsPath, backup, false);
            return $"A recovery copy is at {backup}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"An additional recovery copy could not be created: {ex.Message}";
        }
    }

    private LauncherSettings ParseAndValidate(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        var root = document.RootElement;
        RequireJsonProperty(root, "version", JsonValueKind.Number, "Settings");
        var projects = RequireJsonProperty(root, "projects", JsonValueKind.Array, "Settings");
        // Optional for version 1 compatibility; old profiles gain the automatic pgAdmin shortcut.
        if (root.EnumerateObject().Any(p => p.Name.Equals("developerTools", StringComparison.OrdinalIgnoreCase)))
        {
            var developerTools = RequireJsonProperty(root, "developerTools", JsonValueKind.Array, "Settings");
            foreach (var tool in developerTools.EnumerateArray())
                foreach (var field in new[] { "id", "name", "kind", "target" })
                    RequireJsonProperty(tool, field, JsonValueKind.String, "Developer tool");
        }
        var projectIndex = 0;
        foreach (var project in projects.EnumerateArray())
        {
            var label = $"Project {++projectIndex}";
            foreach (var field in new[] { "id", "name", "rootPath" })
                RequireJsonProperty(project, field, JsonValueKind.String, label);
            var services = RequireJsonProperty(project, "services", JsonValueKind.Array, label);
            var serviceIndex = 0;
            foreach (var service in services.EnumerateArray())
            {
                var serviceLabel = $"{label}, service {++serviceIndex}";
                foreach (var field in new[] { "id", "name", "kind", "workingDirectory", "startCommand" })
                    RequireJsonProperty(service, field, JsonValueKind.String, serviceLabel);
                var kind = RequireJsonProperty(service, "kind", JsonValueKind.String, serviceLabel).GetString();
                foreach (var field in new[] { "url", "uiPath" })
                    if (!string.Equals(kind, "Console", StringComparison.OrdinalIgnoreCase) ||
                        service.EnumerateObject().Any(property => property.Name.Equals(field, StringComparison.OrdinalIgnoreCase)))
                        RequireJsonProperty(service, field, JsonValueKind.String, serviceLabel);
            }
        }
        var settings = JsonSerializer.Deserialize<LauncherSettings>(content, JsonOptions)
            ?? throw new ArgumentException("The settings document is empty.");
        Validate(settings, BaseDirectory);
        return settings;
    }

    private static JsonElement RequireJsonProperty(JsonElement element, string name, JsonValueKind kind, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{label} must be a JSON object.");
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != kind)
                throw new ArgumentException($"{label}: '{name}' must be a JSON {kind.ToString().ToLowerInvariant()}.");
            return property.Value;
        }
        throw new ArgumentException($"{label} is missing the required '{name}' field.");
    }

    internal static void Validate(LauncherSettings settings, string settingsDirectory)
    {
        if (settings.Version != 1) throw new ArgumentException($"Unsupported settings version {settings.Version}; expected 1.");
        if (settings.Projects == null) throw new ArgumentException("The projects field must be an array.");
        if (settings.DeveloperTools == null) throw new ArgumentException("The developerTools field must be an array.");
        // Older settings files have no layout. Keep malformed dimensions from making a panel unreachable.
        settings.Layout ??= new();
        if (!double.IsFinite(settings.Layout.ConsoleShare) || settings.Layout.ConsoleShare <= 0 || settings.Layout.ConsoleShare >= 1)
            settings.Layout.ConsoleShare = 0.47;
        if (!double.IsFinite(settings.Layout.ServicesHeightShare) || settings.Layout.ServicesHeightShare <= 0 || settings.Layout.ServicesHeightShare >= 1)
            settings.Layout.ServicesHeightShare = 0.35;
        var toolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in settings.DeveloperTools)
        {
            if (tool is null) throw new ArgumentException("A developer tool cannot be null.");
            Require(tool.Id, "Developer tool ID");
            if (!toolIds.Add(tool.Id)) throw new ArgumentException($"Duplicate developer tool ID: {tool.Id}.");
            DeveloperToolLauncher.Validate(tool, settingsDirectory);
            tool.Kind = tool.Kind.ToLowerInvariant() switch
            {
                "pgadmin" => "PgAdmin", "website" => "Website", _ => "Application"
            };
        }
        var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceFolders = new List<(string Path, string Label)>();
        foreach (var project in settings.Projects)
        {
            if (project == null) throw new ArgumentException("A project entry cannot be null.");
            Require(project.Id, "Project ID");
            Require(project.Name, "Project name");
            if (!projectIds.Add(project.Id)) throw new ArgumentException($"Duplicate project ID: {project.Id}.");
            Require(project.RootPath, $"Root folder for '{project.Name}'");
            ValidatePath(project.RootPath, $"Root folder for '{project.Name}'");
            if (project.Database is { } database)
            {
                Require(database.SourceId, $"Database connection source for '{project.Name}'");
                Require(database.DatabaseName, $"Database name for '{project.Name}'");
            }
            if (project.Services == null) throw new ArgumentException($"Services for '{project.Name}' must be an array.");
            foreach (var service in project.Services)
            {
                if (service == null) throw new ArgumentException($"'{project.Name}' contains a null service.");
                Require(service.Id, $"Service ID in '{project.Name}'");
                Require(service.Name, $"Service name in '{project.Name}'");
                if (!serviceIds.Add(service.Id)) throw new ArgumentException($"Duplicate service ID: {service.Id}.");
                var label = $"'{project.Name}' / '{service.Name}'";
                Require(service.Kind, $"Service kind for {label}");
                Require(service.WorkingDirectory, $"Working folder for {label}");
                ValidatePath(service.WorkingDirectory, $"Working folder for {label}");
                var projectRoot = Path.GetFullPath(project.RootPath, settingsDirectory);
                var serviceFolder = Path.GetFullPath(service.WorkingDirectory, projectRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var existing in serviceFolders)
                {
                    if (serviceFolder.Equals(existing.Path, StringComparison.OrdinalIgnoreCase) ||
                        serviceFolder.StartsWith(existing.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        existing.Path.StartsWith(serviceFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"The working folders for {label} and {existing.Label} overlap. " +
                            "Choose separate service folders, with neither inside the other, across all saved projects. " +
                            "This is required to identify and stop each service's processes reliably.");
                }
                serviceFolders.Add((serviceFolder, label));
                Require(service.StartCommand, $"Start command for {label}");
                if (service.IsConsole)
                {
                    service.Kind = "Console";
                    service.Url = "";
                    service.UiPath = "";
                    if (service.ApiConfiguration is not null)
                        throw new ArgumentException($"Console app {label} cannot use API configuration. Use its start command and arguments instead.");
                }
                else if (!IsLocalUrl(service.Url))
                    throw new ArgumentException($"URL for {label} must be an http:// or https:// loopback URL (localhost, 127.0.0.1, or [::1]).");
                if (!service.IsConsole && (string.IsNullOrWhiteSpace(service.UiPath) || !service.UiPath.StartsWith('/') || service.UiPath.StartsWith("//") || service.UiPath.Contains('\\')))
                    throw new ArgumentException($"UI path for {label} must begin with one slash, such as / or /swagger.");
                service.CleanCommand ??= "";
                service.SetupCommand ??= "";
                if (new[] { service.StartCommand, service.CleanCommand, service.SetupCommand }
                    .Any(SensitiveDataProtection.ContainsLiteralCredential))
                    throw new ArgumentException("A service command appears to contain a credential. Use encrypted API configuration or an external credential provider instead of saving secrets in commands.");
                if (SensitiveDataProtection.ContainsLiteralCredential(service.Url) ||
                    SensitiveDataProtection.ContainsLiteralCredential(service.UiPath))
                    throw new ArgumentException("A service address appears to contain a credential. Use a sign-in flow or an external credential provider instead of storing secrets in URLs.");
                if (service.ApiConfiguration is { } configuration && configuration.Environment is not ("Local" or "Prod"))
                    throw new ArgumentException($"API configuration for {label} must be Local or Prod.");
            }
        }
    }

    internal static bool IsLocalUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);

    private static void Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} is required.");
    }

    private static void ValidatePath(string path, string label)
    {
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException($"{label} contains invalid path characters.");
        try { Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException($"{label} is invalid: {ex.Message}", ex); }
    }
}
