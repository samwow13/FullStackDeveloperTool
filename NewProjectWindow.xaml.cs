using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class NewProjectWindow : Window
{
    private readonly string _settingsDirectory;
    private readonly IReadOnlyList<ProjectProfile> _savedProjects;
    private int _scanVersion;
    private string? _scannedRoot;
    private ProjectProfile? _conflictingProject;
    private bool _hasExistingFolderConflict;

    public ProjectProfile Result { get; private set; } = new();
    public string? ExistingProjectId { get; private set; }

    public NewProjectWindow(string settingsDirectory, IReadOnlyList<ProjectProfile> savedProjects)
    {
        InitializeComponent();
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _savedProjects = savedProjects;
        ApiDirectoryBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ServiceFolder_TextChanged));
        FrontendDirectoryBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ServiceFolder_TextChanged));
        ProjectNameBox.Text = "My full stack project";
        ScanStatusText.Text = "Choose a project root, then scan it for API and Angular folders.";
        UpdateProjectTypePresentation(FullStackType.IsChecked == true);
    }

    private void ProjectType_Changed(object sender, RoutedEventArgs e)
    {
        if (DiscoveryPanel == null || ProjectNameBox == null || RootPathBox == null) return;
        var fullStack = FullStackType.IsChecked == true;
        UpdateProjectTypePresentation(fullStack);
        if (ProjectNameBox.Text is "My full stack project" or "My console project")
            ProjectNameBox.Text = fullStack ? "My full stack project" : "My console project";
        if (!fullStack)
        {
            _scanVersion++;
            _scannedRoot = null;
            SetBusy(false);
        }
        UpdateExistingFolderConflict();
    }

    private void UpdateProjectTypePresentation(bool fullStack)
    {
        DiscoveryPanel.Visibility = fullStack ? Visibility.Visible : Visibility.Collapsed;
        IntroHelpText.Text = fullStack
            ? "Choose a folder. Discovery fills in the API and Angular working folders for review."
            : "Choose a folder for your console app or executable. Next, review its start command and arguments.";
        RootFolderHelpText.Text = fullStack
            ? "Choose the folder that contains your API and frontend. Paths may also be typed."
            : "Choose the working folder for your console app or executable. Paths may also be typed.";
        NextStepText.Text = fullStack
            ? "Next: review service commands and save project."
            : "Next: review command and arguments, then save project.";
    }

    private void RootPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (ApiDirectoryBox == null || FrontendDirectoryBox == null || ScanStatusText == null ||
            ScanNotesList == null || ManualConfigurationWarning == null) return;
        _scanVersion++;
        _scannedRoot = null;
        ApiDirectoryBox.ItemsSource = null;
        ApiDirectoryBox.Text = "";
        FrontendDirectoryBox.ItemsSource = null;
        FrontendDirectoryBox.Text = "";
        ScanNotesList.ItemsSource = null;
        ScanStatusText.Text = "Scan this folder to find the API and Angular working folders.";
        ManualConfigurationWarning.Visibility = Visibility.Collapsed;
        UpdateExistingFolderConflict();
        SetBusy(false);
    }

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var initial = TryResolveRoot(out var resolved, out _) && Directory.Exists(resolved)
            ? resolved : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(initial)) initial = _settingsDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the project root folder", InitialDirectory = initial, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        RootPathBox.Text = dialog.FolderName;
        if (FullStackType.IsChecked == true) await ScanAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (!TryResolveRoot(out var root, out var message))
        {
            ShowValidation(message);
            return;
        }

        // RootPath_Changed clears paths from the previous root. Keep any paths typed for this root.
        var apiSelection = ApiDirectoryBox.Text.Trim();
        var frontendSelection = FrontendDirectoryBox.Text.Trim();
        var version = ++_scanVersion;
        _scannedRoot = root;
        SetBusy(true);
        ScanStatusText.Text = "Scanning project folders…";
        ScanNotesList.ItemsSource = null;
        ManualConfigurationWarning.Visibility = Visibility.Collapsed;

        try
        {
            var found = await Task.Run(() => FullStackProjectDiscovery.Discover(root));
            if (version != _scanVersion) return;

            ApiDirectoryBox.ItemsSource = found.ApiDirectories.ToArray();
            FrontendDirectoryBox.ItemsSource = found.FrontendDirectories.ToArray();
            var apiTie = found.Notes.Any(note => note.StartsWith(
                "Multiple API folders have the same rank.", StringComparison.Ordinal));
            var frontendTie = found.Notes.Any(note => note.StartsWith(
                "Multiple frontend folders have the same rank.", StringComparison.Ordinal));
            ApiDirectoryBox.Text = apiSelection.Length > 0 ? apiSelection : apiTie
                ? "" : found.ApiDirectories.FirstOrDefault() ?? "";
            FrontendDirectoryBox.Text = frontendSelection.Length > 0 ? frontendSelection : frontendTie
                ? "" : found.FrontendDirectories.FirstOrDefault() ?? "";
            ScanNotesList.ItemsSource = found.Notes.ToArray();
            ScanStatusText.Text = $"Found {found.ApiDirectories.Count} API folder(s) and {found.FrontendDirectories.Count} Angular folder(s).";
            var warnings = new List<string>();
            if (found.ApiDirectories.Count == 0)
                warnings.Add("No API folder was found. Browse or type its existing working folder.");
            if (found.FrontendDirectories.Count == 0)
                warnings.Add("No Angular frontend folder was found. Browse or type its existing working folder.");
            if (apiTie && apiSelection.Length == 0)
                warnings.Add("Multiple API folders match equally. Choose the correct one.");
            if (frontendTie && frontendSelection.Length == 0)
                warnings.Add("Multiple Angular frontend folders match equally. Choose the correct one.");
            if (warnings.Count > 0)
            {
                ManualConfigurationWarning.Text = string.Join(" ", warnings);
                ManualConfigurationWarning.Visibility = Visibility.Visible;
            }
            UpdateExistingFolderConflict();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (version != _scanVersion) return;
            ScanStatusText.Text = "Folder scan could not finish.";
            ManualConfigurationWarning.Text = $"Choose both existing service folders manually before continuing. Scan detail: {ex.Message}";
            ManualConfigurationWarning.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == _scanVersion) SetBusy(false);
        }
    }

    private void BrowseApi_Click(object sender, RoutedEventArgs e) => BrowseServiceFolder(ApiDirectoryBox, "Choose the Backend API working folder");

    private void BrowseFrontend_Click(object sender, RoutedEventArgs e) => BrowseServiceFolder(FrontendDirectoryBox, "Choose the Angular frontend working folder");

    private void BrowseServiceFolder(ComboBox choice, string title)
    {
        if (!TryResolveRoot(out var root, out var message))
        {
            ShowValidation(message);
            return;
        }
        var initial = root;
        if (!string.IsNullOrWhiteSpace(choice.Text))
        {
            try
            {
                var selected = Path.GetFullPath(choice.Text.Trim(), root);
                if (Directory.Exists(selected)) initial = selected;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initial, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            choice.Text = dialog.FolderName;
            UpdateExistingFolderConflict();
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        var name = ProjectNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("Enter a project name.");
            ProjectNameBox.Focus();
            return;
        }
        if (!TryResolveRoot(out var root, out var message))
        {
            ShowValidation(message);
            RootPathBox.Focus();
            return;
        }

        if (ConsoleType.IsChecked == true)
        {
            Result = new ProjectProfile
            {
                Name = name, RootPath = Path.GetRelativePath(_settingsDirectory, root),
                Services =
                [
                    new() { Name = "Console app", Kind = "Console", WorkingDirectory = ".",
                        StartCommand = "dotnet run", Url = "", UiPath = "" }
                ]
            };
            DialogResult = true;
            return;
        }

        if (!string.Equals(_scannedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            await ScanAsync();
            if (ConsoleType.IsChecked != false || !TryResolveRoot(out var currentRoot, out _) ||
                !string.Equals(currentRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        }
        if (!TryResolveServiceFolder(ApiDirectoryBox.Text, root, "Backend API", out var apiDirectory) ||
            !TryResolveServiceFolder(FrontendDirectoryBox.Text, root, "Angular frontend", out var frontendDirectory)) return;

        if (Overlaps(apiDirectory, frontendDirectory))
        {
            ShowValidation("The API and Angular working folders must be separate. Neither folder can contain the other.");
            return;
        }
        UpdateExistingFolderConflict();
        if (_hasExistingFolderConflict)
        {
            ShowValidation(ExistingFolderWarning.Text);
            return;
        }

        var apiChoice = ApiDirectoryBox.Text;
        var frontendChoice = FrontendDirectoryBox.Text;
        var currentScanVersion = _scanVersion;
        SetBusy(true);
        (string? Error, string? ApiUrl) inspection;
        try
        {
            inspection = await Task.Run(() => InspectSelectedFolders(apiDirectory, frontendDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or SecurityException)
        {
            inspection = ($"Could not inspect selected service folders: {ex.Message}", null);
        }
        finally
        {
            SetBusy(false);
        }
        if (currentScanVersion != _scanVersion || FullStackType.IsChecked != true ||
            ApiDirectoryBox.Text != apiChoice || FrontendDirectoryBox.Text != frontendChoice ||
            !TryResolveRoot(out var currentProjectRoot, out _) ||
            !string.Equals(root, currentProjectRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (inspection.Error is { } folderError)
        {
            ShowValidation(folderError);
            return;
        }

        Result = new ProjectProfile
        {
            Name = name,
            RootPath = Path.GetRelativePath(_settingsDirectory, root),
            Services =
            [
                new()
                {
                    Name = "Backend API", Kind = ".NET", WorkingDirectory = Path.GetRelativePath(root, apiDirectory),
                    StartCommand = "dotnet run", CleanCommand = "dotnet clean", SetupCommand = "dotnet restore",
                    Url = SettingsStore.IsLocalUrl(inspection.ApiUrl) ? inspection.ApiUrl! : "http://localhost:5000", UiPath = "/swagger"
                },
                new()
                {
                    Name = "Frontend", Kind = "Angular", WorkingDirectory = Path.GetRelativePath(root, frontendDirectory),
                    StartCommand = "npx ng serve --port 4200", CleanCommand = "npx ng cache clean", SetupCommand = "npm install",
                    Url = "http://localhost:4200", UiPath = "/"
                }
            ]
        };
        DialogResult = true;
    }

    private bool TryResolveRoot(out string root, out string message)
    {
        root = "";
        message = "Choose an existing project root folder.";
        if (RootPathBox.Text.Trim().Length == 0) return false;
        try
        {
            root = Path.GetFullPath(RootPathBox.Text.Trim(), _settingsDirectory);
            if (Directory.Exists(root)) return true;
            message = $"The project root folder does not exist:\n{root}";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = $"The project root folder path is invalid: {ex.Message}";
            return false;
        }
    }

    private bool TryResolveServiceFolder(string text, string root, string label, out string directory)
    {
        directory = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowValidation($"Choose an existing {label} working folder. Use Browse if the folder scan missed it.");
            return false;
        }
        try
        {
            directory = Path.GetFullPath(text.Trim(), root);
            var relative = Path.GetRelativePath(root, directory);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                ShowValidation($"The {label} working folder must be inside the project root.");
                return false;
            }
            if (!Directory.Exists(directory))
            {
                ShowValidation($"The {label} working folder does not exist:\n{directory}");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowValidation($"The {label} working folder path is invalid: {ex.Message}");
            return false;
        }
    }

    private static bool Overlaps(string first, string second)
    {
        return SettingsStore.WorkingFoldersOverlap(first, second);
    }

    private void ServiceFolder_TextChanged(object sender, TextChangedEventArgs e) => UpdateExistingFolderConflict();

    private void ServiceFolder_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExistingFolderConflict();

    private void UpdateExistingFolderConflict()
    {
        if (ExistingFolderWarning == null || OpenSavedProjectButton == null ||
            ApiDirectoryBox == null || FrontendDirectoryBox == null) return;
        _conflictingProject = null;
        _hasExistingFolderConflict = false;
        ExistingFolderWarning.Visibility = Visibility.Collapsed;
        OpenSavedProjectButton.Visibility = Visibility.Collapsed;
        if (FullStackType.IsChecked != true || _scannedRoot is not { } root) return;

        var conflicts = new List<(string SelectedService, ProjectProfile Project, ServiceProfile Service)>();
        foreach (var (selectedService, text) in new[]
                 { ("Backend API", ApiDirectoryBox.Text), ("Angular frontend", FrontendDirectoryBox.Text) })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                var folder = Path.GetFullPath(text.Trim(), root);
                foreach (var owner in SettingsStore.FindServiceFolderConflicts(
                             _savedProjects, _settingsDirectory, folder))
                    conflicts.Add((selectedService, owner.Project, owner.Service));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (conflicts.Count == 0) return;

        _hasExistingFolderConflict = true;
        var projectIds = conflicts.Select(conflict => conflict.Project.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _conflictingProject = projectIds.Length == 1
            ? conflicts[0].Project : null;
        ExistingFolderWarning.Text = string.Join(" ", conflicts.Select(conflict =>
            $"{conflict.SelectedService} folder overlaps saved '{conflict.Project.Name}' / '{conflict.Service.Name}'.")) +
            (_conflictingProject is null
                ? " Choose different folders or select the saved projects from the project list."
                : " Choose different folders or view the saved project below.");
        ExistingFolderWarning.Visibility = Visibility.Visible;
        OpenSavedProjectButton.Visibility = _conflictingProject is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenSavedProject_Click(object sender, RoutedEventArgs e)
    {
        UpdateExistingFolderConflict();
        if (_conflictingProject is null) return;
        ExistingProjectId = _conflictingProject.Id;
        DialogResult = true;
    }

    private static (string? Error, string? ApiUrl) InspectSelectedFolders(string apiDirectory, string frontendDirectory)
    {
        var projects = Directory.GetFiles(apiDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length != 1)
            return ("The Backend API working folder must contain exactly one top-level .csproj file.", null);
        if (!File.Exists(Path.Combine(frontendDirectory, "angular.json")) ||
            !File.Exists(Path.Combine(frontendDirectory, "package.json")))
            return ("The Angular frontend working folder must contain both angular.json and package.json.", null);
        return (null, FullStackProjectDiscovery.TryGetApiUrl(apiDirectory));
    }

    private void SetBusy(bool busy)
    {
        if (ScanButton == null || ContinueButton == null) return;
        ScanButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _scanVersion++;

    private void ShowValidation(string message) =>
        MessageBox.Show(this, message, "Check new project", MessageBoxButton.OK, MessageBoxImage.Warning);
}
