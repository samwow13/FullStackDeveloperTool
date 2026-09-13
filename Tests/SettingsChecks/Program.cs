using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

if (args.Contains("--probe-settings"))
{
    Console.WriteLine(new SettingsStore().SettingsPath);
    return 0;
}

var checks = new (string Name, Action Check)[]
{
    ("missing file creates a settings document and parent folders", MissingFile),
    ("multiple projects and services round-trip without losing commands", RoundTrip),
    ("relative and absolute paths resolve from the settings directory", PathResolution),
    ("command-line settings paths preserve spaces and quotes", CommandLinePaths),
    ("atomic replacement preserves the previous settings backup", Backup),
    ("malformed JSON is preserved with recovery copy and blocked save", CorruptJson),
    ("malformed schema is rejected instead of filling default values", MalformedSchema),
    ("external valid edits prevent stale saves", ExternalEditConflict),
    ("external corruption is protected when saving", ExternalCorruption),
    ("equal and nested service folders are rejected across projects", OverlappingFolders),
    ("overlapping folders in a loaded document cannot be overwritten", LoadedOverlap),
    ("loopback URLs and safe UI paths are enforced", LocalUrls),
    ("missing selected project falls back to the first saved project", SelectedProjectFallback),
    ("legacy settings add pgAdmin without changing saved projects", LegacyDeveloperTools),
    ("application website and pgAdmin shortcuts round-trip with spaces", DeveloperToolsRoundTrip),
    ("an explicitly empty tools list remains empty", EmptyDeveloperTools),
    ("malformed tools are preserved with blocked saving", MalformedDeveloperTools),
    ("duplicate tool IDs and credential URLs are rejected without writes", InvalidDeveloperTools)
};
var failures = 0;
foreach (var check in checks)
{
    try { check.Check(); Console.WriteLine($"PASS {check.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {check.Name}: {ex}"); }
}
Console.WriteLine($"Settings checks: {checks.Length - failures}/{checks.Length} passed.");
return failures == 0 ? 0 : 1;

static void MissingFile()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.Root, "new folder", "saved profiles.json");
    var store = new SettingsStore(path);
    var settings = store.Load();
    Equal(0, settings.Projects.Count);
    Equal<string?>(null, store.LoadWarning);
    Check(!File.Exists(path), "Loading a missing file must not write it.");
    store.Save(settings);
    Check(File.Exists(path), "Saving should create the file and its parent directory.");
    Equal(0, new SettingsStore(path).Load().Projects.Count);
    Check(!File.Exists(path + ".bak"), "An initial save has no previous version to back up.");
}

static void RoundTrip()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    var original = Sample();
    original.Projects[0].Services[0].StartCommand = "dotnet run --project \"API with spaces.csproj\" -- --label \"My CRM\"";
    original.Projects[1].Services[1].StartCommand = "\"C:\\Program Files\\nodejs\\npm.cmd\" start -- --port 4400";
    store.Save(original);
    var loadedStore = fixture.Store();
    var loaded = loadedStore.Load();
    Equal<string?>(null, loadedStore.LoadWarning);
    Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(loaded));
    Equal(2, loaded.Projects.Count);
    Equal(4, loaded.Projects.Sum(p => p.Services.Count));
    Equal("project-b", loaded.SelectedProjectId);
}

static void PathResolution()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    var profile = new ProjectProfile { RootPath = Path.Combine("..", "My project") };
    var service = new ServiceProfile { WorkingDirectory = Path.Combine("Backend API", "src", "..") };
    var expectedRoot = Path.GetFullPath(Path.Combine(fixture.Root, "..", "My project"));
    Equal(expectedRoot, store.ResolveRoot(profile));
    Equal(Path.Combine(expectedRoot, "Backend API"), store.ResolveWorkingDirectory(profile, service).TrimEnd(Path.DirectorySeparatorChar));
    profile.RootPath = Path.Combine(fixture.Root, "absolute project");
    Equal(profile.RootPath, store.ResolveRoot(profile));
    service.WorkingDirectory = Path.Combine(fixture.Root, "another absolute service");
    Equal(service.WorkingDirectory, store.ResolveWorkingDirectory(profile, service));
    // Settings keep their relative spelling so moving the whole layout changes the base naturally.
    var settings = Sample();
    store.Load();
    store.Save(settings);
    Equal(Path.Combine("..", "Project A"), fixture.Store().Load().Projects[0].RootPath);
}

static void CommandLinePaths()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.Root, "folder with spaces", "my saved projects.json");
    Equal(path, ProbeSettings(fixture.Root, "--settings", path));
    Equal(path, ProbeSettings(fixture.Root, "--settings=" + path));
    Equal(path, ProbeSettings(fixture.Root, "--settings", Path.GetRelativePath(fixture.Root, path)));
    Check(!File.Exists(path), "Resolving command-line settings paths must not create a file.");
}

static string ProbeSettings(string workingDirectory, params string[] arguments)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No current executable path.");
    var start = new ProcessStartInfo(executable)
    {
        WorkingDirectory = workingDirectory, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    start.ArgumentList.Add("--probe-settings");
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Probe process failed to start.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    Check(process.WaitForExit(15_000), "Settings-path probe timed out.");
    Equal(0, process.ExitCode, error);
    return output.Trim();
}

static void Backup()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    var settings = Sample();
    store.Save(settings);
    var firstBytes = File.ReadAllBytes(store.SettingsPath);
    settings.Projects[0].Name = "Renamed project";
    store.Save(settings);
    Check(firstBytes.SequenceEqual(File.ReadAllBytes(store.SettingsPath + ".bak")), "Backup must contain the exact previous file.");
    Equal("Renamed project", fixture.Store().Load().Projects[0].Name);
    Equal(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
}

static void CorruptJson()
{
    using var fixture = new Fixture();
    var content = "{ \"version\": 1, \"projects\": [ BROKEN }";
    File.WriteAllText(fixture.Path, content);
    var store = fixture.Store();
    Equal(0, store.Load().Projects.Count);
    Check(store.LoadWarning?.Contains("preserved", StringComparison.OrdinalIgnoreCase) == true, "Load must explain preservation.");
    Equal(content, File.ReadAllText(fixture.Path));
    var backups = Directory.GetFiles(fixture.Root, "settings.json.invalid.*.bak");
    Equal(1, backups.Length);
    Equal(content, File.ReadAllText(backups[0]));
    Throws<InvalidOperationException>(() => store.Save(Sample()), "Saving is disabled");
    Equal(content, File.ReadAllText(fixture.Path));
}

static void MalformedSchema()
{
    using var fixture = new Fixture();
    foreach (var (json, message) in new[]
    {
        ("{}", "version"),
        ("{\"version\":1,\"projects\":null}", "array"),
        ("{\"version\":1,\"projects\":[{}]}", "id"),
        ("{\"version\":8,\"projects\":[]}", "Unsupported settings version"),
        ("{\"version\":1,\"projects\":[null]}", "JSON object")
    })
    {
        File.WriteAllText(fixture.Path, json);
        var store = fixture.Store();
        store.Load();
        Check(store.LoadWarning?.Contains(message, StringComparison.OrdinalIgnoreCase) == true, $"Warning should mention '{message}'.");
        Throws<InvalidOperationException>(() => store.Save(new LauncherSettings()));
        Equal(json, File.ReadAllText(fixture.Path));
    }
}

static void ExternalEditConflict()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    var settings = Sample();
    store.Save(settings);
    var external = fixture.Store();
    var externalSettings = external.Load();
    externalSettings.Projects[0].Name = "Saved by another instance";
    external.Save(externalSettings);
    var externalContent = File.ReadAllText(fixture.Path);
    settings.Projects[0].Name = "Stale local edit";
    Throws<InvalidOperationException>(() => store.Save(settings), "changed outside");
    Equal(externalContent, File.ReadAllText(fixture.Path));
}

static void ExternalCorruption()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    var settings = Sample();
    store.Save(settings);
    File.WriteAllText(fixture.Path, "corrupt external edit");
    Throws<InvalidOperationException>(() => store.Save(settings), "now invalid");
    Check(store.LoadWarning != null, "The store should retain the reason saves were blocked.");
    Equal("corrupt external edit", File.ReadAllText(fixture.Path));
    Equal(1, Directory.GetFiles(fixture.Root, "settings.json.invalid.*.bak").Length);
    Throws<InvalidOperationException>(() => store.Save(settings));
}

static void OverlappingFolders()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    foreach (var secondFolder in new[] { "api", "API", Path.Combine("api", "child"), Path.Combine("api", "..", "api"), "." })
    {
        var settings = Sample();
        settings.Projects[0].Services[1].WorkingDirectory = secondFolder;
        Throws<ArgumentException>(() => store.Save(settings), "overlap");
    }
    var crossProject = Sample();
    crossProject.Projects[1].RootPath = Path.Combine("..", "Project A", "api");
    Throws<ArgumentException>(() => store.Save(crossProject), "across all saved projects");
    var siblings = Sample();
    siblings.Projects[0].Services[1].WorkingDirectory = "api-other";
    store.Save(siblings);
    var reloaded = fixture.Store();
    Equal(2, reloaded.Load().Projects.Count);
    Equal<string?>(null, reloaded.LoadWarning);
}

static void LoadedOverlap()
{
    using var fixture = new Fixture();
    var settings = Sample();
    settings.Projects[1].RootPath = settings.Projects[0].RootPath;
    var original = JsonSerializer.Serialize(settings);
    File.WriteAllText(fixture.Path, original);
    var store = fixture.Store();
    Equal(0, store.Load().Projects.Count);
    Check(store.LoadWarning?.Contains("overlap") == true, "Loaded overlap should produce a clear warning.");
    Throws<InvalidOperationException>(() => store.Save(Sample()));
    Equal(original, File.ReadAllText(fixture.Path));
}

static void LocalUrls()
{
    using var fixture = new Fixture();
    foreach (var url in new[] { "http://localhost:5170", "https://127.0.0.1:8443", "http://[::1]:4200" })
    {
        var settings = Sample();
        settings.Projects[0].Services[0].Url = url;
        SettingsStore.Validate(settings, fixture.Root);
    }
    foreach (var url in new[] { "https://example.com", "file:///C:/", "http://user:pass@localhost:5000", "not-a-url" })
    {
        var settings = Sample();
        settings.Projects[0].Services[0].Url = url;
        Throws<ArgumentException>(() => SettingsStore.Validate(settings, fixture.Root), "loopback");
    }
    foreach (var uiPath in new[] { "//example.com", "https://example.com", "/\\example.com", "" })
    {
        var settings = Sample();
        settings.Projects[0].Services[0].UiPath = uiPath;
        Throws<ArgumentException>(() => SettingsStore.Validate(settings, fixture.Root), "UI path");
    }
}

static void SelectedProjectFallback()
{
    using var fixture = new Fixture();
    var settings = Sample();
    settings.SelectedProjectId = "no-longer-present";
    File.WriteAllText(fixture.Path, JsonSerializer.Serialize(settings));
    Equal("project-a", fixture.Store().Load().SelectedProjectId);
}

static void LegacyDeveloperTools()
{
    using var fixture = new Fixture();
    var original = Sample();
    var oldDocument = JsonSerializer.SerializeToNode(original)!.AsObject();
    Check(oldDocument.Remove(nameof(LauncherSettings.DeveloperTools)), "The legacy fixture must omit the new field.");
    var oldContent = oldDocument.ToJsonString();
    File.WriteAllText(fixture.Path, oldContent);

    var store = fixture.Store();
    var loaded = store.Load();
    Equal<string?>(null, store.LoadWarning);
    Equal(oldContent, File.ReadAllText(fixture.Path));
    Equal(JsonSerializer.Serialize(original.Projects), JsonSerializer.Serialize(loaded.Projects));
    Equal(original.SelectedProjectId, loaded.SelectedProjectId);
    Equal(1, loaded.DeveloperTools.Count);
    Equal("PgAdmin", loaded.DeveloperTools[0].Kind);
    Equal("pgadmin-4", loaded.DeveloperTools[0].Id);

    store.Save(loaded);
    Equal(oldContent, File.ReadAllText(fixture.Path + ".bak"));
    var reloaded = fixture.Store().Load();
    Equal(JsonSerializer.Serialize(original.Projects), JsonSerializer.Serialize(reloaded.Projects));
    Equal(1, reloaded.DeveloperTools.Count);
    using var savedDocument = JsonDocument.Parse(File.ReadAllText(fixture.Path));
    Equal(1, savedDocument.RootElement.GetProperty("developerTools").GetArrayLength());
}

static void DeveloperToolsRoundTrip()
{
    using var fixture = new Fixture();
    var settings = Sample();
    settings.DeveloperTools =
    [
        new() { Id = "sql-client", Name = "Database Client", Kind = "Application", Target = Path.Combine("Portable tools", "Database Client", "Client App.exe") },
        new() { Id = "docs", Name = "Team documentation", Kind = "Website", Target = "https://example.com/docs?q=full%20stack#quick-start" },
        new() { Id = "pgadmin", Name = "pgAdmin auto", Kind = "PgAdmin", Target = "" },
        new() { Id = "pgadmin-custom", Name = "pgAdmin custom", Kind = "PgAdmin", Target = Path.Combine(fixture.Root, "Database tools", "pgAdmin 4.exe") }
    ];
    var store = fixture.Store();
    store.Load();
    store.Save(settings);
    var reloadedStore = fixture.Store();
    var reloaded = reloadedStore.Load();
    Equal<string?>(null, reloadedStore.LoadWarning);
    Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(reloaded));
    Equal(4, reloaded.DeveloperTools.Count);
    Check(!Directory.Exists(Path.Combine(fixture.Root, "Database tools")), "Saving a tool location must not create or launch its target.");
}

static void EmptyDeveloperTools()
{
    using var fixture = new Fixture();
    var settings = Sample();
    settings.DeveloperTools = [];
    var store = fixture.Store();
    store.Load();
    store.Save(settings);
    var reloadedStore = fixture.Store();
    var reloaded = reloadedStore.Load();
    Equal<string?>(null, reloadedStore.LoadWarning);
    Equal(0, reloaded.DeveloperTools.Count);
    reloadedStore.Save(reloaded);
    Equal(0, fixture.Store().Load().DeveloperTools.Count);
    Equal(JsonSerializer.Serialize(settings.Projects), JsonSerializer.Serialize(reloaded.Projects));
}

static void MalformedDeveloperTools()
{
    foreach (var (toolsJson, expectedMessage) in new[]
    {
        ("null", "array"),
        ("{}", "array"),
        ("[null]", "JSON object"),
        ("[{}]", "id"),
        ("[{\"id\":\"bad\",\"name\":\"Missing kind\",\"target\":\"\"}]", "kind"),
        ("[{\"id\":\"bad\",\"name\":\"Missing target\",\"kind\":\"PgAdmin\"}]", "target"),
        ("[{\"id\":\"one\",\"name\":\"First\",\"kind\":\"PgAdmin\",\"target\":\"\"},{\"id\":\"ONE\",\"name\":\"Second\",\"kind\":\"PgAdmin\",\"target\":\"\"}]", "Duplicate developer tool ID")
    })
    {
        using var fixture = new Fixture();
        var document = JsonSerializer.SerializeToNode(Sample())!.AsObject();
        document[nameof(LauncherSettings.DeveloperTools)] = JsonNode.Parse(toolsJson);
        var content = document.ToJsonString();
        File.WriteAllText(fixture.Path, content);
        var store = fixture.Store();
        store.Load();
        Check(store.LoadWarning?.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase) == true,
            $"Expected malformed-tools warning containing '{expectedMessage}', got '{store.LoadWarning}'.");
        Throws<InvalidOperationException>(() => store.Save(Sample()), "Saving is disabled");
        Equal(content, File.ReadAllText(fixture.Path));
        var recovery = Directory.GetFiles(fixture.Root, "settings.json.invalid.*.bak");
        Equal(1, recovery.Length);
        Equal(content, File.ReadAllText(recovery[0]));
    }
}

static void InvalidDeveloperTools()
{
    using var fixture = new Fixture();
    var store = fixture.Store();
    store.Load();
    var valid = Sample();
    store.Save(valid);
    var originalContent = File.ReadAllText(fixture.Path);
    var duplicate = Sample();
    duplicate.DeveloperTools = [DeveloperTool.PgAdmin(), new() { Id = "PGADMIN-4", Name = "Another", Kind = "PgAdmin" }];
    Throws<ArgumentException>(() => store.Save(duplicate), "Duplicate developer tool ID");
    Equal(originalContent, File.ReadAllText(fixture.Path));
    foreach (var target in new[] { "https://user:password@example.com", "http://user:password@localhost:5050" })
    {
        var invalid = Sample();
        invalid.DeveloperTools = [new() { Id = "credentials", Name = "Credential URL", Kind = "Website", Target = target }];
        Throws<ArgumentException>(() => store.Save(invalid));
        Equal(originalContent, File.ReadAllText(fixture.Path));

        // The same invalid entry in a hand-edited file must receive the recovery behavior.
        using var loadedFixture = new Fixture();
        var invalidContent = JsonSerializer.Serialize(invalid);
        File.WriteAllText(loadedFixture.Path, invalidContent);
        var loadedStore = loadedFixture.Store();
        loadedStore.Load();
        Check(loadedStore.LoadWarning != null, "A credential URL in loaded settings must be rejected.");
        Throws<InvalidOperationException>(() => loadedStore.Save(Sample()));
        Equal(invalidContent, File.ReadAllText(loadedFixture.Path));
    }
}

static LauncherSettings Sample() => new()
{
    SelectedProjectId = "project-b",
    Projects =
    [
        new()
        {
            Id = "project-a", Name = "Project A", RootPath = Path.Combine("..", "Project A"),
            Services = [Service("api-a", "api", "dotnet run", 5170), Service("web-a", "web", "npm start", 4200)]
        },
        new()
        {
            Id = "project-b", Name = "Project B", RootPath = Path.Combine("..", "Project B"),
            Services = [Service("api-b", "api", "dotnet run --project Example.csproj", 5270), Service("web-b", "web", "npm start -- --port 4400", 4400)]
        }
    ]
};

static ServiceProfile Service(string id, string folder, string command, int port) => new()
{
    Id = id, Name = id, Kind = folder == "api" ? ".NET" : "Angular", WorkingDirectory = folder,
    StartCommand = command, CleanCommand = folder == "api" ? "dotnet clean" : "npm exec -- ng cache clean",
    SetupCommand = folder == "api" ? "dotnet restore" : "npm install", Url = $"http://localhost:{port}",
    UiPath = folder == "api" ? "/swagger" : "/"
};

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string? detail = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'. {detail}");
}

static void Throws<T>(Action action, string? message = null) where T : Exception
{
    try { action(); }
    catch (T ex)
    {
        if (message != null && !ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected error containing '{message}', got '{ex.Message}'.", ex);
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class Fixture : IDisposable
{
    private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FullStackLauncher.SettingsChecks"));
    private readonly string _name = Guid.NewGuid().ToString("N");
    public string Root { get; }
    public string Path => System.IO.Path.Combine(Root, "settings.json");

    public Fixture()
    {
        Root = System.IO.Path.Combine(_parent, _name);
        Directory.CreateDirectory(Root);
    }

    public SettingsStore Store() => new(Path);

    public void Dispose()
    {
        var resolvedRoot = System.IO.Path.GetFullPath(Root);
        var allowedPrefix = _parent + System.IO.Path.DirectorySeparatorChar;
        if (!resolvedRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(resolvedRoot) != _name)
            throw new InvalidOperationException("Refusing to remove a fixture outside the settings-test directory.");
        if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, recursive: true);
    }
}
