using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _store = new();
    private readonly LauncherSettings _settings;
    private readonly Dictionary<string, List<ServiceViewModel>> _runners = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;
    private bool _closing;
    private bool _closed;
    private bool _batchBusy;
    private bool _layoutReady;
    private ProjectTasks.ProjectTasksWindow? _projectTasksWindow;
    private string _notice = "Ready. Select a service to get started.";
    private string _lastChecked = "Checking status…";

    public ObservableCollection<ProjectProfile> Projects { get; } = [];
    public ObservableCollection<ProjectViewModel> ProjectItems { get; } = [];
    public ICollectionView VisibleProjectItems { get; }
    public ObservableCollection<ServiceViewModel> Services { get; } = [];
    public ObservableCollection<DeveloperToolViewModel> DeveloperTools { get; } = [];
    public ProjectProfile? SelectedProject { get; private set; }
    public string RootPath => SelectedProject is null ? "Add a project to start your workspace." : _store.ResolveRoot(SelectedProject);
    public string Summary => Services.Count == 0 ? "No apps configured" : Services.Any(service => service.Profile.IsConsole)
        ? $"{Services.Count(service => service.IsRunning)} running · {Services.Count(service => service.Runner.Snapshot.State == ServiceState.Completed)} completed · {Services.Count} apps"
        : $"{Services.Count(x => x.IsRunning)} / {Services.Count} services online";
    public bool CanBatch => !IsEditing && !_batchBusy && !_forceStopBatchBusy && !_closing && Services.Count > 0 && Services.All(x => !x.IsBusy && !x.IsStopping);
    public bool AllServicesRunning => Services.Count > 0 && Services.All(service => service.ShowRunningDot);
    public bool CanStartBatch => CanBatch && !_closeRequested && Services.Any(service => service.CanStart);
    public string StartAllDescription => AllServicesRunning
        ? "All services are already running. No start is needed."
        : CanStartBatch ? "Start only services that are stopped. Running services stay running."
        : Services.Count == 0 ? "No services are configured for this project."
        : "Start is unavailable while services are checking, starting, busy, blocked, or being edited. Review each service's status.";
    public bool CanStopBatch => !_closing && !_forceStopBatchBusy && Services.Any(x => x.CanForceStop);
    public bool CanEdit => SelectedProject is not null && !_savingProjectEdits && !_batchBusy && !_forceStopBatchBusy && !_closing && Services.All(x => !x.IsBusy && !x.IsStopping);
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); } }
    public string LastChecked { get => _lastChecked; private set { _lastChecked = value; Changed(nameof(LastChecked)); } }
    public string ProductionNotice => string.Join(Environment.NewLine, Projects.SelectMany(project => project.Services
        .Select(profile => (Project: project, Profile: profile, Runner: _runners.GetValueOrDefault(project.Id)?.FirstOrDefault(s => s.Profile.Id == profile.Id))))
        .Where(item => item.Profile.ApiConfiguration?.Environment == "Prod" || item.Runner?.ProductionWarning == true)
        .Select(item => $"{item.Project.Name} / {item.Profile.Name}: {item.Runner?.ConfigurationStatus ?? "PROD SELECTED · running environment unverified"}"));
    public bool HasProductionNotice => ProductionNotice.Length > 0;
    public bool ProjectsVisible { get => _settings.Layout.ProjectsVisible; set => SetSectionVisibility(nameof(ProjectsVisible), value); }
    public bool ToolsVisible { get => _settings.Layout.ToolsVisible; set => SetSectionVisibility(nameof(ToolsVisible), value); }
    public bool ServicesVisible { get => _settings.Layout.ServicesVisible; set => SetSectionVisibility(nameof(ServicesVisible), value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));

    internal void ReportUnexpectedError()
    {
        // Keep deferred UI failures in the existing status area, without exposing exception data.
        const string message = "An unexpected error occurred. Refresh the affected section, or reopen the launcher if it continues.";
        if (Notice != message) Notice = message;
    }

    public MainWindow()
    {
        _settings = _store.Load();
        foreach (var project in _settings.Projects)
        {
            Projects.Add(project);
            ProjectItems.Add(new(project, GetRunners(project)));
        }
        foreach (var tool in _settings.DeveloperTools) DeveloperTools.Add(new(tool));
        VisibleProjectItems = CollectionViewSource.GetDefaultView(ProjectItems);
        VisibleProjectItems.Filter = item => item is ProjectViewModel project && project.IsArchived == ShowArchivedProjects;
        InitializeComponent();
        InitializeSectionsMenu();
        InitializeNextCommitSettings();
        _consoleTimer.Tick += (_, _) => FlushConsoleOutput();
        _copyErrorsFeedbackTimer.Tick += (_, _) => CloseCopyErrorsFeedback();
        Deactivated += (_, _) => CloseCopyErrorsFeedback();
        LocationChanged += (_, _) => CloseCopyErrorsFeedback();
        Closed += (_, _) => CloseCopyErrorsFeedback();
        _layoutReady = true;
        ApplyLayout();
        DataContext = this;
        InitializeQueueActivity();
        InitializeLongRunningTaskMode();
        InitializeNextCommitReminder();
        InitializeDashboardGitComparison();
        _timer.Tick += async (_, _) => await Task.WhenAll(RefreshAsync(), RefreshProjectBranchesAsync(), RefreshNextCommitAsync());
        Activated += async (_, _) => await Task.WhenAll(RefreshProjectBranchesAsync(), RefreshNextCommitAsync());
        Closed += (_, _) => _branchLifetime.Cancel();
        Closed += (_, _) => _gitWarmupLifetime.Cancel();
        Closed += (_, _) => _nextCommitLifetime.Cancel();
        Closed += (_, _) => _sourceLineCountLifetime.Cancel();
        Closed += (_, _) => _frontendBrowserLifetime.Cancel();
        InitializeFrontendBrowserAccess();
        SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartGitConnectionWarmup();
        await RefreshProjectListAsync(Projects.FirstOrDefault(x => x.Id == _settings.SelectedProjectId));
        if (!string.IsNullOrWhiteSpace(_store.LoadWarning))
        {
            Notice = _store.LoadWarning;
            MessageBox.Show(this, _store.LoadWarning, "Settings need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _timer.Start();
        _consoleTimer.Start();
        await RestartExistingMonitorAsync();
        await RefreshDeveloperToolsAsync();
        await RefreshAsync();
    }

    private List<ServiceViewModel> GetRunners(ProjectProfile project)
    {
        if (_runners.TryGetValue(project.Id, out var existing)) return existing;
        var created = project.Services.Select(profile =>
        {
            var runner = new ServiceRunner(profile, _store.ResolveWorkingDirectory(project, profile));
            runner.LogReceived += log => QueueLog(project.Id, runner, profile.Name, log);
            runner.ConsoleOutputReset += () => QueueConsoleReset(project.Id, runner);
            var service = new ServiceViewModel(runner);
            runner.FrontendReady += (url, generation) => QueueFrontendBrowserOpen(project, service, url, generation);
            return service;
        }).ToList();
        _runners.Add(project.Id, created);
        return created;
    }

    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProjectList) return;
        // The list is disabled during inline editing; protect programmatic changes as well.
        if (IsEditing)
        {
            _refreshingProjectList = true;
            try { ProjectList.SelectedValue = SelectedProject; }
            finally { _refreshingProjectList = false; }
            return;
        }
        await ShowSelectedProjectAsync(ProjectList.SelectedValue as ProjectProfile);
    }

    private async Task ShowSelectedProjectAsync(ProjectProfile? project, bool saveSelection = true)
    {
        if (project is null)
        {
            SelectedProject = null;
            ResetNextCommitProject();
            _projectTasksWindow?.ShowProject(null, "");
            NotifyQueueActivityChanged();
            Services.Clear();
            foreach (var name in new[] { nameof(SelectedProject), nameof(SelectedProjectItem), nameof(RootPath) }) Changed(name);
            UpdateActions();
            return;
        }
        SelectedProject = project;
        ResetNextCommitProject();
        _projectTasksWindow?.ShowProject(project, _store.ResolveRoot(project));
        NotifyQueueActivityChanged();
        Services.Clear();
        foreach (var service in GetRunners(project)) Services.Add(service);
        _ = LoadServiceLineCountsAsync(project, Services.ToArray());
        if (saveSelection)
        {
            _settings.SelectedProjectId = project.Id;
            try { _store.Save(_settings); }
            catch (Exception ex) { Notice = $"Settings could not be saved: {ex.Message}"; }
        }
        foreach (var name in new[] { nameof(SelectedProject), nameof(SelectedProjectItem), nameof(RootPath), nameof(Summary), nameof(CanBatch), nameof(CanStopBatch), nameof(CanEdit) }) Changed(name);
        NotifyStartAllChanged();
        UpdateArchiveActions();
        _ = RefreshProjectBranchesAsync();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closeRequested || _closing) return;
        _refreshing = true;
        try
        {
            var services = _runners.Values.SelectMany(x => x).ToArray();
            await Task.WhenAll(services.Select(async service =>
            {
                if (_closeRequested || _closing) return;
                service.Update();
                // Folder discovery is independent of process polling; a slow drive must
                // not hold the global status refresh or a service command on the UI thread.
                _ = service.RefreshApiProjectAvailabilityAsync();
                if (service.IsBusy || service.IsStopping) return;
                try { await service.Runner.RefreshAsync(); }
                catch (Exception ex) { if (!_closeRequested && !_closing) Notice = $"Status check: {ex.Message}"; }
                if (_closeRequested || _closing) return;
                service.Update();
                _ = service.RefreshApiDatabaseAsync();
            }));
            if (_closeRequested || _closing) return;
            LastChecked = $"Checked {DateTime.Now:HH:mm:ss}";
            UpdateActions();
        }
        finally { _refreshing = false; }
    }

    private void UpdateActions()
    {
        RefreshLinkedPortAvailability();
        RefreshServiceConsoleWindows();
        RefreshApiEndpointWindows();
        Changed(nameof(Summary)); Changed(nameof(CanBatch)); Changed(nameof(CanStopBatch)); Changed(nameof(CanEdit));
        Changed(nameof(CanChangeProject)); Changed(nameof(CanEditDetails)); Changed(nameof(CanSaveProjectEdits));
        Changed(nameof(CanRemoveProject)); Changed(nameof(CanCancelProjectEdits));
        Changed(nameof(ProductionNotice)); Changed(nameof(HasProductionNotice));
        NotifyStartAllChanged();
        UpdateArchiveActions();
    }

    private void NotifyStartAllChanged()
    {
        Changed(nameof(AllServicesRunning));
        Changed(nameof(CanStartBatch));
        Changed(nameof(StartAllDescription));
    }

    private void ApiSecrets_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { CanConfigureApi: true } service || _closing || _batchBusy) return;
        ApiSecretsWindow editor;
        try
        {
            editor = new ApiSecretsWindow(service.Directory, service.Name, service.SelectedConfiguration == "Prod") { Owner = this };
            editor.ShowDialog();
        }
        catch (ApiSecretStoreException ex) { Notice = ex.Message; return; }
        catch (Exception) { Notice = "The API configuration editor could not be opened. Check the configured API folder and reopen the launcher."; return; }
        if (editor.SavedChanges)
        {
            foreach (var other in _runners.Values.SelectMany(list => list).Where(item => item.Directory.Equals(service.Directory, StringComparison.OrdinalIgnoreCase)))
            {
                var applied = other.Runner.AppliedConfigurationEnvironment;
                if ((applied == "Prod" && editor.SavedProdChanges) || (applied != "Prod" && editor.SavedLocalChanges))
                    other.Runner.ConfigurationNeedsRestart = true;
                other.Update();
            }
            Notice = "API configuration saved. Use Local or Prod to restart with the selected values.";
            UpdateActions();
        }
    }

    private async void UseApiConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { CanSwitchConfiguration: true } service || _closing || _batchBusy ||
            sender is not Button { Tag: string environment } || environment is not ("Local" or "Prod")) return;
        await RunActionAsync(service, async runner =>
        {
            // Complete validation while the current API is still running; saving can fail without stopping it.
            var configuration = await Task.Run(() => ApiLaunchConfiguration.Prepare(service.Profile, service.Directory, environment));
            var previous = service.Profile.ApiConfiguration;
            service.Profile.ApiConfiguration = new() { Environment = environment };
            try { _store.Save(_settings); }
            catch
            {
                service.Profile.ApiConfiguration = previous;
                throw new InvalidOperationException("The API configuration selection could not be saved. The running API was left unchanged. Reopen the launcher if settings changed elsewhere.");
            }
            service.Update();
            UpdateActions();
            await runner.ApplyConfigurationAsync(configuration);
        }, $"Applying {environment} configuration");
    }

    private async Task<bool> RunActionAsync(ServiceViewModel service, Func<ServiceRunner, Task> action, string verb)
    {
        if (IsEditing || _savingProjectEdits || service.IsBusy || service.IsStopping || _closing || _forceStopBatchBusy || _closeRequested) return false;
        var agentAction = verb == "Starting" ? "start" : verb == "Restarting" || verb.StartsWith("Applying ", StringComparison.Ordinal) ? "restart" : null;
        if (agentAction != null) RecordAgentBridgeEvent(service, agentAction, "requested");
        RecordServiceMessage(service, $"{verb}…", ServiceLogKind.Information);
        service.IsBusy = true;
        UpdateActions();
        Notice = $"{verb}: {service.Name}…";
        try
        {
            await action(service.Runner);
            await service.Runner.RefreshAsync();
            var snapshot = service.Runner.Snapshot;
            Notice = $"{service.Name}: {snapshot.Detail}";
            if (agentAction != null) RecordAgentBridgeEvent(service, agentAction,
                snapshot.State is ServiceState.Error or ServiceState.Conflict ? "failed" : "completed");
            return snapshot.State is not (ServiceState.Error or ServiceState.Conflict);
        }
        catch (Exception ex)
        {
            Notice = $"{service.Name}: {ex.Message}";
            RecordServiceMessage(service, ex.Message, ServiceLogKind.Error);
            if (agentAction != null) RecordAgentBridgeEvent(service, agentAction, "failed");
            return false;
        }
        finally { service.IsBusy = false; UpdateActions(); }
    }

    private async Task RunBatchAsync(Func<ServiceRunner, Task> action, string verb, Func<ServiceViewModel, bool> filter)
    {
        if (!CanBatch) return;
        _batchBusy = true;
        var targets = Services.Where(filter).ToArray();
        UpdateActions();
        try { await Task.WhenAll(targets.Select(service => RunActionAsync(service, action, verb))); }
        finally { _batchBusy = false; UpdateActions(); }
    }

    private static ServiceViewModel? ServiceFrom(object sender) => (sender as FrameworkElement)?.DataContext as ServiceViewModel;
    private async void StartService_Click(object sender, RoutedEventArgs e) { if (ServiceFrom(sender) is { } s) await RunActionAsync(s, r => r.StartAsync(), "Starting"); }
    private async void RestartService_Click(object sender, RoutedEventArgs e) { if (ServiceFrom(sender) is { } s) await RunActionAsync(s, r => r.RestartAsync(), "Restarting"); }
    private async void ResolveConflict_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { CanResolveConflict: true } service) return;
        await RunActionAsync(service, r => r.ResolveConflictAsync(message => !_closing &&
            MessageBox.Show(this, message, $"Resolve conflict — {service.Name}", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes), "Resolving conflict");
    }
    private async void CleanService_Click(object sender, RoutedEventArgs e) { if (ServiceFrom(sender) is { } s) await RunActionAsync(s, r => r.CleanAsync(), "Cleaning"); }
    private async void SetupService_Click(object sender, RoutedEventArgs e) { if (ServiceFrom(sender) is { } s) await RunActionAsync(s, r => r.SetupAsync(), "Installing dependencies"); }
    private async void StopService_Click(object sender, RoutedEventArgs e) { if (ServiceFrom(sender) is { } s) await StopActionAsync(s); }
    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartBatch) return;
        await RunBatchAsync(r => r.StartAsync(), "Starting", s => s.CanStart);
    }
    private async void RestartAll_Click(object sender, RoutedEventArgs e) => await RunBatchAsync(r => r.RestartAsync(), "Restarting", s => s.CanRestart);
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await ForceStopAllAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await Task.WhenAll(RefreshAsync(), RefreshProjectBranchesAsync(), RefreshDeveloperToolsAsync());
    }

    private void CodexAlertsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void ProjectTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_projectTasksWindow == null)
        {
            _projectTasksWindow = new(CaptureProjectApplicationStates) { Owner = this };
            _projectTasksWindow.Closed += (_, _) => _projectTasksWindow = null;
        }
        _projectTasksWindow.ShowProject(SelectedProject, SelectedProject == null ? "" : _store.ResolveRoot(SelectedProject));
        _projectTasksWindow.Show();
        if (_projectTasksWindow.WindowState == WindowState.Minimized) _projectTasksWindow.WindowState = WindowState.Normal;
        _projectTasksWindow.Activate();
    }

    private IReadOnlyList<ProjectTasks.ProjectApplicationState> CaptureProjectApplicationStates(string projectId)
    {
        var project = Projects.FirstOrDefault(candidate => candidate.Id == projectId);
        if (project == null) return [];
        _runners.TryGetValue(project.Id, out var runners);
        return project.Services.Select(profile =>
        {
            var snapshot = runners?.FirstOrDefault(service => service.Profile.Id == profile.Id)?.Runner.Snapshot;
            return new ProjectTasks.ProjectApplicationState(profile.Name, profile.Kind,
                snapshot?.State.ToString() ?? "Unavailable", snapshot?.Detail ?? "No status available",
                snapshot?.ActiveUrl);
        }).ToArray();
    }

    private void CodexAlerts_Click(object sender, RoutedEventArgs e) => OpenCodexMonitor("show");
    private void CodexOverlayShow_Click(object sender, RoutedEventArgs e) => OpenCodexMonitor("show-overlay");
    private void CodexOverlayHide_Click(object sender, RoutedEventArgs e) => OpenCodexMonitor("hide-overlay");
    private void CodexOverlayClear_Click(object sender, RoutedEventArgs e) => OpenCodexMonitor("clear-completed");

    private void OpenCodexMonitor(string command)
    {
        if (_closing || _restartingMonitor) return;
        try
        {
            if (CodexMonitor.CodexMonitorWindow.TrySignalCommand(command))
            {
                Notice = command switch
                {
                    "show-overlay" => "Chat progress is shown above your other windows.",
                    "hide-overlay" => "Chat progress hidden. Monitoring and saved green indicators continue.",
                    "clear-completed" => "Finished chat indicators cleared for watched projects.",
                    _ => "Codex alerts opened. Add or remove watched projects there."
                };
                return;
            }
            var arguments = new List<string> { "--codex-monitor" };
            if (command != "show")
            {
                arguments.Add("--overlay-command");
                arguments.Add(command switch { "show-overlay" => "show", "hide-overlay" => "hide", _ => "clear" });
            }
            if (SelectedProject is not null)
            {
                // A remembered monitor selection is independent of the dashboard profile.
                arguments.Add("--default-project");
                arguments.Add(RootPath);
            }
            AddQueueStoreSettingsArgument(arguments);
            var start = CodexMonitor.MonitorRuntime.CreateStart(arguments.ToArray());
            Process.Start(start)?.Dispose();
            Notice = "Codex alerts opened. The monitor keeps its watched projects; add or remove folders there.";
        }
        catch { Notice = "The Codex monitor could not open. Try launching with --codex-monitor."; }
    }

    private async Task RefreshDeveloperToolsAsync()
    {
        await Task.WhenAll(DeveloperTools.ToArray().Select(async tool =>
        {
            try { tool.SetTarget(await Task.Run(() => DeveloperToolLauncher.Resolve(tool.Profile, _store.BaseDirectory))); }
            catch (Exception ex) { tool.SetTarget(null, ex.Message); }
        }));
    }

    private async void OpenDeveloperTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeveloperToolViewModel tool || tool.IsOpening || _closing) return;
        tool.IsOpening = true;
        try
        {
            // Resolve again so moving/reinstalling a tool while the launcher is open is supported.
            var target = await Task.Run(() => DeveloperToolLauncher.Resolve(tool.Profile, _store.BaseDirectory));
            tool.SetTarget(target);
            DeveloperToolLauncher.Open(target);
            Notice = $"Opened {tool.Name}.";
        }
        catch (Exception ex)
        {
            tool.SetTarget(null, ex.Message);
            Notice = $"Could not open {tool.Name}: {ex.Message}";
            MessageBox.Show(this, ex.Message, $"Open {tool.Name}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { tool.IsOpening = false; }
    }

    private async void ManageTools_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        var editor = new DeveloperToolsWindow(_settings.DeveloperTools, _store.BaseDirectory) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var previous = _settings.DeveloperTools;
        _settings.DeveloperTools = editor.Result;
        try { _store.Save(_settings); }
        catch (Exception ex) { _settings.DeveloperTools = previous; ShowSaveError(ex); return; }
        DeveloperTools.Clear();
        foreach (var tool in _settings.DeveloperTools) DeveloperTools.Add(new(tool));
        await RefreshDeveloperToolsAsync();
        Notice = "Developer tools saved for all projects.";
    }

    private async Task StopActionAsync(ServiceViewModel service, bool includeConflicts = false)
    {
        if (service.IsStopping || _closing || _savingProjectEdits) return;
        RecordAgentBridgeEvent(service, "stop", "requested");
        RecordServiceMessage(service, "Force stop requested…", ServiceLogKind.Information);
        service.IsStopping = true;
        UpdateActions();
        try
        {
            if (includeConflicts)
                await service.Runner.ForceStopAsync(message => !_closing && MessageBox.Show(this, message,
                    $"Force stop — {service.Name}", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes);
            else await service.Runner.ForceStopAsync();
            Notice = $"{service.Name}: {service.Runner.Snapshot.Detail}";
            var snapshot = service.Runner.Snapshot;
            RecordAgentBridgeEvent(service, "stop", (snapshot.State is ServiceState.Stopped or ServiceState.Completed) &&
                !service.Runner.HasManagedProcess && snapshot.ProcessIds.Count == 0 ? "completed" : "failed");
        }
        catch (Exception ex)
        {
            Notice = $"Could not stop {service.Name}: {ex.Message}";
            RecordServiceMessage(service, ex.Message, ServiceLogKind.Error);
            RecordAgentBridgeEvent(service, "stop", "failed");
        }
        finally { service.IsStopping = false; UpdateActions(); }
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { HasLiveUrl: true } service) return;
        try
        {
            if (!Uri.TryCreate(service.LiveUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.IsLoopback)
                throw new InvalidOperationException("Only a local HTTP or HTTPS service address can be opened.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = $"Could not open URL: {ex.Message}"; }
    }

    private void CopyServiceUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { HasLiveUrl: true } service) return;
        try
        {
            if (!Uri.TryCreate(service.LiveUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.IsLoopback)
                throw new InvalidOperationException("Only a local HTTP or HTTPS service address can be copied.");
            Clipboard.SetText(uri.AbsoluteUri);
            Notice = $"Copied {service.Name} address.";
        }
        catch (Exception ex) { Notice = $"Could not copy URL: {ex.Message}"; }
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e) => await AddProjectAsync();

    private async Task AddProjectAsync()
    {
        if (!CanChangeProject) return;
        var setup = new NewProjectWindow(_store.BaseDirectory, _settings.Projects) { Owner = this };
        if (setup.ShowDialog() != true) return;
        if (setup.ExistingProjectId is { } existingProjectId)
        {
            var existing = Projects.FirstOrDefault(project =>
                project.Id.Equals(existingProjectId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Notice = "The saved project is no longer available. Reopen New project and scan again.";
                return;
            }
            _showArchivedProjects = existing.IsArchived;
            await RefreshProjectListAsync(existing);
            return;
        }
        var profile = setup.Result;
        var editor = new ProfileEditorWindow(profile, _store.BaseDirectory, _settings.Projects) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var result = editor.Result;
        AngularDevProxyConfiguration proxyChanges;
        try
        {
            proxyChanges = AngularDevProxyConfiguration.Prepare(new ProjectProfile
            {
                Id = result.Id, Name = result.Name, RootPath = result.RootPath
            }, result, _store);
            _settings.Projects.Add(result);
            proxyChanges.SaveWithSettings(() => _store.Save(_settings));
        }
        catch (Exception ex) { _settings.Projects.Remove(result); ShowSaveError(ex); return; }
        Projects.Add(result);
        ProjectItems.Add(new(result, GetRunners(result)));
        _showArchivedProjects = false;
        await RefreshProjectListAsync(result);
        Notice = $"Saved {result.Name}.";
        if (proxyChanges.UpdatedFileCount > 0)
            Notice += " Angular's development API proxy was updated. Start the frontend to use the saved API target.";
    }

    private async void OpenProjectDetails()
    {
        if (!CanEdit || IsEditing || SelectedProject is not { } selected) return;
        if (Services.Any(s => s.Runner.HasManagedProcess || s.Runner.Snapshot.ProcessIds.Count > 0))
        {
            Notice = "Stop this project's services before changing its paths or commands.";
            return;
        }
        // A modal dialog still pumps the dispatcher. Keep dashboard and bridge service
        // actions blocked until fresh process checks and the settings/proxy save finish.
        SetProjectEditMode(true);
        var settingsSaved = false;
        try
        {
            var editor = new ProfileEditorWindow(selected, _store.BaseDirectory, _settings.Projects) { Owner = this };
            if (editor.ShowDialog() != true) return;
            var result = editor.Result;
            var services = Services.ToArray();
            SetSavingProjectEdits(true);
            await Task.WhenAll(services.Select(service => service.Runner.RefreshForProfileEditAsync()));
            if (!ReferenceEquals(selected, SelectedProject) || !services.SequenceEqual(Services))
                throw new InvalidOperationException("The selected project changed while its settings were open. Reopen project settings and retry.");
            foreach (var service in services)
            {
                service.Update();
                if (service.IsBusy || service.IsStopping || service.Runner.HasManagedProcess ||
                    !service.Runner.HasVerifiedNoServiceProcesses || service.Runner.Snapshot.ProcessIds.Count != 0)
                    throw new InvalidOperationException($"Stop {service.Name} and complete a fresh process check before saving project settings.");
            }
            var changedApiIds = selected.Services.Where(api =>
                SettingsStore.IsLinkableApiService(api) &&
                result.Services.FirstOrDefault(next => next.Id.Equals(api.Id, StringComparison.OrdinalIgnoreCase)) is { } next &&
                new Uri(api.Url).GetLeftPart(UriPartial.Authority) !=
                new Uri(next.Url).GetLeftPart(UriPartial.Authority))
                .Select(api => api.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var apiId in changedApiIds)
            {
                var previousApi = selected.Services.Single(api => api.Id.Equals(apiId, StringComparison.OrdinalIgnoreCase));
                var nextApi = result.Services.Single(api => api.Id.Equals(apiId, StringComparison.OrdinalIgnoreCase));
                var beforeAddress = new Uri(previousApi.Url);
                var afterAddress = new Uri(nextApi.Url);
                if (!_store.ResolveWorkingDirectory(selected, previousApi).Equals(
                    _store.ResolveWorkingDirectory(result, nextApi), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Save the API folder change first, then edit its address with both services stopped.");
                foreach (var frontend in result.Services.Where(service =>
                    string.Equals(service.ApiTargetServiceId, apiId, StringComparison.OrdinalIgnoreCase)))
                {
                    var previous = selected.Services.FirstOrDefault(service =>
                        service.Id.Equals(frontend.Id, StringComparison.OrdinalIgnoreCase));
                    if (previous is null || !_store.ResolveWorkingDirectory(selected, previous).Equals(
                        _store.ResolveWorkingDirectory(result, frontend), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Save the Angular service and its folder first, then edit the linked API address with both services stopped.");
                }
                if (beforeAddress.Port != afterAddress.Port)
                {
                    var conflict = _settings.Projects.Where(project => project.Id != selected.Id)
                        .SelectMany(project => project.Services).Concat(result.Services)
                        .FirstOrDefault(other => !other.Id.Equals(apiId, StringComparison.OrdinalIgnoreCase) &&
                            ServicePortConfiguration.GetPort(other) == afterAddress.Port);
                    if (conflict is not null)
                        throw new InvalidOperationException($"Port {afterAddress.Port} is already assigned to {conflict.Name}. Choose another API port.");
                }
                if (nextApi.ApiConfiguration is null && nextApi.StartCommand == previousApi.StartCommand)
                {
                    if (beforeAddress.Scheme != afterAddress.Scheme || beforeAddress.Host != afterAddress.Host)
                        throw new InvalidOperationException("Update the API start command together with its HTTP scheme or host in project settings.");
                    var rewritten = JsonSerializer.Deserialize<ServiceProfile>(JsonSerializer.Serialize(previousApi))!;
                    ServicePortConfiguration.Apply(rewritten, _store.ResolveWorkingDirectory(selected, previousApi),
                        afterAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    nextApi.StartCommand = rewritten.StartCommand;
                }
            }

            var candidateSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            var index = candidateSettings.Projects.FindIndex(project => project.Id == selected.Id);
            if (index < 0) throw new InvalidOperationException("The selected project was removed while its settings were open.");
            candidateSettings.Projects[index] = result;
            var proxyChanges = AngularDevProxyConfiguration.Prepare(selected, result, _store);
            proxyChanges.SaveWithSettings(() => _store.Save(candidateSettings));
            settingsSaved = true;

            _settings.Projects[_settings.Projects.IndexOf(selected)] = result;
            if (_runners.Remove(selected.Id, out var old)) foreach (var service in old) service.Runner.Dispose();
            Projects[Projects.IndexOf(selected)] = result;
            _refreshingProjectList = true;
            try
            {
                var previousItem = ProjectItems.First(item => item.Profile == selected);
                var itemIndex = ProjectItems.IndexOf(previousItem);
                ProjectItems[itemIndex] = new(result, GetRunners(result))
                {
                    IsDetailsExpanded = previousItem.IsDetailsExpanded
                };
            }
            finally { _refreshingProjectList = false; }
            await RefreshProjectListAsync(result);
            Notice = $"Saved {result.Name}.";
            if (proxyChanges.UpdatedFileCount > 0)
                Notice += " Angular's development API proxy was updated. Start the frontend to use the saved API target.";
        }
        catch (Exception ex)
        {
            if (settingsSaved)
            {
                Notice = $"Project settings were saved, but the dashboard could not refresh: {ex.Message}";
                MessageBox.Show(this, Notice, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else ShowSaveError(ex);
        }
        finally
        {
            if (_savingProjectEdits) SetSavingProjectEdits(false);
            if (IsEditing) SetProjectEditMode(false);
        }
    }

    private void ShowSaveError(Exception ex)
    {
        Notice = $"Settings could not be saved: {ex.Message}";
        MessageBox.Show(this, Notice, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || IsEditing || SelectedProject is not { } selected) return;
        if (Services.Any(s => s.Runner.HasManagedProcess || s.Runner.Snapshot.ProcessIds.Count > 0))
        {
            Notice = "Stop this project's services before removing its saved profile.";
            return;
        }
        if (MessageBox.Show(this, $"Remove the saved profile for {selected.Name}? Project files stay on disk.",
                "Remove project", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_projectTasksWindow?.PrepareToRemoveProject(selected.Id) == false) return;
        var index = _settings.Projects.IndexOf(selected);
        _settings.Projects.RemoveAt(index);
        var previousSelection = _settings.SelectedProjectId;
        _settings.SelectedProjectId = _settings.Projects.FirstOrDefault(project => project.IsArchived == ShowArchivedProjects)?.Id;
        try { _store.Save(_settings); }
        catch (Exception ex) { _settings.Projects.Insert(index, selected); _settings.SelectedProjectId = previousSelection; ShowSaveError(ex); return; }
        if (_runners.Remove(selected.Id, out var old)) foreach (var service in old) service.Runner.Dispose();
        Projects.Remove(selected);
        _refreshingProjectList = true;
        try { ProjectItems.Remove(ProjectItems.First(item => item.Profile == selected)); }
        finally { _refreshingProjectList = false; }
        await RefreshProjectListAsync(saveSelection: false);
        Notice = $"Removed the saved profile for {selected.Name}.";
    }

    private void SetSectionVisibility(string property, bool visible)
    {
        var current = property switch
        {
            nameof(ProjectsVisible) => ProjectsVisible, nameof(ToolsVisible) => ToolsVisible,
            nameof(ServicesVisible) => ServicesVisible, nameof(NextCommitVisible) => NextCommitVisible, _ => visible
        };
        if (current == visible) return;
        switch (property)
        {
            case nameof(ProjectsVisible): _settings.Layout.ProjectsVisible = visible; break;
            case nameof(ToolsVisible): _settings.Layout.ToolsVisible = visible; break;
            case nameof(ServicesVisible): _settings.Layout.ServicesVisible = visible; break;
            case nameof(NextCommitVisible): _settings.Layout.NextCommitVisible = visible; break;
        }
        ApplyLayout();
        Changed(property);
        SaveLayout();
        if (property == nameof(NextCommitVisible) && visible) _ = RefreshNextCommitAsync();
    }

    private void ApplyLayout()
    {
        if (!_layoutReady) return;
        static Visibility Display(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
        var sidebarVisible = ProjectsVisible || ToolsVisible;
        SidebarPanel.Visibility = Display(sidebarVisible);
        SidebarColumn.Width = new GridLength(sidebarVisible ? 245 : 0);
        ProjectsPanel.Visibility = Display(ProjectsVisible);
        ProjectsRow.Height = ProjectsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ToolsPanel.Visibility = Display(ToolsVisible);
        ToolsRow.Height = !ToolsVisible ? new GridLength(0) : ProjectsVisible ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ToolsScroller.MaxHeight = ProjectsVisible ? 250 : double.PositiveInfinity;

        ServicesPanel.Visibility = Display(ServicesVisible);
        BatchControlsPanel.Visibility = Display(ServicesVisible || IsEditing);
        EmptyWorkspace.Visibility = Display(!ServicesVisible);
        NextCommitPanel.Visibility = Display(NextCommitVisible);
        NextCommitSplitter.Visibility = Display(NextCommitVisible);
        NextCommitColumn.MinWidth = NextCommitVisible ? 280 : 0;
        if (!NextCommitVisible) NextCommitColumn.Width = new GridLength(0);
        else if (NextCommitColumn.Width.Value == 0) NextCommitColumn.Width = new GridLength(402);
        NextCommitSplitterColumn.Width = new GridLength(NextCommitVisible ? 10 : 0);
        RepositionSectionsMenu();
    }

    private void SaveLayout()
    {
        if (!_layoutReady) return;
        try { _store.Save(_settings); }
        catch (Exception ex) { Notice = $"Layout changed for this session, but could not be saved: {ex.Message}"; }
    }

    private void HideSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        SetSectionVisibility(section + "Visible", false);
        // Keep keyboard focus on a visible control that can immediately reopen the section.
        SectionsMenuToggle.Focus();
    }

    private void ShowServices_Click(object sender, RoutedEventArgs e)
    {
        ServicesVisible = true;
        SectionsMenuToggle.Focus();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
