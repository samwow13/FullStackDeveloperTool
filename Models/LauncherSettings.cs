namespace FullStackLauncher.Models;

public sealed class LauncherSettings
{
    public int Version { get; set; } = 1;
    public string? SelectedProjectId { get; set; }
    public bool LongRunningTaskEnabled { get; set; }
    public List<ProjectProfile> Projects { get; set; } = [];
    public List<DeveloperTool> DeveloperTools { get; set; } = [];
    public WorkspaceLayout Layout { get; set; } = new();
}

public sealed class WorkspaceLayout
{
    public bool ProjectsVisible { get; set; } = true;
    public bool ToolsVisible { get; set; } = true;
    public bool ServicesVisible { get; set; } = true;
    public bool NextCommitVisible { get; set; }
    // Zero lets the Codex crew choose a comfortable card count for the available width.
    public int CodexCrewVisibleAgents { get; set; }
    public bool ConsoleVisible { get; set; } = true;
    public bool DatabaseVisible { get; set; } = true;
    public double ConsoleShare { get; set; } = 0.47;
    public double ServicesHeightShare { get; set; } = 0.35;
}

public sealed class DeveloperTool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New tool";
    // PgAdmin discovers the desktop installation. Application and Website use Target.
    public string Kind { get; set; } = "Application";
    public string Target { get; set; } = "";
    public static DeveloperTool PgAdmin() => new() { Id = "pgadmin-4", Name = "pgAdmin 4", Kind = "PgAdmin" };
}

public sealed class ProjectProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New project";
    public string RootPath { get; set; } = "..";
    public bool IsArchived { get; set; }
    // Legacy preferences are preserved for settings compatibility only; automatic restarts are removed.
    public bool AutoRestartAfterAgentsEnabled { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoRestartWatchPath { get; set; }
    // Inert legacy explorer selection, retained only to round-trip existing settings.
    public DatabaseSelection? Database { get; set; }
    // Optional SQLite file reference, relative to the project root. Never opens a database.
    [System.Text.Json.Serialization.JsonPropertyName("sqliteDatabasePath")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SQLiteDatabasePath { get; set; }
    public List<ServiceProfile> Services { get; set; } = [];
}

// Inert legacy database reference. Never save credentials here.
public sealed class DatabaseSelection
{
    public string SourceId { get; set; } = "";
    public string DatabaseName { get; set; } = "";
}

public sealed class ServiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Service";
    public string Kind { get; set; } = "Custom";
    // Optional display label for a generic console command, such as Docker or Python.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ConsoleType { get; set; }
    // A nonempty API type opts generic commands into job-based ownership with HTTP readiness.
    // Legacy Kind = API profiles without this marker keep their existing behavior.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiType { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGenericConsole => string.Equals(Kind, "Console", StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCommandApi => string.Equals(Kind, "API", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiType);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConsole => IsProcessBasedKind(Kind);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesProcessSession => IsConsole || IsCommandApi;
    public static bool IsProcessBasedKind(string? kind) =>
        string.Equals(kind, "Console", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "Dart", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "Flutter", StringComparison.OrdinalIgnoreCase);
    public string WorkingDirectory { get; set; } = ".";
    public string StartCommand { get; set; } = "";
    public string CleanCommand { get; set; } = "";
    public string SetupCommand { get; set; } = "";
    public string Url { get; set; } = "http://localhost:4200";
    public string UiPath { get; set; } = "/";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool OpenAfterBuild { get; set; }
    // Inert legacy comparison target, retained only to round-trip existing settings.
    public DatabaseSelection? ProductionDatabase { get; set; }
    // Inert legacy comparison source. No credentials or comparison results are saved here.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ComparisonLocalSourceId { get; set; }
    // An Angular development service can follow one API service in the same project.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiTargetServiceId { get; set; }
    // Existing unlinked Angular services retain port matching; an explicit None disables it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DisableLegacyApiPortSync { get; set; }
    // Only launch preferences are saved here; values live in the Windows-user encrypted store.
    public ApiConfigurationSelection? ApiConfiguration { get; set; }
}

public sealed class ApiConfigurationSelection
{
    public string Environment { get; set; } = "Local";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LaunchCommand { get; set; }
}

public enum ServiceState { Stopped, Starting, Running, Busy, Conflict, Error, Checking, Completed }

public sealed record ServiceSnapshot(ServiceState State, string Detail, IReadOnlyList<int> ProcessIds,
    string? ActiveUrl = null, bool IsManaged = false);

public enum ServiceLogKind { Output, Command, Information, Success, Warning, Error }

public sealed record ServiceLog(DateTime Timestamp, string ServiceId, string Message, bool IsError = false,
    ServiceLogKind? Kind = null)
{
    // In-memory ordering only. Queued output from before a clear must not reappear afterward.
    internal long ConsoleSequence { get; init; }
}
