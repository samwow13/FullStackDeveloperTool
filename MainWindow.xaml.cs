using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    public bool ConsoleVisible { get => _settings.Layout.ConsoleVisible; set => SetSectionVisibility(nameof(ConsoleVisible), value); }
    public bool DatabaseVisible { get => _settings.Layout.DatabaseVisible; set => SetSectionVisibility(nameof(DatabaseVisible), value); }
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
        _consoleTimer.Tick += (_, _) => FlushConsoleOutput();
        _layoutReady = true;
        ApplyLayout();
        DataContext = this;
        InitializeLongRunningTaskMode();
        DatabasePanel.DatabaseSelected += SaveDatabaseSelection;
        DatabasePanel.HideRequested += (_, _) =>
        {
            DatabaseVisible = false;
            DatabaseToggle.Focus();
        };
        _timer.Tick += async (_, _) => await Task.WhenAll(RefreshAsync(), RefreshProjectBranchesAsync());
        Activated += async (_, _) => await RefreshProjectBranchesAsync();
        Closed += (_, _) => _branchLifetime.Cancel();
        SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
            runner.LogReceived += log => QueueLog(project.Id, profile.Name, log);
            return new ServiceViewModel(runner);
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
            _projectTasksWindow?.ShowProject(null, "");
            Services.Clear();
            foreach (var name in new[] { nameof(SelectedProject), nameof(SelectedProjectItem), nameof(RootPath), nameof(ConsoleLines) }) Changed(name);
            UpdateActions();
            await DatabasePanel.ShowProjectAsync(null, Projects, _store);
            return;
        }
        SelectedProject = project;
        _projectTasksWindow?.ShowProject(project, _store.ResolveRoot(project));
        Services.Clear();
        foreach (var service in GetRunners(project)) Services.Add(service);
        if (saveSelection)
        {
            _settings.SelectedProjectId = project.Id;
            try { _store.Save(_settings); }
            catch (Exception ex) { Notice = $"Settings could not be saved: {ex.Message}"; }
        }
        foreach (var name in new[] { nameof(SelectedProject), nameof(SelectedProjectItem), nameof(RootPath), nameof(Summary), nameof(CanBatch), nameof(CanStopBatch), nameof(CanEdit), nameof(ConsoleLines) }) Changed(name);
        UpdateArchiveActions();
        _ = RefreshProjectBranchesAsync();
        await Task.WhenAll(RefreshAsync(), DatabasePanel.ShowProjectAsync(project, Projects, _store));
    }

    private void SaveDatabaseSelection(ProjectProfile project, DatabaseSelection selection)
    {
        if (project.Database?.SourceId == selection.SourceId && project.Database.DatabaseName == selection.DatabaseName) return;
        var previous = project.Database;
        project.Database = selection;
        try { _store.Save(_settings); }
        catch (Exception ex)
        {
            project.Database = previous;
            Notice = $"The database is available for this session, but its selection could not be saved: {ex.Message}";
        }
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
            }));
            if (_closeRequested || _closing) return;
            LastChecked = $"Checked {DateTime.Now:HH:mm:ss}";
            UpdateActions();
        }
        finally { _refreshing = false; }
    }

    private void UpdateActions()
    {
        Changed(nameof(Summary)); Changed(nameof(CanBatch)); Changed(nameof(CanStopBatch)); Changed(nameof(CanEdit));
        Changed(nameof(CanChangeProject)); Changed(nameof(CanEditDetails)); Changed(nameof(CanSaveProjectEdits));
        Changed(nameof(CanRemoveProject)); Changed(nameof(CanCancelProjectEdits));
        Changed(nameof(ProductionNotice)); Changed(nameof(HasProductionNotice));
        UpdateArchiveActions();
    }

    private async void ApiSecrets_Click(object sender, RoutedEventArgs e)
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
            await DatabasePanel.ShowProjectAsync(SelectedProject, Projects, _store);
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
        await DatabasePanel.ShowProjectAsync(SelectedProject, Projects, _store);
    }

    private async Task RunActionAsync(ServiceViewModel service, Func<ServiceRunner, Task> action, string verb)
    {
        if (IsEditing || service.IsBusy || service.IsStopping || _closing || _forceStopBatchBusy) return;
        RevealServiceConsole(service);
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
        }
        catch (Exception ex)
        {
            Notice = $"{service.Name}: {ex.Message}";
            RecordServiceMessage(service, ex.Message, ServiceLogKind.Error);
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
    private async void StartAll_Click(object sender, RoutedEventArgs e) => await RunBatchAsync(r => r.StartAsync(), "Starting", s => s.CanStart);
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
            _projectTasksWindow = new() { Owner = this };
            _projectTasksWindow.Closed += (_, _) => _projectTasksWindow = null;
        }
        _projectTasksWindow.ShowProject(SelectedProject, SelectedProject == null ? "" : _store.ResolveRoot(SelectedProject));
        _projectTasksWindow.Show();
        if (_projectTasksWindow.WindowState == WindowState.Minimized) _projectTasksWindow.WindowState = WindowState.Normal;
        _projectTasksWindow.Activate();
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
        RevealServiceConsole(service);
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
        }
        catch (Exception ex)
        {
            Notice = $"Could not stop {service.Name}: {ex.Message}";
            RecordServiceMessage(service, ex.Message, ServiceLogKind.Error);
        }
        finally { service.IsStopping = false; UpdateActions(); }
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { CanOpen: true } service) return;
        try
        {
            if (!Uri.TryCreate(service.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.IsLoopback)
                throw new InvalidOperationException("Only a local HTTP or HTTPS service address can be opened.");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Notice = $"Could not open URL: {ex.Message}"; }
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e) => await AddProjectAsync(consoleApp: false);

    private async void AddConsoleProject_Click(object sender, RoutedEventArgs e) => await AddProjectAsync(consoleApp: true);

    private async Task AddProjectAsync(bool consoleApp)
    {
        if (!CanChangeProject) return;
        var profile = new ProjectProfile
        {
            Name = consoleApp ? "My console project" : "My full stack project",
            RootPath = _store.BaseDirectory,
            Services = consoleApp ?
            [
                new() { Name = "Console app", Kind = "Console", WorkingDirectory = ".", StartCommand = "dotnet run", Url = "", UiPath = "" }
            ] :
            [
                new() { Name = "Backend API", Kind = ".NET", WorkingDirectory = ".", StartCommand = "dotnet run", CleanCommand = "dotnet clean", SetupCommand = "dotnet restore", Url = "http://localhost:5000", UiPath = "/swagger" },
                new() { Name = "Frontend", Kind = "Angular", WorkingDirectory = ".", StartCommand = "npm start -- --port 4200", CleanCommand = "npm exec -- ng cache clean", SetupCommand = "npm install", Url = "http://localhost:4200" }
            ]
        };
        var editor = new ProfileEditorWindow(profile, _store.BaseDirectory) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var result = editor.Result;
        _settings.Projects.Add(result);
        try { _store.Save(_settings); }
        catch (Exception ex) { _settings.Projects.Remove(result); ShowSaveError(ex); return; }
        Projects.Add(result);
        ProjectItems.Add(new(result, GetRunners(result)));
        _showArchivedProjects = false;
        await RefreshProjectListAsync(result);
        Notice = $"Saved {result.Name}.";
    }

    private async void OpenProjectDetails()
    {
        if (!CanEdit || IsEditing || SelectedProject is not { } selected) return;
        if (Services.Any(s => s.Runner.HasManagedProcess || s.Runner.Snapshot.ProcessIds.Count > 0))
        {
            Notice = "Stop this project's services before changing its paths or commands.";
            return;
        }
        var editor = new ProfileEditorWindow(selected, _store.BaseDirectory) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var result = editor.Result;
        var index = _settings.Projects.IndexOf(selected);
        _settings.Projects[index] = result;
        try { _store.Save(_settings); }
        catch (Exception ex) { _settings.Projects[index] = selected; ShowSaveError(ex); return; }
        if (_runners.Remove(selected.Id, out var old)) foreach (var service in old) service.Runner.Dispose();
        Projects[Projects.IndexOf(selected)] = result;
        _refreshingProjectList = true;
        try
        {
            var itemIndex = ProjectItems.IndexOf(ProjectItems.First(item => item.Profile == selected));
            ProjectItems[itemIndex] = new(result, GetRunners(result));
        }
        finally { _refreshingProjectList = false; }
        await RefreshProjectListAsync(result);
        Notice = $"Saved {result.Name}.";
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
        _logs.Remove(selected.Id);
        await RefreshProjectListAsync(saveSelection: false);
        Notice = $"Removed the saved profile for {selected.Name}.";
    }

    private void SetSectionVisibility(string property, bool visible)
    {
        var current = property switch
        {
            nameof(ProjectsVisible) => ProjectsVisible, nameof(ToolsVisible) => ToolsVisible,
            nameof(ServicesVisible) => ServicesVisible, nameof(ConsoleVisible) => ConsoleVisible,
            nameof(DatabaseVisible) => DatabaseVisible, _ => visible
        };
        if (current == visible) return;
        RememberPanelSizes();
        switch (property)
        {
            case nameof(ProjectsVisible): _settings.Layout.ProjectsVisible = visible; break;
            case nameof(ToolsVisible): _settings.Layout.ToolsVisible = visible; break;
            case nameof(ServicesVisible): _settings.Layout.ServicesVisible = visible; break;
            case nameof(ConsoleVisible): _settings.Layout.ConsoleVisible = visible; break;
            case nameof(DatabaseVisible): _settings.Layout.DatabaseVisible = visible; break;
        }
        ApplyLayout();
        Changed(property);
        SaveLayout();
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

        var upperVisible = ServicesVisible || ConsoleVisible;
        var columnsSplit = ServicesVisible && ConsoleVisible;
        ServicesPanel.Visibility = Display(ServicesVisible);
        BatchControlsPanel.Visibility = Display(ServicesVisible || IsEditing);
        ConsolePanel.Visibility = Display(ConsoleVisible);
        ServicesColumn.MinWidth = ServicesVisible ? 320 : 0;
        ConsoleColumn.MinWidth = ConsoleVisible ? 240 : 0;
        ServicesColumn.Width = ServicesVisible ? new GridLength(columnsSplit ? 1 - _settings.Layout.ConsoleShare : 1, GridUnitType.Star) : new GridLength(0);
        ConsoleColumn.Width = ConsoleVisible ? new GridLength(columnsSplit ? _settings.Layout.ConsoleShare : 1, GridUnitType.Star) : new GridLength(0);
        ConsoleDividerColumn.Width = new GridLength(columnsSplit ? 10 : 0);
        ConsoleDivider.Visibility = Display(columnsSplit);
        ServiceConsoleGrid.Visibility = Display(upperVisible);

        var rowsSplit = upperVisible && DatabaseVisible;
        ServiceConsoleRow.MinHeight = upperVisible ? 140 : 0;
        DatabaseRow.MinHeight = DatabaseVisible ? (IsEditing ? 140 : 270) : 0;
        ServiceConsoleRow.Height = upperVisible ? new GridLength(rowsSplit ? _settings.Layout.ServicesHeightShare : 1, GridUnitType.Star) : new GridLength(0);
        DatabaseRow.Height = DatabaseVisible ? new GridLength(rowsSplit ? 1 - _settings.Layout.ServicesHeightShare : 1, GridUnitType.Star) : new GridLength(0);
        WorkspaceDividerRow.Height = new GridLength(rowsSplit ? 10 : 0);
        WorkspaceDivider.Visibility = Display(rowsSplit);
        DatabaseSection.Visibility = Display(DatabaseVisible);
        EmptyWorkspace.Visibility = Display(!upperVisible && !DatabaseVisible);
        // Give the empty-state message a real row even when every workspace section is hidden.
        if (!upperVisible && !DatabaseVisible) ServiceConsoleRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void RememberPanelSizes()
    {
        if (!_layoutReady || !IsLoaded) return;
        if (ServicesVisible && ConsoleVisible && ServicesColumn.ActualWidth + ConsoleColumn.ActualWidth > 0)
            _settings.Layout.ConsoleShare = ConsoleColumn.ActualWidth / (ServicesColumn.ActualWidth + ConsoleColumn.ActualWidth);
        if ((ServicesVisible || ConsoleVisible) && DatabaseVisible && ServiceConsoleRow.ActualHeight + DatabaseRow.ActualHeight > 0)
            _settings.Layout.ServicesHeightShare = ServiceConsoleRow.ActualHeight / (ServiceConsoleRow.ActualHeight + DatabaseRow.ActualHeight);
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
        (section switch
        {
            "Projects" => ProjectsToggle, "Tools" => ToolsToggle, "Services" => ServicesToggle,
            "Console" => ConsoleToggle, "Database" => DatabaseToggle, _ => null
        })?.Focus();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _settings.Layout = new WorkspaceLayout();
        ApplyLayout();
        foreach (var property in new[] { nameof(ProjectsVisible), nameof(ToolsVisible), nameof(ServicesVisible), nameof(ConsoleVisible), nameof(DatabaseVisible) }) Changed(property);
        Notice = "All sections are visible. Panel sizes restored.";
        SaveLayout();
        ProjectsToggle.Focus();
    }

    private void LayoutDivider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled) return;
        // Preview splitters commit their GridLengths during DragCompleted; arrange before reading actual sizes.
        UpdateLayout();
        RememberPanelSizes();
        SaveLayout();
    }

    private void LayoutDivider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RememberPanelSizes();
        SaveLayout();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
