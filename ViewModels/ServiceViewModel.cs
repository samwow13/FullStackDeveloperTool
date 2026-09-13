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

public sealed class ServiceViewModel(ServiceRunner runner) : ObservableObject
{
    private ServiceSnapshot _snapshot = runner.Snapshot;
    private bool _isBusy;
    private bool _isStopping;
    private bool _areCommandsBlocked;
    public bool AreCommandsBlocked { get => _areCommandsBlocked; set { _areCommandsBlocked = value; Update(); } }
    private bool _isConsoleVisible;
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];
    public bool IsConsoleVisible { get => _isConsoleVisible; set { _isConsoleVisible = value; Changed(); } }
    private bool _isEditing;
    private bool _isSavingEdits;
    private string _draftName = "";
    private string _draftPort = "";
    private string? _portEditUnavailableReason;
    public bool IsEditing => _isEditing;
    public bool IsSavingEdits { get => _isSavingEdits; set { _isSavingEdits = value; Update(); } }
    public string DraftName { get => _draftName; set { _draftName = value; Changed(); } }
    public string DraftPort { get => _draftPort; set { _draftPort = value; Changed(); } }
    public bool IsConsoleApp => Profile.IsConsole;
    public bool HasWebEndpoint => !IsConsoleApp;
    public string StartButtonLabel => IsConsoleApp ? "▶ Run" : "▶ Start";
    public bool ShowClean => !IsConsoleApp || !string.IsNullOrWhiteSpace(Profile.CleanCommand);
    public bool ShowSetup => !IsConsoleApp || !string.IsNullOrWhiteSpace(Profile.SetupCommand);
    public int ActionColumnCount => 3 + (ShowClean ? 1 : 0) + (ShowSetup ? 1 : 0);
    public bool CanStopForPortEdit => HasWebEndpoint && CanStop;
    public string DesiredPortLabel => IsConsoleApp ? "CONSOLE APP" : $"{Kind} · port {ServicePortConfiguration.GetPort(Profile)?.ToString() ?? "—"}";
    public bool IsStoppedForPortEdit => HasWebEndpoint && !IsBusy && !IsStopping && !Runner.HasManagedProcess &&
        Runner.HasVerifiedNoServiceProcesses && Runner.Snapshot.ProcessIds.Count == 0 &&
        Runner.Snapshot.State is ServiceState.Stopped or ServiceState.Error or ServiceState.Conflict;
    public bool CanEditPort => IsEditing && !IsSavingEdits && IsStoppedForPortEdit && _portEditUnavailableReason is null;
    public string PortEditHint => _portEditUnavailableReason ?? (IsStoppedForPortEdit
        ? "Applied on the next start."
        : Runner.HasManagedProcess || Runner.Snapshot.ProcessIds.Count > 0
            ? "Stop this service before changing its port. Names can still be edited."
            : "Waiting for a successful process check to unlock the port. Names can still be edited.");
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
        : $"dotnet run --no-launch-profile · {Profile.ApiConfiguration.Environment} configuration";
    public bool HasApiConfiguration => !IsConsoleApp && (Profile.ApiConfiguration is not null || HasDotnetProject());
    private bool HasDotnetProject()
    {
        try { return System.IO.Directory.Exists(Directory) && System.IO.Directory.EnumerateFiles(Directory, "*.csproj").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public bool CanConfigureApi => !AreCommandsBlocked && !IsEditing && HasApiConfiguration && !IsBusy && !IsStopping;
    public bool CanSwitchConfiguration => CanConfigureApi && !HasConflict;
    public string SelectedConfiguration => Profile.ApiConfiguration?.Environment ?? "Local";
    public bool ProductionWarning => SelectedConfiguration == "Prod" || Runner.AppliedConfigurationEnvironment == "Prod";
    public string ConfigurationColor => ProductionWarning ? "#FFACA9" : "#A6B4C9";
    public string ConfigurationBackground => ProductionWarning ? "#3D202A" : "#121A25";
    public string ConfigurationBorder => ProductionWarning ? "#E96B78" : "#303D50";
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
                return $"ENVIRONMENT UNVERIFIED · {SelectedConfiguration} selected; use a button below to restart";
            return $"{SelectedConfiguration.ToUpperInvariant()} SELECTED · API stopped";
        }
    }
    public string LocalButtonLabel => "Use Local & restart";
    public string ProdButtonLabel => "Use Prod & restart";
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Update(); } }
    public bool IsStopping { get => _isStopping; set { _isStopping = value; Update(); } }
    public string Status => IsStopping ? "STOPPING" : IsBusy ? "WORKING" : _snapshot.State.ToString().ToUpperInvariant();
    public string Detail => _snapshot.Detail;
    public string StateColor => IsStopping || IsBusy ? "#F6CF7D" : _snapshot.State switch
    {
        ServiceState.Running or ServiceState.Completed => "#69E2C0", ServiceState.Starting or ServiceState.Busy => "#F6CF7D",
        ServiceState.Conflict or ServiceState.Error => "#FF939A", _ => "#9AAAC0"
    };
    public string ProcessLabel => _snapshot.ProcessIds.Count == 0 ? "No matching process" :
        $"PID {string.Join(", ", _snapshot.ProcessIds)}  ·  {(_snapshot.IsManaged ? "Started here" : "Detected externally")}";
    public string Url => _snapshot.ActiveUrl ?? ExpectedUrl;
    public string UrlCaption => _snapshot.ActiveUrl is null ? "CONFIGURED ADDRESS" : "LIVE ADDRESS";
    public string ExpectedUrl => Uri.TryCreate(Profile.Url, UriKind.Absolute, out var uri)
        ? new Uri(uri, string.IsNullOrWhiteSpace(Profile.UiPath) ? "/" : Profile.UiPath).ToString() : Profile.Url;
    public bool CanOpen => HasWebEndpoint && _snapshot.State == ServiceState.Running && _snapshot.ActiveUrl is not null;
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
    public void Update()
    {
        _snapshot = Runner.Snapshot;
        foreach (var name in new[] { nameof(Name), nameof(IsConsoleApp), nameof(HasWebEndpoint), nameof(StartButtonLabel), nameof(ShowClean), nameof(ShowSetup), nameof(ActionColumnCount), nameof(CanStopForPortEdit), nameof(DesiredPortLabel), nameof(ExpectedUrl), nameof(IsEditing), nameof(IsSavingEdits), nameof(CanEditPort), nameof(PortEditHint),
                     nameof(IsBusy), nameof(Status), nameof(Detail), nameof(StateColor), nameof(ProcessLabel),
                     nameof(Url), nameof(UrlCaption), nameof(CanOpen), nameof(CanStart), nameof(CanRestart), nameof(HasConflict), nameof(CanResolveConflict), nameof(CanClean), nameof(CanSetup), nameof(CanStop), nameof(IsRunning),
                     nameof(Command), nameof(CanForceStop), nameof(HasApiConfiguration), nameof(CanConfigureApi), nameof(CanSwitchConfiguration), nameof(SelectedConfiguration), nameof(ProductionWarning), nameof(ConfigurationColor),
                     nameof(ConfigurationBackground), nameof(ConfigurationBorder), nameof(ConfigurationStatus) }) Changed(name);
    }
}
