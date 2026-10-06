using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Collections.ObjectModel;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class ServiceViewModel(ServiceRunner runner) : ObservableObject
{
    private const int MaxRetainedConsoleLines = 600;
    private ServiceSnapshot _snapshot = runner.Snapshot;
    private SwaggerUiStatus _swaggerStatus = runner.SwaggerStatus;
    private readonly Dictionary<string, object?> _notifiedValues = new(StringComparer.Ordinal);
    private bool _hasDotnetProject;
    private bool _refreshingApiProject;
    private long _nextApiProjectRefresh;
    private string? _databaseChangeCardName;
    private string? _databaseChangeStatus;
    private string? _databaseChangeDetails;
    private string? _databaseChangeExpectedName;
    private string? _databaseChangeKey;
    private long _databaseChangePreviousRun;
    private bool _isBusy;
    private bool _isStopping;
    private string? _activeOperation;
    private bool _areCommandsBlocked;
    public bool AreCommandsBlocked { get => _areCommandsBlocked; set { if (_areCommandsBlocked == value) return; _areCommandsBlocked = value; Update(); } }
    private int _retainedOutputErrors;
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];
    public SourceLineCountViewModel SourceLines { get; } = new();
    public ApiEndpointCountViewModel ApiEndpoints { get; } = new();
    public bool HasConsoleOutput => ConsoleLines.Count > 0;
    public bool HasOutputError => _retainedOutputErrors > 0;

    public void AppendConsoleLine(ConsoleLine line)
    {
        var hadOutput = HasConsoleOutput;
        var hadError = HasOutputError;
        ConsoleLines.Add(line);
        if (line.Kind == ServiceLogKind.Error) _retainedOutputErrors++;
        while (ConsoleLines.Count > MaxRetainedConsoleLines)
        {
            if (ConsoleLines[0].Kind == ServiceLogKind.Error) _retainedOutputErrors--;
            ConsoleLines.RemoveAt(0);
        }
        if (!hadOutput) Changed(nameof(HasConsoleOutput));
        if (hadError != HasOutputError) Changed(nameof(HasOutputError));
    }

    public void ClearConsoleLines()
    {
        var hadOutput = HasConsoleOutput;
        var hadError = HasOutputError;
        ConsoleLines.Clear();
        _retainedOutputErrors = 0;
        if (hadOutput) Changed(nameof(HasConsoleOutput));
        if (hadError) Changed(nameof(HasOutputError));
    }
    private bool _isEditing;
    private bool _isSavingEdits;
    private string _draftName = "";
    private string _draftPort = "";
    private string? _portEditUnavailableReason;
    private string? _portEditDependencyReason;
    private string? _portEditLinkedFrontendHint;
    public bool IsEditing => _isEditing;
    public bool IsSavingEdits { get => _isSavingEdits; set { if (_isSavingEdits == value) return; _isSavingEdits = value; Update(); } }
    public string DraftName { get => _draftName; set { if (_draftName == value) return; _draftName = value; Changed(); } }
    public string DraftPort { get => _draftPort; set { if (_draftPort == value) return; _draftPort = value; Changed(); } }
    public bool IsConsoleApp => Profile.IsConsole;
    public bool IsCommandApi => Profile.IsCommandApi;
    public bool ShowCommandFolder => IsConsoleApp || IsCommandApi;
    public bool IsFrontendService => FrontendServiceSupport.IsFrontend(Profile);
    public bool OpenAfterBuild => Profile.OpenAfterBuild;
    public bool CanChangeOpenAfterBuild => IsFrontendService && !IsEditing && !IsSavingEdits &&
        !AreCommandsBlocked && !IsBusy && !IsStopping;
    public bool CanChangeFrontendPort => IsFrontendService && !IsEditing && !IsSavingEdits &&
        !AreCommandsBlocked && !IsBusy && !IsStopping;
    private string _browserOpenStatus = "Applies to the next managed start or restart.";
    public string OpenAfterBuildDetails =>
        "Open this frontend after a successful build and local HTTP readiness. " +
        "An existing tab at the same local site, including another page within it, prevents another tab. " +
        "Checks Chrome and Edge background tabs through the paired browser extension. " +
        "If existing tabs cannot be checked, opening is skipped.\n\n" + _browserOpenStatus;
    public void SetBrowserOpenStatus(string status)
    {
        if (_browserOpenStatus == status) return;
        _browserOpenStatus = status;
        Changed(nameof(OpenAfterBuildDetails));
    }
    public void RefreshOpenAfterBuild()
    {
        Changed(nameof(OpenAfterBuild));
        Changed(nameof(CanChangeOpenAfterBuild));
    }
    public bool HasWebEndpoint => !IsConsoleApp;
    public string StartButtonLabel => IsConsoleApp ? "▶ Run" : "▶ Start";
    public bool ShowClean => !(IsConsoleApp || IsCommandApi) || !string.IsNullOrWhiteSpace(Profile.CleanCommand);
    public bool ShowSetup => !(IsConsoleApp || IsCommandApi) || !string.IsNullOrWhiteSpace(Profile.SetupCommand);
    public bool ShowInlinePortEdit => HasWebEndpoint && !IsFrontendService;
    public bool CanStopForPortEdit => ShowInlinePortEdit && CanStop;
    public string ServiceTypeLabel => IsCommandApi ? Profile.ApiType!.ToUpperInvariant() : Profile.IsGenericConsole
        ? string.IsNullOrWhiteSpace(Profile.ConsoleType) ? "CONSOLE APP" : Profile.ConsoleType.ToUpperInvariant()
        : Kind;
    public bool ShowProcessTrackingHint => Runner.UsesManagedProcessTrackingOnly;
    public string ProcessTrackingHint => Profile.IsGenericConsole || IsCommandApi
        ? "Tracks launcher-started command processes."
        : "Shared service folder: tracks only launcher-started processes.";
    public bool ShowServiceTypeLabel => !IsCommandApi && !ShowApiDatabaseLabel &&
        !Profile.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase);
    public bool IsStoppedForPortEdit => HasWebEndpoint && !IsBusy && !IsStopping && !Runner.HasManagedProcess &&
        Runner.HasVerifiedNoServiceProcesses && Runner.Snapshot.ProcessIds.Count == 0 &&
        Runner.Snapshot.State is ServiceState.Stopped or ServiceState.Error or ServiceState.Conflict;
    public bool CanEditPort => IsEditing && !IsSavingEdits && IsStoppedForPortEdit &&
        _portEditUnavailableReason is null && _portEditDependencyReason is null;
    public string PortEditHint => _portEditUnavailableReason ?? _portEditDependencyReason ?? (IsStoppedForPortEdit
        ? _portEditLinkedFrontendHint ?? "Applied on the next start."
        : Runner.HasManagedProcess || Runner.Snapshot.ProcessIds.Count > 0
            ? "Stop this service before changing its port. Names can still be edited."
            : "Waiting for a successful process check to unlock the port. Names can still be edited.");
    public void SetPortEditDependencyStatus(string? reason, string? linkedFrontendHint)
    {
        if (_portEditDependencyReason == reason && _portEditLinkedFrontendHint == linkedFrontendHint) return;
        _portEditDependencyReason = reason;
        _portEditLinkedFrontendHint = linkedFrontendHint;
        NotifyIfChanged(CanEditPort, nameof(CanEditPort));
        NotifyIfChanged(PortEditHint, nameof(PortEditHint));
    }
    public void BeginEditing()
    {
        DraftName = Name;
        DraftPort = ServicePortConfiguration.GetPort(Profile)?.ToString() ?? "";
        _portEditUnavailableReason = ServicePortConfiguration.GetEditUnavailableReason(Profile, Directory);
        _isEditing = true;
        Update();
    }
    public void EndEditing() { _isEditing = false; Update(); }
    public ServiceRunner Runner { get; } = runner;
    public ServiceProfile Profile => Runner.Profile;
    public string Name => Profile.Name;
    public string Kind => Profile.Kind.ToUpperInvariant();
    public string Directory => Runner.WorkingDirectory;
    public string Command => Profile.ApiConfiguration is null ? Profile.StartCommand
        : $"{Profile.ApiConfiguration.LaunchCommand ?? ApiLaunchConfiguration.DefaultLaunchCommand} · {Profile.ApiConfiguration.Environment} configuration";
    public bool HasApiConfiguration => !(IsConsoleApp || IsCommandApi) && (Profile.ApiConfiguration is not null || _hasDotnetProject);
    public bool ShowApiDatabaseLabel => !(IsConsoleApp || IsCommandApi) && (HasApiConfiguration ||
        Profile.Kind.Equals(".NET", StringComparison.OrdinalIgnoreCase) ||
        Profile.Kind.Equals("API", StringComparison.OrdinalIgnoreCase));
    private string? DiscoveredDatabaseName => HasCurrentApiDatabaseDiscovery
        ? _discoveredApiDatabases.OrderBy(database => ApiDatabaseIdentifier.ConnectionKeyPriority(database.Key))
            .Select(database => database.Name).FirstOrDefault(name => name is not null) : null;
    public bool ShowChangeDatabaseRecovery => HasApiConfiguration && !HasDatabaseCard;
    public string DatabaseCardName => _databaseChangeCardName ?? DiscoveredDatabaseName ?? "Database";
    public string DatabaseCardStatus => _databaseChangeStatus ?? (HasCurrentApiDatabaseDiscovery
        ? HasDatabaseCard ? "Connected" : "Unavailable · no database configured"
        : _apiDatabaseDiscoveryAvailable ? "Unavailable · Local configuration inactive"
        : "Unavailable · configuration unreadable");
    public string DatabaseCardDetails => _databaseChangeDetails ?? "";
    public bool HasDatabaseCardDetails => !string.IsNullOrWhiteSpace(_databaseChangeDetails);

    internal void BeginDatabaseChange(string expectedName, string key)
    {
        _databaseChangeCardName = ApiDatabases.FirstOrDefault(database =>
            database.Identity.Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? expectedName;
        _databaseChangeExpectedName = expectedName;
        _databaseChangeKey = key;
        _databaseChangePreviousRun = Runner.ManagedApiRunVersion;
        _databaseChangeStatus = "Changing DB…";
        _databaseChangeDetails = $"Configuration saved. Restarting {Name}, then rediscovering its database configuration.";
        NotifyDatabaseCard();
    }

    internal void FinishDatabaseChange(bool verified, string detail)
    {
        _databaseChangeCardName = verified ? null : _databaseChangeCardName ?? DatabaseCardName;
        _databaseChangeStatus = verified ? null : "Change not verified";
        _databaseChangeDetails = verified ? null : detail;
        if (verified)
        {
            _databaseChangeExpectedName = null;
            _databaseChangeKey = null;
        }
        NotifyDatabaseCard();
    }

    private void TryCompleteDatabaseChangeFromDiscovery(long discoveryRunVersion)
    {
        if (_databaseChangeExpectedName is { } expectedName && _databaseChangeKey is { } key &&
            discoveryRunVersion == Runner.ManagedApiRunVersion && discoveryRunVersion > _databaseChangePreviousRun &&
            Runner.Snapshot is { State: ServiceState.Running, IsManaged: true } &&
            Runner.AppliedConfigurationEnvironment == "Local" && !Runner.ConfigurationNeedsRestart &&
            IsDatabaseConfigurationDiscovered(key, expectedName))
            FinishDatabaseChange(true, "");
    }

    private void NotifyDatabaseCard()
    {
        UpdateApiDatabaseServices();
        NotifyIfChanged(ShowChangeDatabaseRecovery, nameof(ShowChangeDatabaseRecovery));
        NotifyIfChanged(DatabaseCardName, nameof(DatabaseCardName));
        NotifyIfChanged(DatabaseCardStatus, nameof(DatabaseCardStatus));
        NotifyIfChanged(DatabaseCardDetails, nameof(DatabaseCardDetails));
        NotifyIfChanged(HasDatabaseCardDetails, nameof(HasDatabaseCardDetails));
    }

    // Bindings must not touch the filesystem: project folders may live on OneDrive or a slow drive.
    // Periodic background discovery still notices project files created/removed outside the launcher.
    public async Task RefreshApiProjectAvailabilityAsync()
    {
        if (IsConsoleApp || IsCommandApi || Profile.ApiConfiguration is not null || _refreshingApiProject ||
            Environment.TickCount64 < _nextApiProjectRefresh) return;
        _refreshingApiProject = true;
        try
        {
            var hasProject = await Task.Run(() =>
            {
                try { return System.IO.Directory.EnumerateFiles(Directory, "*.csproj", SearchOption.TopDirectoryOnly).Any(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { return false; }
            });
            _hasDotnetProject = hasProject;
            NotifyIfChanged(HasApiConfiguration, nameof(HasApiConfiguration));
            NotifyIfChanged(ShowChangeDatabaseRecovery, nameof(ShowChangeDatabaseRecovery));
            NotifyIfChanged(ShowApiDatabaseLabel, nameof(ShowApiDatabaseLabel));
            NotifyIfChanged(ShowServiceTypeLabel, nameof(ShowServiceTypeLabel));
            NotifyIfChanged(CanConfigureApi, nameof(CanConfigureApi));
            NotifyIfChanged(CanSwitchConfiguration, nameof(CanSwitchConfiguration));
            NotifyIfChanged(CanChangeDatabase, nameof(CanChangeDatabase));
        }
        finally
        {
            _nextApiProjectRefresh = Environment.TickCount64 + 15_000;
            _refreshingApiProject = false;
        }
    }
    public bool CanConfigureApi => !AreCommandsBlocked && !IsEditing && HasApiConfiguration && !IsBusy && !IsStopping;
    public bool CanSwitchConfiguration => CanConfigureApi && !HasConflict;
    public bool CanChangeDatabase => CanSwitchConfiguration && CanRestart && !ProductionWarning;
    public string SelectedConfiguration => Profile.ApiConfiguration?.Environment ?? "Local";
    public bool ProductionWarning => SelectedConfiguration == "Prod" || Runner.AppliedConfigurationEnvironment == "Prod";
    public string ConfigurationColor => ProductionWarning ? "#FFACA9" : "#A6B4C9";
    public string ConfigurationStatus
    {
        get
        {
            if (Runner.AppliedConfigurationEnvironment is { } applied)
            {
                var state = _snapshot.State == ServiceState.Running ? "ACTIVE" : "STARTING / APPLIED";
                var detail = $"{applied.ToUpperInvariant()} {state}";
                if (applied != SelectedConfiguration) detail += $" · {SelectedConfiguration} selected, restart needed";
                else if (Runner.ConfigurationNeedsRestart) detail += " · settings saved, restart needed";
                return detail;
            }
            if (_snapshot.State == ServiceState.Checking)
                return $"{SelectedConfiguration.ToUpperInvariant()} SELECTED · checking API status";
            if (_snapshot.State == ServiceState.Busy)
                return $"{SelectedConfiguration.ToUpperInvariant()} SELECTED · service maintenance in progress";
            if (_snapshot.ProcessIds.Count > 0)
                return $"ENVIRONMENT UNVERIFIED · {SelectedConfiguration} selected; restart through API settings";
            return $"{SelectedConfiguration.ToUpperInvariant()} SELECTED · API stopped";
        }
    }
    public string LocalButtonLabel => "Use Local & restart";
    public bool IsBusy { get => _isBusy; set { if (_isBusy == value) return; _isBusy = value; Update(); } }
    public bool IsStopping { get => _isStopping; set { if (_isStopping == value) return; _isStopping = value; Update(); } }
    public string Status => IsStopping ? "STOPPING" : IsBusy ? "WORKING" : _snapshot.State.ToString().ToUpperInvariant();
    public string CardStatus => !IsStopping && !IsBusy && HasWebEndpoint && _snapshot.State == ServiceState.Running &&
        Uri.TryCreate(_snapshot.ActiveUrl, UriKind.Absolute, out var activeUrl) && activeUrl.Port > 0
            ? $"Running on {activeUrl.Port}"
            : Status;
    public string OverviewStatus => $"{Name} {OverviewState}";
    public string OverviewStatusColor => StateColor;
    private string OverviewState => IsStopping ? "Stopping"
        : IsBusy && _snapshot.State == ServiceState.Busy &&
          _snapshot.Detail.StartsWith("Stopping", StringComparison.Ordinal) ? "Stopping"
        : IsBusy ? _activeOperation ?? "Working"
        : _snapshot.State == ServiceState.Starting ? IsConsoleApp ? "Starting" : "Waiting for HTTP"
        : _snapshot.State switch
        {
            ServiceState.Running => "Running",
            ServiceState.Completed => "Completed",
            ServiceState.Conflict => "Port conflict",
            ServiceState.Error => "Failed",
            ServiceState.Checking => "Checking",
            ServiceState.Busy => "Working",
            _ => "Stopped"
        };
    internal void SetActiveOperation(string? operation)
    {
        if (_activeOperation == operation) return;
        _activeOperation = operation;
        Update();
    }
    public string Detail => _snapshot.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail) &&
        !(HasWebEndpoint && _snapshot.State == ServiceState.Stopped && Detail == "No matching service processes");
    public bool HasCardDetail => HasDetail && (IsConsoleApp || _snapshot.State != ServiceState.Running ||
        Detail.EndsWith("(server reports an error)", StringComparison.Ordinal));
    public string StateColor => IsStopping || IsBusy ? "#F6CF7D" : _snapshot.State switch
    {
        ServiceState.Running or ServiceState.Completed => "#69E2C0", ServiceState.Starting or ServiceState.Busy => "#F6CF7D",
        ServiceState.Conflict or ServiceState.Error => "#FF939A", _ => "#9AAAC0"
    };
    public string ProcessLabel => _snapshot.ProcessIds.Count == 0 ? "No matching process" :
        $"PID {string.Join(", ", _snapshot.ProcessIds)}  ·  {(_snapshot.IsManaged ? "Started here" : "Detected externally")}";
    public string Url => LiveUrl ?? ExpectedUrl;
    public string LaunchButtonLabel
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return "Launch Page";
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString).ToArray();
            if (segments.Any(segment => segment.Equals("swagger", StringComparison.OrdinalIgnoreCase)))
                return "Launch Swagger";
            if (IsCommandApi && segments.Any(segment => segment.Equals("docs", StringComparison.OrdinalIgnoreCase)))
                return "Launch API Docs";
            var page = segments.LastOrDefault();
            if (page is not null && (page.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                page.Equals("index.htm", StringComparison.OrdinalIgnoreCase)))
                page = segments.Length > 1 ? segments[^2] : null;
            if (page is null)
                return IsFrontendService ? "Launch Landing Page" : IsCommandApi || ShowApiDatabaseLabel ? "Launch API" : "Launch Home Page";
            page = Path.GetFileNameWithoutExtension(page).Replace('-', ' ').Replace('_', ' ').Trim();
            var pageName = page.ToLowerInvariant() switch
            {
                "openapi" => "OpenAPI",
                "redoc" => "ReDoc",
                "api" => "API",
                _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(page)
            };
            return string.IsNullOrEmpty(pageName) ? "Launch Page" : $"Launch {pageName}";
        }
    }
    public string LaunchButtonAccessibleName => $"{LaunchButtonLabel} for {Name}";
    public bool IsSwaggerUnavailable => Runner.IsSwaggerUiLaunch &&
        _snapshot.State == ServiceState.Running && !IsBusy && !IsStopping &&
        _swaggerStatus.Availability == SwaggerUiAvailability.Unavailable &&
        string.Equals(_swaggerStatus.UiUrl, _snapshot.ActiveUrl, StringComparison.Ordinal);
    public string LaunchButtonHelpText
    {
        get
        {
            if (HasLiveUrl) return $"Open {LiveUrl} in your default browser.";
            if (IsStopping) return $"{Name} is stopping. Start it again and wait until this button turns green.";
            if (IsBusy) return $"{Name} is busy. Wait for the operation to finish and for this button to turn green.";
            if (Runner.IsSwaggerUiLaunch && _snapshot.State == ServiceState.Running)
                return string.Equals(_swaggerStatus.UiUrl, _snapshot.ActiveUrl, StringComparison.Ordinal) &&
                    _swaggerStatus.Availability is SwaggerUiAvailability.Checking or SwaggerUiAvailability.Unavailable
                    ? _swaggerStatus.Message
                    : "Checking whether Swagger UI is available. Wait until this button turns green.";
            return _snapshot.State switch
            {
                ServiceState.Checking => $"Checking whether {Name} is ready. Wait until this button turns green.",
                ServiceState.Starting => $"{Name} is starting. Wait until it is running and ready; this button will turn green.",
                ServiceState.Busy => $"{Name} is busy. Wait for the operation to finish and for this button to turn green.",
                ServiceState.Conflict => $"{Name} has a port conflict. Resolve the conflict and restart it, then wait until this button turns green.",
                ServiceState.Error => $"{Name} is unavailable. Check its console output, then start or restart it and wait until this button turns green.",
                ServiceState.Stopped or ServiceState.Completed => $"{Name} is stopped. Click Start and wait until this button turns green.",
                _ => $"{Name} is not ready to open yet. Wait until this button turns green."
            };
        }
    }
    public string ExpectedUrl => Uri.TryCreate(Profile.Url, UriKind.Absolute, out var uri)
        ? new Uri(uri, string.IsNullOrWhiteSpace(Profile.UiPath) ? "/" : Profile.UiPath).ToString() : Profile.Url;
    public bool CanOpen => HasWebEndpoint && _snapshot.State == ServiceState.Running && _snapshot.ActiveUrl is not null &&
        (!Runner.IsSwaggerUiLaunch || (_swaggerStatus.Availability == SwaggerUiAvailability.Ready &&
            string.Equals(_swaggerStatus.UiUrl, _snapshot.ActiveUrl, StringComparison.Ordinal)));
    public bool HasLiveUrl => CanOpen && !IsBusy && !IsStopping;
    public string? LiveUrl => HasLiveUrl ? _snapshot.ActiveUrl : null;
    public bool CanStart => !AreCommandsBlocked && !IsEditing && !IsBusy && !IsStopping && (_snapshot.State is ServiceState.Stopped or ServiceState.Error or ServiceState.Completed) && _snapshot.ProcessIds.Count == 0;
    public bool CanRestart => !AreCommandsBlocked && !IsEditing && !IsBusy && !IsStopping && _snapshot.State is not (ServiceState.Busy or ServiceState.Conflict or ServiceState.Checking);
    public bool HasConflict => _snapshot.State == ServiceState.Conflict;
    public bool CanResolveConflict => !AreCommandsBlocked && !IsEditing && HasConflict && !IsBusy && !IsStopping;
    public bool CanMaintain => !AreCommandsBlocked && !IsEditing && !IsBusy && !IsStopping && _snapshot.ProcessIds.Count == 0 && _snapshot.State is not (ServiceState.Starting or ServiceState.Busy or ServiceState.Conflict or ServiceState.Checking);
    public bool CanClean => CanMaintain && !string.IsNullOrWhiteSpace(Profile.CleanCommand);
    public bool CanSetup => CanMaintain && !string.IsNullOrWhiteSpace(Profile.SetupCommand);
    public bool CanStop => !IsSavingEdits && !IsStopping && (_snapshot.ProcessIds.Count > 0 || Runner.HasManagedProcess);
    public bool CanForceStop => !IsSavingEdits && !IsStopping && (CanStop || HasConflict);
    public bool IsRunning => _snapshot.State == ServiceState.Running;
    public bool ShowRunningDot => IsRunning && !IsBusy && !IsStopping;
    public void Update()
    {
        _snapshot = Runner.Snapshot;
        _swaggerStatus = Runner.SwaggerStatus;
        UpdateFlutterDatabaseState();
        // The same VM appears in the sidebar and the service card. Re-notifying every property
        // for an unchanged poll unnecessarily invalidates both visual trees and their layout.
        NotifyIfChanged(Name, nameof(Name));
        NotifyIfChanged(Kind, nameof(Kind));
        NotifyIfChanged(IsConsoleApp, nameof(IsConsoleApp));
        NotifyIfChanged(IsCommandApi, nameof(IsCommandApi));
        NotifyIfChanged(ShowCommandFolder, nameof(ShowCommandFolder));
        NotifyIfChanged(IsFrontendService, nameof(IsFrontendService));
        NotifyIfChanged(OpenAfterBuild, nameof(OpenAfterBuild));
        NotifyIfChanged(CanChangeOpenAfterBuild, nameof(CanChangeOpenAfterBuild));
        NotifyIfChanged(CanChangeFrontendPort, nameof(CanChangeFrontendPort));
        NotifyIfChanged(HasWebEndpoint, nameof(HasWebEndpoint));
        NotifyIfChanged(StartButtonLabel, nameof(StartButtonLabel));
        NotifyIfChanged(ShowClean, nameof(ShowClean));
        NotifyIfChanged(ShowSetup, nameof(ShowSetup));
        NotifyIfChanged(CanStopForPortEdit, nameof(CanStopForPortEdit));
        NotifyIfChanged(ShowInlinePortEdit, nameof(ShowInlinePortEdit));
        NotifyIfChanged(ServiceTypeLabel, nameof(ServiceTypeLabel));
        NotifyIfChanged(ShowProcessTrackingHint, nameof(ShowProcessTrackingHint));
        NotifyIfChanged(ProcessTrackingHint, nameof(ProcessTrackingHint));
        NotifyIfChanged(ShowServiceTypeLabel, nameof(ShowServiceTypeLabel));
        NotifyIfChanged(ExpectedUrl, nameof(ExpectedUrl));
        NotifyIfChanged(IsEditing, nameof(IsEditing));
        NotifyIfChanged(IsSavingEdits, nameof(IsSavingEdits));
        NotifyIfChanged(IsStoppedForPortEdit, nameof(IsStoppedForPortEdit));
        NotifyIfChanged(CanEditPort, nameof(CanEditPort));
        NotifyIfChanged(PortEditHint, nameof(PortEditHint));
        NotifyIfChanged(AreCommandsBlocked, nameof(AreCommandsBlocked));
        NotifyIfChanged(IsBusy, nameof(IsBusy));
        NotifyIfChanged(IsStopping, nameof(IsStopping));
        NotifyIfChanged(Status, nameof(Status));
        NotifyIfChanged(CardStatus, nameof(CardStatus));
        NotifyIfChanged(OverviewStatus, nameof(OverviewStatus));
        NotifyIfChanged(OverviewStatusColor, nameof(OverviewStatusColor));
        NotifyIfChanged(Detail, nameof(Detail));
        NotifyIfChanged(HasDetail, nameof(HasDetail));
        NotifyIfChanged(HasCardDetail, nameof(HasCardDetail));
        NotifyIfChanged(StateColor, nameof(StateColor));
        NotifyIfChanged(ProcessLabel, nameof(ProcessLabel));
        NotifyIfChanged(Url, nameof(Url));
        NotifyIfChanged(LaunchButtonLabel, nameof(LaunchButtonLabel));
        NotifyIfChanged(LaunchButtonAccessibleName, nameof(LaunchButtonAccessibleName));
        NotifyIfChanged(IsSwaggerUnavailable, nameof(IsSwaggerUnavailable));
        NotifyIfChanged(LaunchButtonHelpText, nameof(LaunchButtonHelpText));
        NotifyIfChanged(CanOpen, nameof(CanOpen));
        NotifyIfChanged(HasLiveUrl, nameof(HasLiveUrl));
        NotifyIfChanged(LiveUrl, nameof(LiveUrl));
        NotifyIfChanged(CanStart, nameof(CanStart));
        NotifyIfChanged(CanRestart, nameof(CanRestart));
        NotifyIfChanged(HasConflict, nameof(HasConflict));
        NotifyIfChanged(CanResolveConflict, nameof(CanResolveConflict));
        NotifyIfChanged(CanMaintain, nameof(CanMaintain));
        NotifyIfChanged(CanClean, nameof(CanClean));
        NotifyIfChanged(CanSetup, nameof(CanSetup));
        NotifyIfChanged(CanStop, nameof(CanStop));
        NotifyIfChanged(IsRunning, nameof(IsRunning));
        NotifyIfChanged(ShowRunningDot, nameof(ShowRunningDot));
        NotifyIfChanged(Command, nameof(Command));
        NotifyIfChanged(CanForceStop, nameof(CanForceStop));
        NotifyIfChanged(HasApiConfiguration, nameof(HasApiConfiguration));
        NotifyIfChanged(ShowApiDatabaseLabel, nameof(ShowApiDatabaseLabel));
        NotifyDatabaseCard();
        NotifyIfChanged(CanConfigureApi, nameof(CanConfigureApi));
        NotifyIfChanged(CanSwitchConfiguration, nameof(CanSwitchConfiguration));
        NotifyIfChanged(CanChangeDatabase, nameof(CanChangeDatabase));
        NotifyIfChanged(SelectedConfiguration, nameof(SelectedConfiguration));
        NotifyIfChanged(ProductionWarning, nameof(ProductionWarning));
        NotifyIfChanged(ConfigurationColor, nameof(ConfigurationColor));
        NotifyIfChanged(ConfigurationStatus, nameof(ConfigurationStatus));
    }

    private void NotifyIfChanged(object? value, string name)
    {
        if (_notifiedValues.TryGetValue(name, out var previous) && Equals(previous, value)) return;
        _notifiedValues[name] = value;
        Changed(name);
    }
}
