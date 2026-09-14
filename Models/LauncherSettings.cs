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
    public DatabaseSelection? Database { get; set; }
    public List<ServiceProfile> Services { get; set; } = [];
}

// Only a configuration reference and database name are portable. Never save credentials here.
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
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConsole => string.Equals(Kind, "Console", StringComparison.OrdinalIgnoreCase);
    public string WorkingDirectory { get; set; } = ".";
    public string StartCommand { get; set; } = "";
    public string CleanCommand { get; set; } = "";
    public string SetupCommand { get; set; } = "";
    public string Url { get; set; } = "http://localhost:4200";
    public string UiPath { get; set; } = "/";
    // Only the selection is saved here; values live in the launcher's Windows-user encrypted store.
    public ApiConfigurationSelection? ApiConfiguration { get; set; }
}

public sealed class ApiConfigurationSelection
{
    public string Environment { get; set; } = "Local";
}

public enum ServiceState { Stopped, Starting, Running, Busy, Conflict, Error, Checking, Completed }

public sealed record ServiceSnapshot(ServiceState State, string Detail, IReadOnlyList<int> ProcessIds,
    string? ActiveUrl = null, bool IsManaged = false);

public enum ServiceLogKind { Output, Command, Information, Success, Warning, Error }

public sealed record ServiceLog(DateTime Timestamp, string ServiceId, string Message, bool IsError = false,
    ServiceLogKind? Kind = null);
