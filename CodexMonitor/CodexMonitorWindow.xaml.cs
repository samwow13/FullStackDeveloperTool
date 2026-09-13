using System.ComponentModel;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FullStackLauncher.CodexMonitor;

public partial class CodexMonitorWindow : Window
{
    internal const string InstanceName = @"Local\Cascade.FullStackLauncher.CodexMonitor";
    private readonly Mutex _instance;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showRegistration;
    private readonly LocalCodexReader _reader;
    private readonly Dictionary<string, ProjectWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    // Preserve removed scopes long enough to hand families to an overlapping watch.
    // Only their minimal completion records are persisted; they are never polled or displayed.
    private readonly Dictionary<string, ProjectWatch> _retiredWatches = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ChatProgressRow> _progressRows = [];
    private readonly ChatProgressWindow _overlay;
    private readonly MonitorPreferences _preferences;
    private readonly bool _firstUse;
    private readonly List<(EventWaitHandle Signal, RegisteredWaitHandle Registration)> _commandListeners = [];
    private readonly DispatcherTimer _preferencesTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _soundMenu;
    private readonly Forms.ToolStripMenuItem _overlayMenu;
    private readonly SoundPlayer _sound;
    private readonly MemoryStream _soundStream;
    private readonly string _codexHome;
    private readonly string? _diagnosticsFile;
    private readonly string _settingsPath;
    private string? _project;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _exiting;
    private bool _released;
    private bool _settingsWarning;
    private long _generation;
    private string? _savedCompletedJson;
    private bool _preferencesUnreadable;

    public static async void Open(string[] args)
    {
        var command = Option(args, "--overlay-command") switch
        {
            "show" => "show-overlay", "hide" => "hide-overlay", "clear" => "clear-completed", _ => "show"
        };
        if (args.Contains("--background", StringComparer.OrdinalIgnoreCase)) command = "resume";
        if (args.Contains("--exit-monitor", StringComparer.OrdinalIgnoreCase))
        {
            TrySignalCommand("exit-monitor");
            Application.Current.Shutdown();
            return;
        }
        // Forward controls first; only a new monitor needs a private runtime copy.
        if (TrySignalCommand(command))
        {
            Application.Current.Shutdown();
            return;
        }
        try
        {
            if (MonitorRuntime.PrepareStart(args) is { } start)
            {
                using var process = System.Diagnostics.Process.Start(start)
                    ?? throw new IOException("The monitor process could not start.");
                Application.Current.Shutdown();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("The monitor could not prepare its separate runtime copy. Finish any launcher build and try again.\n\n" + ex.Message,
                "Codex alerts", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
            return;
        }
        var instance = new Mutex(false, InstanceName);
        bool acquired;
        try { acquired = instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            if (!TrySignalCommand(command))
                MessageBox.Show("The running monitor needs to be reopened to use these controls. Exit it from the tray, then open Codex alerts again.", "Codex alerts");
            instance.Dispose();
            Application.Current.Shutdown();
            return;
        }
        CodexMonitorWindow? window = null;
        try
        {
            window = new CodexMonitorWindow(instance, args);
            Application.Current.MainWindow = window;
            if (command == "show") window.Show();
            else if (command != "clear-completed") window.HandleCommand(command);
            await window.InitializeAsync(args);
            if (command == "clear-completed") window.HandleCommand(command);
        }
        catch
        {
            MessageBox.Show("The Codex monitor could not open. Try restarting it.", "Codex alerts", MessageBoxButton.OK, MessageBoxImage.Error);
            if (window is not null) window.ExitMonitor();
            else
            {
                instance.ReleaseMutex();
                instance.Dispose();
                Application.Current.Shutdown(1);
            }
        }
    }

    private CodexMonitorWindow(Mutex instance, string[] args)
    {
        _instance = instance;
        _codexHome = Option(args, "--codex-home") ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _diagnosticsFile = Option(args, "--diagnostics-file");
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullStackLauncher", "codex-monitor.settings.json");
        _preferences = LoadPreferences();
        _firstUse = _preferences.WatchedProjectPaths is null;
        _project = _preferences.ProjectPath;
        foreach (var path in _preferences.WatchedProjectPaths ?? []) AddWatch(path);
        foreach (var path in _preferences.CompletedChats.Select(record => record.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!_watches.ContainsKey(path)) _retiredWatches[path] = new ProjectWatch(path, _preferences.CompletedChats);
        _savedCompletedJson = JsonSerializer.Serialize(ExportCompleted());
        _reader = new LocalCodexReader(_codexHome);
        InitializeComponent();
        _overlay = new ChatProgressWindow();
        RestoreOverlayBounds();
        _overlay.HideRequested += (_, _) => SetOverlayVisible(false);
        _overlay.ClearCompletedRequested += ClearCompleted;
        _overlay.BoundsChanged += (_, _) =>
        {
            RememberOverlayBounds();
            _preferencesTimer.Stop();
            _preferencesTimer.Start();
        };
        _preferencesTimer.Tick += (_, _) => { _preferencesTimer.Stop(); SaveProject(); };
        SourceText.Text = "Local status • " + _codexHome;
        SourceText.ToolTip = "Uses Codex's internal local status format. An incompatible Codex update pauses alerts. Remote/cloud tasks are not monitored.";

        _soundStream = new MemoryStream(CreateChime());
        _sound = new SoundPlayer(_soundStream);
        using var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/launcher.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconResource),
            Text = "Codex alerts — connecting",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open monitor", null, (_, _) => ShowMonitor());
        _overlayMenu = new Forms.ToolStripMenuItem("Show chat progress", null, (_, _) => SetOverlayVisible(!_preferences.OverlayVisible));
        _tray.ContextMenuStrip.Items.Add(_overlayMenu);
        _tray.ContextMenuStrip.Items.Add("Clear finished chats", null, (_, _) => ClearCompleted(null));
        _tray.ContextMenuStrip.Items.Add("Dismiss reminder", null, (_, _) => Dismiss());
        _soundMenu = new Forms.ToolStripMenuItem("Enable Sounds", null, (_, _) => SetReminderSound(!_preferences.ReminderSoundEnabled));
        _tray.ContextMenuStrip.Items.Add(_soundMenu);
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit monitor", null, (_, _) => ExitMonitor());
        _tray.DoubleClick += (_, _) => ShowMonitor();
        _tray.BalloonTipClicked += (_, _) => ShowMonitor();
        UpdateSoundControls();
        UpdateWatchedProjects();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            if (!_exiting) Dispatcher.BeginInvoke(ShowMonitor);
        }, null, Timeout.Infinite, false);
        foreach (var command in new[] { "show-overlay", "hide-overlay", "clear-completed", "exit-monitor", "resume" })
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, CommandEventName(command));
            var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            {
                if (!_exiting) Dispatcher.BeginInvoke(() => HandleCommand(command));
            }, null, Timeout.Infinite, false);
            _commandListeners.Add((signal, registration));
        }
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => ReleaseResources();
    }

    private async Task InitializeAsync(string[] args)
    {
        var generation = _generation;
        var explicitProject = Option(args, "--project");
        var firstUse = _firstUse;
        var preferred = explicitProject ?? _preferences.ProjectPath;
        await LoadProjectsAsync(preferred ?? (firstUse ? Option(args, "--default-project") : null), matchParentFolder: firstUse && preferred is null);
        if (_exiting) return;
        if (generation == _generation && (explicitProject is not null || firstUse) && !string.IsNullOrWhiteSpace(_project)) AddWatch(_project);
        UpdateWatchedProjects();
        SetOverlayVisible(_preferences.OverlayVisible);
        UpdateProgressDisplay();
        _timer.Start();
        await RefreshAsync();
    }

    private async Task LoadProjectsAsync(string? preferred, bool matchParentFolder = false)
    {
        var generation = _generation;
        RememberProjectPaths([preferred]);
        ProjectPicker.ItemsSource = _preferences.ProjectPaths.ToArray();
        ProjectPicker.Text = preferred ?? "";
        try
        {
            var paths = await Task.Run(_reader.GetProjectPaths);
            if (_exiting || generation != _generation) return;
            RememberProjectPaths(paths);
            ProjectPicker.ItemsSource = _preferences.ProjectPaths.ToArray();
            // A dashboard suggestion can be a subfolder of the Codex workspace.
            // An explicit or remembered selection must retain its exact scope.
            var selected = matchParentFolder && preferred is not null
                ? paths.Where(p => SameOrChild(preferred, p)).OrderByDescending(p => p.Length).FirstOrDefault()
                : null;
            _project = selected ?? preferred ?? paths.FirstOrDefault();
            ProjectPicker.Text = _project ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_exiting || generation != _generation) return;
            _project = preferred;
            ProjectPicker.Text = preferred ?? "";
            StatusText.Text = "Local Codex status is unavailable.";
            DetailText.Text = "Open Codex and choose the folder used by your tasks. The monitor will retry automatically.";
        }
    }

    private async Task RefreshAsync()
    {
        if (_exiting) return;
        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                await RefreshSnapshotAsync();
            } while (_refreshPending && !_exiting);
        }
        finally { _refreshing = false; }
    }

    private async Task RefreshSnapshotAsync()
    {
        var generation = _generation;
        try
        {
            if (_watches.Count == 0)
            {
                UpdateProgressDisplay();
                AgentList.ItemsSource = null;
                StatusText.Text = "Choose a Codex project folder to watch.";
                DetailText.Text = "Add one or more folders above. Removing a project stops watching it; its tasks continue running.";
                CheckedText.Text = "No projects watched";
                SetTrayText("Codex alerts — no projects watched");
                WriteDiagnostics(false, [], StatusText.Text);
                return;
            }
            var projects = _watches.Keys.ToArray();
            var snapshot = await Task.Run(() => _reader.ReadStatus(projects));
            if (_exiting || generation != _generation) return;
            var snapshots = snapshot.Projects;
            var now = DateTimeOffset.UtcNow;
            var updates = new List<ReminderUpdate>();
            var agentRows = new List<MonitoredAgentRow>();
            var owners = snapshots.SelectMany(project => project.Value.Select(agent => (agent.Id, project.Key)))
                .ToDictionary(item => item.Id, item => item.Key, StringComparer.Ordinal);
            foreach (var (path, agentsInProject) in snapshots)
                foreach (var agent in agentsInProject)
                    if (!string.IsNullOrWhiteSpace(agent.ParentId)) owners.TryAdd(agent.ParentId, path);
            foreach (var (path, source) in _watches.Concat(_retiredWatches))
            {
                var transfers = source.AgentIds.Concat(source.Progress.ExportCompleted().Select(record => record.Id)).Distinct()
                    .Where(id => owners.TryGetValue(id, out var owner) && !string.Equals(owner, path, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(id => owners[id], StringComparer.OrdinalIgnoreCase);
                foreach (var transfer in transfers)
                {
                    var destination = _watches[transfer.Key];
                    var movedIds = source.Progress.TransferTo(destination.Progress, transfer.ToArray());
                    source.Reminder.TransferTo(destination.Reminder, movedIds);
                    source.AgentIds.ExceptWith(movedIds);
                }
            }
            // New observed work supersedes retained markers even if their former
            // watched folder was removed. Do not resurrect those markers later.
            var activeIds = snapshots.Values.SelectMany(value => value)
                .Where(agent => agent.State is AgentRunState.Running or AgentRunState.Waiting)
                .Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, watch) in _watches)
            {
                var projectAgents = snapshots[path];
                watch.AgentIds = projectAgents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
                var update = watch.Reminder.Observe(projectAgents, now);
                watch.Progress.Observe(projectAgents, now);
                activeIds.UnionWith(watch.Progress.Rows.Where(row => !row.IsCompleted).Select(row => row.Id));
                updates.Add(update);
                watch.Status = update.IsReminding && !_preferences.ReminderSoundEnabled ? "Work finished · reminder sound off" : update.Status;
                if (projectAgents.Count == 0 && watch.Progress.Rows.Count == 0)
                    watch.Status = "No Codex tasks match this folder.";
                watch.ParentWorkspacePath = snapshot.ParentWorkspaces.GetValueOrDefault(path);
                foreach (var agent in projectAgents)
                {
                    var estimate = watch.Progress.GetAgentEstimate(agent.Id);
                    agentRows.Add(new MonitoredAgentRow(agent.Title, agent.State, path, ProjectName(path),
                        agent.ProjectPath, estimate.Text, estimate.Detail));
                }
            }
            foreach (var retired in _retiredWatches.Values)
                foreach (var record in retired.Progress.ExportCompleted().Where(record => activeIds.Contains(record.Id)))
                    retired.Progress.ClearCompleted(record.Id);
            _preferences.CompletedChats.RemoveAll(record => record is null || activeIds.Contains(record.Id));
            var agents = snapshots.Values.SelectMany(value => value).ToArray();
            UpdateProgressDisplay();
            UpdateWatchedProjects();
            SaveCompletedIfChanged();
            AgentList.ItemsSource = agentRows.OrderBy(a => a.State == AgentRunState.Running ? 0 : a.State == AgentRunState.Waiting ? 1 : a.State == AgentRunState.Unknown ? 2 : 3)
                .ThenBy(a => a.ProjectPath, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Title).ToArray();
            var ready = updates.Count(update => update.IsReminding);
            StatusText.Text = $"Watching {_watches.Count} project(s)" + (ready > 0 ? $" · {ready} ready" : "") +
                (_preferences.ReminderSoundEnabled ? " · reminder sound on" : " · reminder sound off");
            DetailText.Text = $"{updates.Sum(update => update.RunningCount)} running · {updates.Sum(update => update.WaitingCount)} queued / waiting · {agents.Length} local tasks and agents. Each project completes independently.";
            CheckedText.Text = $"Checked {DateTime.Now:HH:mm:ss} · polls every 5 seconds" +
                (_preferences.ReminderSoundEnabled ? " · sound repeats every 60 seconds" : " · progress stays live while sound is off") +
                (_settingsWarning ? " · preferences could not be saved" : "");
            SetTrayText($"Codex alerts — {_watches.Count} projects · {ready} ready" + (_preferences.ReminderSoundEnabled ? "" : " · sound off"));
            if (_preferences.ReminderSoundEnabled && updates.Any(update => update.ShouldPlaySound))
            {
                PlaySound();
                _tray.ShowBalloonTip(5000, "Codex work finished", "Watched project work has finished. Open the monitor for project details or dismiss the reminder from this tray icon.", Forms.ToolTipIcon.Info);
            }
            WriteDiagnostics(true, agents, StatusText.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_exiting || generation != _generation) return;
            foreach (var watch in _watches.Values.Concat(_retiredWatches.Values))
            {
                watch.Reminder.ConnectionLost();
                watch.Progress.ConnectionLost();
                watch.Status = "Status unavailable — completion unconfirmed";
                watch.ParentWorkspacePath = null;
            }
            _sound.Stop();
            UpdateProgressDisplay();
            UpdateWatchedProjects();
            AgentList.ItemsSource = null;
            StatusText.Text = "Local Codex status is unavailable. Alerts are suspended.";
            DetailText.Text = "Keep Codex open. If you just updated it, its local status format may have changed. No completion sound will play while status is uncertain.";
            CheckedText.Text = $"Last attempt {DateTime.Now:HH:mm:ss} · automatically retries every 5 seconds";
            SetTrayText("Codex alerts — status unavailable");
            WriteDiagnostics(false, [], StatusText.Text);
        }
    }

    private async void Watch_Click(object sender, RoutedEventArgs e) => await WatchProjectAsync(ProjectPicker.Text.Trim());

    private async void WatchWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) await WatchProjectAsync(path);
    }

    private async Task WatchProjectAsync(string text)
    {
        if (!Path.IsPathFullyQualified(text) || !Directory.Exists(text))
        {
            StatusText.Text = "Choose an existing project folder using its full path.";
            return;
        }
        _generation++;
        _project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        RememberProjectPaths([_project]);
        ProjectPicker.ItemsSource = _preferences.ProjectPaths.ToArray();
        ProjectPicker.Text = _project;
        AddWatch(_project);
        UpdateWatchedProjects();
        UpdateProgressDisplay();
        SaveProject();
        AgentList.ItemsSource = null;
        StatusText.Text = "Project added. Watching all listed projects…";
        await RefreshAsync();
    }

    private void Dismiss()
    {
        foreach (var watch in _watches.Values) watch.Reminder.Dismiss();
        _sound.Stop();
        StatusText.Text = "Reminder dismissed. Watching for new work.";
        SetTrayText("Codex alerts — reminder dismissed");
    }

    private void SetReminderSound(bool enabled)
    {
        _preferences.ReminderSoundEnabled = enabled;
        _sound.Stop();
        UpdateSoundControls();
        SaveProject();
        StatusText.Text = enabled ? "Reminder sound on. Progress tracking continues." : "Reminder sound off. All projects and the overlay continue updating.";
        if (_settingsWarning) StatusText.Text += " This preference could not be saved.";
        SetTrayText(enabled ? "Codex alerts — sound on" : "Codex alerts — sound off · watching");
    }

    private void UpdateSoundControls()
    {
        ReminderSoundCheckBox.IsChecked = _preferences.ReminderSoundEnabled;
        _soundMenu.Checked = _preferences.ReminderSoundEnabled;
    }

    private void AddWatch(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!_watches.ContainsKey(path))
            _watches.Add(path, _retiredWatches.Remove(path, out var retired) ? retired : new ProjectWatch(path, _preferences.CompletedChats));
    }

    private async void RemoveWatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !_watches.ContainsKey(path)) return;
        // Retain historical completion markers when a folder is removed from the watch list.
        _preferences.CompletedChats = ExportCompleted().ToList();
        _generation++;
        _retiredWatches[path] = _watches[path];
        _watches.Remove(path);
        _sound.Stop();
        UpdateWatchedProjects();
        UpdateProgressDisplay();
        AgentList.ItemsSource = null;
        SaveProject();
        await RefreshAsync();
    }

    private void UpdateWatchedProjects()
    {
        WatchedProjectList.ItemsSource = _watches.Select(pair => new
        {
            ProjectPath = pair.Key,
            ProjectName = ProjectName(pair.Key),
            pair.Value.Status,
            pair.Value.ParentWorkspacePath,
            HasParentWorkspace = pair.Value.ParentWorkspacePath is not null,
            ParentWorkspaceHint = pair.Value.ParentWorkspacePath is { } parent
                ? $"Codex also uses {ProjectName(parent)} as a workspace. Work started there is outside this folder's watch."
                : ""
        }).ToArray();
        WatchedProjectsEmpty.Visibility = _watches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<CompletedChatRecord> ExportCompleted() => _preferences.CompletedChats
        .Where(record => record is not null && !IsWatched(record.ProjectPath))
        .Concat(_watches.Values.Concat(_retiredWatches.Values).SelectMany(watch => watch.Progress.ExportCompleted()))
        .OrderBy(record => record.ProjectPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();

    private bool IsWatched(string path)
    {
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return _watches.ContainsKey(path) || _retiredWatches.ContainsKey(path);
        }
        catch { return false; }
    }

    private static string ProjectName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private sealed class ProjectWatch
    {
        public string Path { get; }
        public ReminderState Reminder { get; } = new();
        public ChatProgressState Progress { get; private set; } = new();
        public HashSet<string> AgentIds { get; set; } = new(StringComparer.Ordinal);
        public string Status { get; set; } = "Waiting for local status…";
        public string? ParentWorkspacePath { get; set; }

        public ProjectWatch(string path, IEnumerable<CompletedChatRecord> completed)
        {
            Path = path;
            Progress.ImportCompleted(completed.Where(record => record is not null && SameOrChild(record.ProjectPath, path) && SameOrChild(path, record.ProjectPath)));
            Progress.SetProject(path);
        }

    }

    private sealed record MonitoredAgentRow(string Title, AgentRunState State, string ProjectPath,
        string ProjectName, string WorkingFolder, string EstimateText, string EstimateDetail);

    private void PlaySound()
    {
        try { _sound.Play(); }
        catch { StatusText.Text = "The sound could not play. Check your Windows audio output."; }
    }

    private void ShowMonitor()
    {
        if (_exiting) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SetTrayText(string text) => _tray.Text = text.Length <= 63 ? text : text[..60] + "…";
    internal static bool TrySignalCommand(string command)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(CommandEventName(command));
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string CommandEventName(string command) => InstanceName + (command == "show" ? ".Show" : "." + command);

    private void HandleCommand(string command)
    {
        if (_exiting) return;
        switch (command)
        {
            case "show-overlay": SetOverlayVisible(true); break;
            case "hide-overlay": SetOverlayVisible(false); break;
            case "clear-completed": ClearCompleted(null); break;
            case "exit-monitor": ExitMonitor(); break;
            case "resume": break; // Reopen in the tray; initialization restores saved overlay visibility.
            default: ShowMonitor(); break;
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        _preferences.OverlayVisible = visible;
        if (visible) _overlay.Show(); else _overlay.Hide();
        OverlayToggleButton.Content = _overlayMenu.Text = visible ? "Hide chat progress" : "Show chat progress";
        SaveProject();
    }

    private void ClearCompleted(string? id)
    {
        foreach (var watch in _watches.Values) watch.Progress.ClearCompleted(id);
        UpdateProgressDisplay();
        SaveProject();
        if (!_settingsWarning) _savedCompletedJson = JsonSerializer.Serialize(ExportCompleted());
    }

    private void UpdateProgressDisplay()
    {
        _progressRows = _watches.Values.SelectMany(watch => watch.Progress.Rows)
            .OrderBy(row => row.State is AgentRunState.Running or AgentRunState.Waiting ? 0 : row.IsCompleted ? 2 : 1)
            .DistinctBy(row => row.Id).OrderBy(row => row.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.State is AgentRunState.Running or AgentRunState.Waiting ? 0 : row.IsCompleted ? 2 : 1)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        _overlay.UpdateRows(_progressRows);
        ProgressChatList.ItemsSource = _progressRows;
        ProgressEmpty.Visibility = _progressRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveCompletedIfChanged()
    {
        var json = JsonSerializer.Serialize(ExportCompleted());
        if (json == _savedCompletedJson) return;
        SaveProject();
        if (!_settingsWarning) _savedCompletedJson = json;
    }

    private void RestoreOverlayBounds()
    {
        var area = SystemParameters.WorkArea;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        _overlay.Width = double.IsFinite(_preferences.OverlayWidth) ? Math.Clamp(_preferences.OverlayWidth, _overlay.MinWidth, Math.Max(_overlay.MinWidth, Math.Min(800, virtualWidth))) : 420;
        _overlay.Height = double.IsFinite(_preferences.OverlayHeight) ? Math.Clamp(_preferences.OverlayHeight, _overlay.MinHeight, Math.Max(_overlay.MinHeight, Math.Min(900, virtualHeight))) : 440;
        var left = _preferences.OverlayLeft is double x && double.IsFinite(x) ? x : area.Right - _overlay.Width - 20;
        var top = _preferences.OverlayTop is double y && double.IsFinite(y) ? y : area.Top + 70;
        _overlay.Left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualLeft + virtualWidth - _overlay.Width));
        _overlay.Top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualTop + virtualHeight - _overlay.Height));
        // The virtual desktop rectangle can contain gaps between staggered monitors.
        // Keep the first draggable chat row on a real display when restoring old bounds.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_overlay);
        var rowAnchor = new Point(_overlay.Left + 40, _overlay.Top + 50);
        var onDisplay = Forms.Screen.AllScreens.Any(screen =>
        {
            var bounds = screen.WorkingArea;
            return new Rect(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY,
                bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY).Contains(rowAnchor);
        });
        if (!onDisplay)
        {
            _overlay.Left = Math.Max(area.Left, area.Right - _overlay.Width - 20);
            _overlay.Top = area.Top + 20;
        }
    }

    private void RememberOverlayBounds()
    {
        if (!_overlay.IsLoaded || _overlay.WindowState != WindowState.Normal) return;
        _preferences.OverlayLeft = _overlay.Left;
        _preferences.OverlayTop = _overlay.Top;
        _preferences.OverlayWidth = _overlay.Width;
        _preferences.OverlayHeight = _overlay.Height;
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e) => SetOverlayVisible(!_preferences.OverlayVisible);
    private void ClearFinished_Click(object sender, RoutedEventArgs e) => ClearCompleted(null);
    private void ClearFinishedChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ClearCompleted(id);
    }
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void ReminderSound_Click(object sender, RoutedEventArgs e) => SetReminderSound(ReminderSoundCheckBox.IsChecked == true);
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitMonitor();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
    }

    private void ExitMonitor()
    {
        if (_exiting) return;
        RememberOverlayBounds();
        SaveProject();
        if (!_preferencesUnreadable && _settingsWarning)
        {
            StatusText.Text = "The watcher could not save its settings. It is still running; resolve the settings file access problem and retry exiting.";
            ShowMonitor();
            return;
        }
        _exiting = true;
        _timer.Stop();
        _preferencesTimer.Stop();
        _overlay.CloseForExit();
        Close();
        Application.Current.Shutdown();
    }

    private void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        _timer.Stop();
        _preferencesTimer.Stop();
        foreach (var listener in _commandListeners)
        {
            listener.Registration.Unregister(null);
            listener.Signal.Dispose();
        }
        _showRegistration.Unregister(null);
        _showEvent.Dispose();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _sound.Stop();
        _sound.Dispose();
        _soundStream.Dispose();
        _instance.ReleaseMutex();
        _instance.Dispose();
    }

    private MonitorPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new();
            var preferences = JsonSerializer.Deserialize<MonitorPreferences>(File.ReadAllText(_settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException();
            preferences.Normalize();
            var completed = new ChatProgressState();
            completed.ImportCompleted(preferences.CompletedChats);
            preferences.CompletedChats = completed.ExportCompleted().ToList();
            return preferences;
        }
        catch { _preferencesUnreadable = true; _settingsWarning = true; return new(); }
    }

    private void SaveProject()
    {
        if (_preferencesUnreadable) return; // Preserve malformed preferences for recovery.
        try
        {
            _preferences.ProjectPath = _project ?? _preferences.ProjectPath;
            _preferences.WatchedProjectPaths = _watches.Keys.ToList();
            RememberProjectPaths(_watches.Keys.Append(_preferences.ProjectPath));
            _preferences.CompletedChats = ExportCompleted().ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(_preferences, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            File.Move(_settingsPath + ".tmp", _settingsPath, true);
            _settingsWarning = false;
        }
        catch { _settingsWarning = true; }
    }

    private void RememberProjectPaths(IEnumerable<string?> paths)
    {
        // Retain folder choices when Codex is closed or its local metadata is unavailable.
        // Only paths are saved; task titles and messages never enter preferences.
        var remembered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _preferences.ProjectPaths.Concat(paths))
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            try { remembered.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        _preferences.ProjectPaths = remembered.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void WriteDiagnostics(bool connected, IReadOnlyList<AgentSnapshot> agents, string status)
    {
        if (_diagnosticsFile is null) return;
        // Opt-in troubleshooting output never includes titles, messages, credentials or task IDs.
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                checkedAt = DateTimeOffset.UtcNow, connected, watchedProjects = _watches.Count, reminderSoundEnabled = _preferences.ReminderSoundEnabled, status,
                counts = agents.GroupBy(a => a.State.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                projects = _watches.Values.Select((watch, index) => new
                {
                    watchIndex = index + 1, status = watch.Status,
                    hasParentWorkspace = watch.ParentWorkspacePath is not null,
                    counts = agents.Where(agent => watch.AgentIds.Contains(agent.Id)).GroupBy(agent => agent.State.ToString())
                        .ToDictionary(group => group.Key, group => group.Count()),
                    chats = watch.Progress.Rows.Count, finished = watch.Progress.Rows.Count(row => row.IsCompleted)
                }).ToArray(),
                overlay = new { visible = _overlay.IsVisible, topmost = _overlay.Topmost, transparent = _overlay.AllowsTransparency, chats = _progressRows.Count, finished = _progressRows.Count(r => r.IsCompleted) }
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_diagnosticsFile + ".tmp", json);
            File.Move(_diagnosticsFile + ".tmp", _diagnosticsFile, true);
        }
        catch { CheckedText.Text += " · diagnostic file could not be written"; }
    }

    private static string? Option(string[] args, string key)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool SameOrChild(string path, string root)
    {
        try
        {
            path = Path.GetFullPath(path).TrimEnd('\\', '/');
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static byte[] CreateChime()
    {
        const int rate = 22050;
        const int samples = rate / 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            var segment = t < .25 ? t : t - .25;
            var envelope = Math.Min(segment / .015, 1) * Math.Max(0, 1 - segment / .25);
            writer.Write((short)(Math.Sin(2 * Math.PI * (t < .25 ? 660 : 880) * segment) * envelope * 7000));
        }
        return stream.ToArray();
    }
}
