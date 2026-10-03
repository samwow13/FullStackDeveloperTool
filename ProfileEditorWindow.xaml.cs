using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class ProfileEditorWindow : Window
{
    private enum ApiTargetMode { Legacy, Manual }

    private readonly string _settingsDirectory;
    private readonly IReadOnlyList<ProjectProfile> _savedProjects;
    private readonly ObservableCollection<ServiceProfile> _services;
    private bool _updatingApiTargetOptions;
    public ProjectProfile Result { get; private set; }

    public ProfileEditorWindow(ProjectProfile profile, string settingsDirectory,
        IReadOnlyList<ProjectProfile>? savedProjects = null)
    {
        InitializeComponent();
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _savedProjects = savedProjects ?? [];
        Result = Clone(profile);
        _services = new ObservableCollection<ServiceProfile>(Result.Services);
        ProjectNameBox.Text = Result.Name;
        RootPathBox.Text = Result.RootPath;
        SettingsLocationText.Text = $"Relative paths start at: {_settingsDirectory}";
        ServicesList.ItemsSource = _services;
        ServicesList.SelectedItem = _services.FirstOrDefault();
        UpdateSelection();
    }

    private void ServicesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (ServiceForm == null) return;
        var service = ServicesList.SelectedItem as ServiceProfile;
        ServiceForm.DataContext = service;
        ServiceForm.Visibility = service == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyServiceHint.Visibility = service == null ? Visibility.Visible : Visibility.Collapsed;
        RemoveServiceButton.IsEnabled = service != null;
        UpdateServiceKindFields();
    }

    private void ServiceKind_SourceUpdated(object sender, DataTransferEventArgs e) => UpdateServiceKindFields();

    private void UpdateServiceKindFields()
    {
        if (WebSettingsPanel == null || ConsoleCommandHelp == null || OptionalCommandsExpander == null || ApiTargetPanel == null) return;
        var isConsole = ServiceForm.DataContext is ServiceProfile { IsConsole: true };
        WebSettingsPanel.Visibility = isConsole ? Visibility.Collapsed : Visibility.Visible;
        ConsoleCommandHelp.Visibility = isConsole ? Visibility.Visible : Visibility.Collapsed;
        OptionalCommandsExpander.IsExpanded = !isConsole;
        UpdateApiTargetOptions();
    }

    private void UpdateApiTargetOptions()
    {
        if (_services is null || ApiTargetBox == null || ApiTargetPanel == null) return;
        var frontend = ServiceForm.DataContext as ServiceProfile;
        ApiTargetPanel.Visibility = frontend is not null && frontend.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        _updatingApiTargetOptions = true;
        try
        {
            ApiTargetBox.Items.Clear();
            var legacy = new ComboBoxItem { Content = "Automatic port matching (legacy)", Tag = ApiTargetMode.Legacy };
            var manual = new ComboBoxItem { Content = "None (manage proxy manually)", Tag = ApiTargetMode.Manual };
            ApiTargetBox.Items.Add(legacy);
            ApiTargetBox.Items.Add(manual);
            ComboBoxItem? selected = null;
            if (frontend is not null)
            {
                foreach (var api in _services.Where(service => !ReferenceEquals(service, frontend) && SettingsStore.IsLinkableApiService(service)))
                {
                    var option = new ComboBoxItem { Content = api.Name, Tag = api.Id };
                    ApiTargetBox.Items.Add(option);
                    if (api.Id.Equals(frontend.ApiTargetServiceId, StringComparison.OrdinalIgnoreCase)) selected = option;
                }
                if (selected is null && !string.IsNullOrWhiteSpace(frontend.ApiTargetServiceId))
                {
                    selected = new ComboBoxItem { Content = "Unavailable API service (choose another)", Tag = frontend.ApiTargetServiceId };
                    ApiTargetBox.Items.Add(selected);
                }
            }
            ApiTargetBox.SelectedItem = selected ?? (frontend?.DisableLegacyApiPortSync == true ? manual : legacy);
        }
        finally { _updatingApiTargetOptions = false; }
    }

    private void ApiTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingApiTargetOptions || ServiceForm.DataContext is not ServiceProfile frontend ||
            ApiTargetBox.SelectedItem is not ComboBoxItem option) return;
        if (option.Tag is string targetId)
        {
            frontend.ApiTargetServiceId = targetId;
            frontend.DisableLegacyApiPortSync = true;
        }
        else
        {
            frontend.ApiTargetServiceId = null;
            frontend.DisableLegacyApiPortSync = option.Tag is ApiTargetMode.Manual;
        }
    }

    private void ServiceName_LostFocus(object sender, RoutedEventArgs e) => ServicesList.Items.Refresh();

    private void AddService_Click(object sender, RoutedEventArgs e)
    {
        var service = new ServiceProfile { Name = "New service", Kind = "Custom", WorkingDirectory = ".", Url = "http://localhost:5000", UiPath = "/" };
        _services.Add(service);
        ServicesList.SelectedItem = service;
        ServicesList.ScrollIntoView(service);
    }

    private void AddConsoleApp_Click(object sender, RoutedEventArgs e)
    {
        var service = new ServiceProfile
        {
            Name = "Console app", Kind = "Console", WorkingDirectory = ".",
            StartCommand = "dotnet run", Url = "", UiPath = ""
        };
        _services.Add(service);
        ServicesList.SelectedItem = service;
        ServicesList.ScrollIntoView(service);
    }

    private void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (ServicesList.SelectedItem is not ServiceProfile service) return;
        var index = ServicesList.SelectedIndex;
        _services.Remove(service);
        if (_services.Count > 0) ServicesList.SelectedIndex = Math.Min(index, _services.Count - 1);
        UpdateSelection();
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the project root folder", Multiselect = false };
        var initial = TryResolveRoot();
        if (initial != null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) == true)
            RootPathBox.Text = Path.GetRelativePath(_settingsDirectory, dialog.FolderName);
    }

    private void BrowseService_Click(object sender, RoutedEventArgs e)
    {
        if (ServicesList.SelectedItem is not ServiceProfile service) return;
        var root = TryResolveRoot();
        if (root == null || !Directory.Exists(root))
        {
            ShowValidation("Choose an existing project root folder before browsing service folders.");
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose the service working folder", Multiselect = false, InitialDirectory = root };
        try
        {
            var initial = Path.GetFullPath(service.WorkingDirectory, root);
            if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (dialog.ShowDialog(this) != true) return;
        service.WorkingDirectory = Path.GetRelativePath(root, dialog.FolderName);
        // Models are plain configuration records, so refresh this editor's binding after a browse.
        ServiceForm.DataContext = null;
        ServiceForm.DataContext = service;
    }

    private string? TryResolveRoot()
    {
        try { return Path.GetFullPath(RootPathBox.Text.Trim(), _settingsDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (ServicesList.SelectedItem is not ServiceProfile { IsConsole: true } service) return;
        StartCommandBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!TryGetExecutableArguments(service.StartCommand, out var arguments))
        {
            ShowValidation("Use executable changes a direct executable command and keeps its arguments. " +
                "Clear the start command first to replace a different command. " +
                "For a .NET app that launches another executable, enter that target path as an argument after dotnet run --.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose the executable to run directly", Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false, DefaultExt = ".exe"
        };
        var root = TryResolveRoot();
        if (root != null && Directory.Exists(root)) dialog.InitialDirectory = root;
        if (dialog.ShowDialog(this) != true) return;
        if (!Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowValidation("Choose an .exe application file.");
            return;
        }
        // CMD expands these characters even inside quoted paths. Never generate
        // a command that could resolve to a different executable after expansion.
        if (dialog.FileName.IndexOfAny(['%', '!', '"', '\r', '\n']) >= 0)
        {
            ShowValidation("This executable path contains characters that Windows command processing may expand. " +
                "Choose an executable from a folder without %, ! or quote characters.");
            return;
        }
        service.StartCommand = $"\"{dialog.FileName}\"{arguments}";
        StartCommandBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        StartCommandBox.Focus();
        StartCommandBox.CaretIndex = StartCommandBox.Text.Length;
    }

    private static bool TryGetExecutableArguments(string command, out string arguments)
    {
        arguments = "";
        command = command.TrimStart();
        if (command.Length == 0 || command.TrimEnd().Equals("dotnet run", StringComparison.OrdinalIgnoreCase)) return true;
        // Only replace one literal executable token. Leave compound commands and
        // dotnet invocations intact rather than guessing which arguments to keep.
        if (command.IndexOfAny(['&', '|', '<', '>', '^', '\r', '\n']) >= 0) return false;
        string executable;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0 || (end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))) return false;
            executable = command[1..end];
            arguments = command[(end + 1)..];
        }
        else
        {
            var end = command.IndexOfAny([' ', '\t']);
            if (end < 0) end = command.Length;
            executable = command[..end];
            arguments = command[end..];
        }
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !executable.Contains('"');
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit the editable type even when Enter invokes the default Save button.
        ServiceKindBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        var candidate = new ProjectProfile
        {
            Id = Result.Id,
            Name = ProjectNameBox.Text.Trim(),
            RootPath = RootPathBox.Text.Trim(),
            IsArchived = Result.IsArchived,
            AutoRestartAfterAgentsEnabled = Result.AutoRestartAfterAgentsEnabled,
            AutoRestartWatchPath = Result.AutoRestartWatchPath,
            Database = Result.Database,
            Services = _services.Select(CloneService).ToList()
        };
        foreach (var service in candidate.Services)
        {
            service.Name = service.Name.Trim();
            service.Kind = service.Kind.Trim();
            service.WorkingDirectory = service.WorkingDirectory.Trim();
            service.StartCommand = service.StartCommand.Trim();
            service.CleanCommand = service.CleanCommand.Trim();
            service.SetupCommand = service.SetupCommand.Trim();
            service.Url = service.Url.Trim().TrimEnd('/');
            service.UiPath = service.UiPath.Trim();
            service.ApiTargetServiceId = service.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase)
                ? service.ApiTargetServiceId?.Trim() : null;
            if (!service.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase))
                service.DisableLegacyApiPortSync = false;
            if (service.IsConsole) service.ApiConfiguration = null;
        }
        try
        {
            SettingsStore.Validate(new LauncherSettings { Projects = [candidate] }, _settingsDirectory);
            if (candidate.Services.Count == 0) throw new ArgumentException("Add at least one service to this project.");
            var root = Path.GetFullPath(candidate.RootPath, _settingsDirectory);
            if (!Directory.Exists(root)) throw new ArgumentException($"The project root folder does not exist:\n{root}");
            foreach (var service in candidate.Services)
            {
                var folder = Path.GetFullPath(service.WorkingDirectory, root);
                if (!Directory.Exists(folder))
                    throw new ArgumentException($"The working folder for '{service.Name}' does not exist:\n{folder}");
                var conflicts = SettingsStore.FindServiceFolderConflicts(
                    _savedProjects, _settingsDirectory, folder, candidate.Id);
                if (conflicts.Count > 0)
                {
                    var conflict = conflicts[0];
                    throw new ArgumentException($"The working folder for '{service.Name}' overlaps " +
                        $"saved '{conflict.Project.Name}' / '{conflict.Service.Name}'. " +
                        "Choose a different folder or open that saved project.");
                }
            }
            Result = candidate;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowValidation(ex.Message);
        }
    }

    private void ShowValidation(string message) => MessageBox.Show(this, message, "Check project settings", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static ProjectProfile Clone(ProjectProfile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, RootPath = profile.RootPath, IsArchived = profile.IsArchived,
        AutoRestartAfterAgentsEnabled = profile.AutoRestartAfterAgentsEnabled,
        AutoRestartWatchPath = profile.AutoRestartWatchPath,
        Database = profile.Database is { } database
            ? new() { SourceId = database.SourceId, DatabaseName = database.DatabaseName } : null,
        Services = profile.Services.Select(CloneService).ToList()
    };

    private static ServiceProfile CloneService(ServiceProfile service) => new()
    {
        Id = service.Id, Name = service.Name, Kind = service.Kind,
        WorkingDirectory = service.WorkingDirectory, StartCommand = service.StartCommand,
        CleanCommand = service.CleanCommand, SetupCommand = service.SetupCommand,
        Url = service.Url, UiPath = service.UiPath, ApiTargetServiceId = service.ApiTargetServiceId,
        OpenAfterBuild = service.OpenAfterBuild,
        DisableLegacyApiPortSync = service.DisableLegacyApiPortSync,
        ProductionDatabase = service.ProductionDatabase is { } productionDatabase
            ? new() { SourceId = productionDatabase.SourceId, DatabaseName = productionDatabase.DatabaseName } : null,
        ComparisonLocalSourceId = service.ComparisonLocalSourceId,
        ApiConfiguration = service.ApiConfiguration is { } configuration
            ? new() { Environment = configuration.Environment } : null
    };
}
