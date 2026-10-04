using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class ConsoleServiceWindow : Window
{
    private readonly ProjectProfile _project;
    private readonly string _settingsDirectory;
    private readonly IReadOnlyList<ProjectProfile> _savedProjects;
    private readonly Action<ServiceProfile>? _save;
    private readonly string _serviceId = Guid.NewGuid().ToString("N");
    private string _workingDirectory = "";
    private string _suggestedName = "";
    private string _suggestedType = "";
    private string _suggestedCommand = "";
    private ServiceCommandDetection? _detection;
    private CancellationTokenSource? _folderDetectionCancellation;
    private bool _applyingSuggestion;
    private bool _settingDetection;
    private bool _detecting;
    private bool _closed;
    private bool _saving;

    public ServiceProfile? Result { get; private set; }

    public ConsoleServiceWindow(ProjectProfile project, string settingsDirectory,
        IReadOnlyList<ProjectProfile>? savedProjects = null, Action<ServiceProfile>? save = null,
        ServiceCommandDetection? detection = null, string? workingDirectory = null)
    {
        InitializeComponent();
        _project = project;
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _savedProjects = savedProjects ?? [];
        _save = save;
        ProjectNameText.Text = project.Name;
        SetFolderDetection(workingDirectory ?? Path.GetFullPath(project.RootPath, _settingsDirectory), detection);
        ConsoleTypeBox.SelectionChanged += ConsoleType_SelectionChanged;
        Closed += (_, _) => { _closed = true; _folderDetectionCancellation?.Cancel(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void SetFolderDetection(string folder, ServiceCommandDetection? detection, bool preserveEdits = false)
    {
        _workingDirectory = Path.GetFullPath(folder);
        _detection = detection;
        FolderBox.Text = _workingDirectory;
        FolderBox.ToolTip = _workingDirectory;
        var suggestions = detection?.Suggestions ?? [];
        var manual = ServiceCommandDiscovery.CreateTemplate("Console", false, _workingDirectory);
        SuggestionPanel.Visibility = suggestions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _settingDetection = true;
        try
        {
            SuggestionBox.ItemsSource = suggestions.Count > 1 ? suggestions.Append(manual).ToArray() : suggestions;
            SuggestionBox.SelectedIndex = suggestions.Count == 1 ? 0 : -1;
        }
        finally { _settingDetection = false; }
        ApplySuggestion(suggestions.Count == 1 ? suggestions[0] : manual, preserveEdits);
        if (suggestions.Count > 1)
        {
            DetectionStatusText.Text = $"Found {suggestions.Count} launch options. Choose one or enter a command.";
            DetectionStatusText.ToolTip = string.Join(Environment.NewLine,
                suggestions.Select(suggestion => suggestion.Evidence).Concat(detection?.Notes ?? []));
        }
    }

    private void Suggestion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingDetection && SuggestionBox.SelectedItem is ServiceCommandSuggestion suggestion)
            ApplySuggestion(suggestion, preserveEdits: false);
    }

    private void ApplySuggestion(ServiceCommandSuggestion suggestion, bool preserveEdits)
    {
        _applyingSuggestion = true;
        try
        {
            if (!preserveEdits || NameBox.Text == _suggestedName) NameBox.Text = suggestion.Name;
            if (!preserveEdits || ConsoleTypeBox.Text == _suggestedType) ConsoleTypeBox.Text = suggestion.Type;
            if (!preserveEdits || CommandBox.Text.Trim() == _suggestedCommand.Trim()) CommandBox.Text = suggestion.Command;
            _suggestedName = suggestion.Name;
            _suggestedType = suggestion.Type;
            _suggestedCommand = suggestion.Command;
            DetectionStatusText.Text = suggestion.IsDetected ? $"Detected: {suggestion.Type}"
                : "Type not detected. Choose a type or enter a command.";
            DetectionStatusText.ToolTip = string.Join(Environment.NewLine,
                new[] { suggestion.Evidence }.Concat(_detection?.Notes ?? []));
        }
        finally { _applyingSuggestion = false; }
    }

    private void ConsoleType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSuggestion || e.AddedItems.Count == 0 || e.AddedItems[0] is not ComboBoxItem { Content: string type }) return;
        var template = ServiceCommandDiscovery.CreateTemplate(type, false, _workingDirectory);
        // A type preset only replaces a command that still matches its previous suggestion.
        if (CommandBox.Text.Trim() == _suggestedCommand.Trim()) CommandBox.Text = template.Command;
        _suggestedType = type;
        _suggestedCommand = template.Command;
        SuggestionBox.SelectedIndex = -1;
        DetectionStatusText.Text = $"Command suggestion: {type}";
        DetectionStatusText.ToolTip = template.Evidence;
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _detecting) return;
        CancellationTokenSource? scan = null;
        var previousStatus = DetectionStatusText.Text;
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose the console working folder", Multiselect = false,
                InitialDirectory = Directory.Exists(_workingDirectory) ? _workingDirectory : ResolveRoot()
            };
            if (dialog.ShowDialog(this) != true) return;
            scan = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _folderDetectionCancellation = scan;
            _detecting = true;
            SaveButton.IsEnabled = ChangeFolderButton.IsEnabled = false;
            DetectionStatusText.Text = "Inspecting folder…";
            var detection = await ServiceCommandDiscovery.DetectAsync(dialog.FolderName, false, scan.Token).WaitAsync(scan.Token);
            scan.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (!detection.IsComplete)
            {
                DetectionStatusText.Text = previousStatus;
                ShowValidation("Folder inspection could not finish. Previous folder and command are kept.\n" +
                    string.Join(Environment.NewLine, detection.Notes));
                return;
            }
            SetFolderDetection(dialog.FolderName, detection, preserveEdits: true);
            ValidationText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                DetectionStatusText.Text = previousStatus;
                ShowValidation("Folder inspection timed out. Previous folder and command are kept.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            if (_closed) return;
            DetectionStatusText.Text = previousStatus;
            ShowValidation(ex.Message);
        }
        finally
        {
            _folderDetectionCancellation = null;
            scan?.Dispose();
            _detecting = false;
            if (!_closed) SaveButton.IsEnabled = ChangeFolderButton.IsEnabled = true;
        }
    }

    private string ResolveRoot()
    {
        var root = Path.GetFullPath(_project.RootPath, _settingsDirectory);
        if (!Directory.Exists(root))
            throw new ArgumentException($"The project root folder does not exist:\n{root}");
        return root;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _detecting) return;
        ValidationText.Visibility = Visibility.Collapsed;
        try
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0)
            {
                NameBox.Focus();
                throw new ArgumentException("Enter a console app name.");
            }
            var command = CommandBox.Text.Trim();
            if (command.Length == 0)
            {
                CommandBox.Focus();
                throw new ArgumentException("Enter a command, including any arguments.");
            }
            var root = ResolveRoot();
            var folder = _workingDirectory;
            if (!Directory.Exists(folder))
            {
                FolderBox.Focus();
                throw new ArgumentException($"The working folder does not exist:\n{folder}");
            }
            var type = ConsoleTypeBox.Text.Trim();
            var service = new ServiceProfile
            {
                Id = _serviceId, Name = name, Kind = "Console",
                ConsoleType = type.Length == 0 || type.Equals("Console", StringComparison.OrdinalIgnoreCase) ? null : type,
                WorkingDirectory = Path.GetRelativePath(root, folder), StartCommand = command, Url = "", UiPath = ""
            };
            // Validation may normalize profiles, so only detached copies enter it.
            var profiles = _savedProjects.Where(project =>
                !project.Id.Equals(_project.Id, StringComparison.OrdinalIgnoreCase)).Append(_project).ToList();
            var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(
                new LauncherSettings { Projects = profiles }))!;
            var candidateProject = candidate.Projects.Single(project => project.Id.Equals(_project.Id, StringComparison.OrdinalIgnoreCase));
            candidateProject.Services.Add(service);
            SettingsStore.Validate(candidate, _settingsDirectory);

            // Persistence happens before closing. Failures retain all entered fields.
            _saving = true;
            SaveButton.IsEnabled = false;
            _save?.Invoke(service);
            Result = service;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            ShowValidation(ex.Message);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
