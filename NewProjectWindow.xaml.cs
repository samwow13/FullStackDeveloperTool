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
    private bool _hasExistingFolderConflict;
    private bool _showingServices;
    private bool _busy;
    private string? _suggestedName;
    private ProjectProfile? _reviewDraft;
    private string? _reviewKey;
    private bool _applyingDetectedType;
    private string? _typeDiscoveryRoot;
    private ProjectTypeDiscoveryResult? _typeDiscovery;
    private int _appFolderVersion;
    private bool _selectedPackageIsFlutter;

    private enum ProjectType { FullStack, Console, FlutterDart }
    private ProjectType SelectedType => ConsoleType.IsChecked == true ? ProjectType.Console
        : FlutterDartType.IsChecked == true ? ProjectType.FlutterDart : ProjectType.FullStack;
    private string AppLabel => SelectedType switch
    {
        ProjectType.FlutterDart => "Flutter / Dart app",
        ProjectType.Console => "Console app",
        _ => "Angular frontend"
    };

    public ProjectProfile Result { get; private set; } = new();

    public NewProjectWindow(string settingsDirectory, IReadOnlyList<ProjectProfile> savedProjects)
    {
        InitializeComponent();
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _savedProjects = savedProjects;
        ApiDirectoryBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ServiceFolder_TextChanged));
        FrontendDirectoryBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ServiceFolder_TextChanged));
        ProjectNameBox.Text = "My Angular project";
        UpdateProjectTypePresentation();
        UpdateStepPresentation();
        Loaded += (_, _) => RootPathBox.Focus();
    }

    private async void ProjectType_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingDetectedType || DiscoveryPanel == null || ProjectNameBox == null || RootPathBox == null || FlutterTargetPanel == null) return;
        _reviewDraft = null;
        ResetDiscovery();
        UpdateProjectTypePresentation();
        if (_showingServices) await ScanAsync();
    }

    private void UpdateProjectTypePresentation()
    {
        var fullStack = SelectedType == ProjectType.FullStack;
        DiscoveryPanel.Visibility = Visibility.Visible;
        ConsoleFolderText.Visibility = SelectedType == ProjectType.Console ? Visibility.Visible : Visibility.Collapsed;
        DatabaseHelpText.Visibility = fullStack ? Visibility.Visible : Visibility.Collapsed;
        ScanStatusText.Visibility = SelectedType == ProjectType.FlutterDart ? Visibility.Collapsed : Visibility.Visible;
        UpdateFlutterTargetPresentation();
        ApiFolderLabel.Visibility = ApiFolderPanel.Visibility = ApiFolderHelp.Visibility =
            fullStack ? Visibility.Visible : Visibility.Collapsed;
        AppFolderLabel.Text = fullStack ? "Angular app folder" : AppLabel + " folder";
        DiscoveryTitleText.Text = fullStack ? "Detected services" : SelectedType == ProjectType.Console ? "Working folder" : "App package";
        AppFolderHelp.Text = fullStack
            ? "Folder containing angular.json and package.json."
            : SelectedType == ProjectType.Console ? "Folder used to run the console command." : "Contains pubspec.yaml.";
        System.Windows.Automation.AutomationProperties.SetName(FrontendDirectoryBox, AppLabel + " working folder");
        System.Windows.Automation.AutomationProperties.SetName(BrowseAppButton, "Browse " + AppLabel + " working folder");
        UpdateStepPresentation();
    }

    private void UpdateStepPresentation()
    {
        FolderPage.Visibility = _showingServices ? Visibility.Collapsed : Visibility.Visible;
        ServicesPage.Visibility = _showingServices ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _showingServices ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = _showingServices ? "STEP 2 OF 3 · SERVICES" : "STEP 1 OF 3 · FOLDER";
        PageTitleText.Text = _showingServices ? "Review your services" : "Choose your project folder";
        IntroHelpText.Text = _showingServices
            ? "Check the detected folders. Choose another match, type a path, or browse to adjust."
            : "Select the top-level folder containing your project. We use it to find your services.";
        if (_showingServices && SelectedType == ProjectType.Console)
            IntroHelpText.Text = "Name your project, then review its command and arguments.";
        ContinueButton.Content = _showingServices ? "Review commands" : "Continue";
        NextStepText.Text = _showingServices ? "Next: check commands and save." : "Next: detect services.";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _showingServices = false;
        UpdateStepPresentation();
        PageScrollViewer.ScrollToTop();
        RootPathBox.Focus();
    }

    private async Task<bool> DetectProjectTypeAsync(string root)
    {
        // Returning to the same root keeps the user's manual type selection.
        if (string.Equals(_typeDiscoveryRoot, root, StringComparison.OrdinalIgnoreCase)) return true;
        var version = ++_scanVersion;
        SetBusy(true);
        RootFolderHelpText.Text = "Detecting project type and working folders…";
        try
        {
            var found = await Task.Run(() => ProjectTypeDiscovery.Discover(root));
            if (version != _scanVersion || !TryResolveRoot(out var currentRoot, out _) ||
                !string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase)) return false;
            _typeDiscoveryRoot = root;
            _typeDiscovery = found;
            _applyingDetectedType = true;
            try
            {
                switch (found.SelectedType)
                {
                    case DetectedProjectType.FullStack: FullStackType.IsChecked = true; break;
                    case DetectedProjectType.Console: ConsoleType.IsChecked = true; break;
                    case DetectedProjectType.FlutterDart: FlutterDartType.IsChecked = true; break;
                }
            }
            finally { _applyingDetectedType = false; }
            TypeDetectionText.Text = found.SelectedType switch
            {
                DetectedProjectType.FullStack => "Detected Angular Full Stack. Change type if needed.",
                DetectedProjectType.Console => "Detected Console project. Change type if needed.",
                DetectedProjectType.FlutterDart => "Detected Flutter / Dart app. Commands follow the selected package.",
                _ => "Project type unclear. Choose a type; scan details show what was found."
            };
            UpdateProjectTypePresentation();
            UpdateExistingFolderConflict();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or SecurityException)
        {
            if (version != _scanVersion) return false;
            TypeDetectionText.Text = "Project type could not be detected. Choose a type manually.";
            return true;
        }
        finally
        {
            if (version == _scanVersion)
            {
                SetBusy(false);
                RootFolderHelpText.Text = "Project type and working folders are detected when you continue.";
            }
        }
    }

    private void UpdateFlutterTargetPresentation()
    {
        if (FlutterTargetPanel == null || FlutterDartType == null) return;
        FlutterTargetPanel.Visibility = SelectedType == ProjectType.FlutterDart && _selectedPackageIsFlutter
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshPackageKindAsync()
    {
        var version = ++_appFolderVersion;
        _selectedPackageIsFlutter = false;
        UpdateFlutterTargetPresentation();
        if (SelectedType != ProjectType.FlutterDart || !TryResolveRoot(out var root, out _) ||
            string.IsNullOrWhiteSpace(FrontendDirectoryBox.Text)) return;
        var folder = FrontendDirectoryBox.Text;
        try
        {
            var path = Path.GetFullPath(folder.Trim(), root);
            await Task.Delay(180);
            if (version != _appFolderVersion) return;
            var flutter = await Task.Run(() => DartProjectDiscovery.IsFlutterProject(path));
            if (version != _appFolderVersion || SelectedType != ProjectType.FlutterDart ||
                FrontendDirectoryBox.Text != folder) return;
            _selectedPackageIsFlutter = flutter;
            UpdateFlutterTargetPresentation();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or SecurityException) { }
    }

    private void RootPath_Changed(object sender, TextChangedEventArgs e)
    {
        _typeDiscoveryRoot = null;
        _typeDiscovery = null;
        if (TypeDetectionText != null) TypeDetectionText.Text = "";
        if (RootFolderHelpText != null)
            RootFolderHelpText.Text = "Project type and working folders are detected when you continue.";
        ResetDiscovery();
    }

    private void ResetDiscovery()
    {
        if (ApiDirectoryBox == null || FrontendDirectoryBox == null || ScanStatusText == null ||
            ScanNotesList == null || ManualConfigurationWarning == null) return;
        _scanVersion++;
        _appFolderVersion++;
        _selectedPackageIsFlutter = false;
        UpdateFlutterTargetPresentation();
        _scannedRoot = null;
        _reviewDraft = null;
        ApiDirectoryBox.ItemsSource = null;
        ApiDirectoryBox.Text = "";
        FrontendDirectoryBox.ItemsSource = null;
        FrontendDirectoryBox.Text = "";
        ScanNotesList.ItemsSource = null;
        ScanStatusText.Text = SelectedType == ProjectType.FullStack
            ? "Scan this folder to find the API and Angular working folders."
            : $"Scan this folder to find {AppLabel} packages.";
        ManualConfigurationWarning.Visibility = Visibility.Collapsed;
        UpdateExistingFolderConflict();
        SetBusy(false);
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
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
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(useDetectedResults: false);

    private async Task ScanAsync(bool useDetectedResults = true)
    {
        // A manual rescan must not let a later type switch restore older folder candidates.
        if (!useDetectedResults) _typeDiscovery = null;
        var type = SelectedType;
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
            var detected = useDetectedResults && string.Equals(_typeDiscoveryRoot, root, StringComparison.OrdinalIgnoreCase)
                ? _typeDiscovery : null;
            if (type == ProjectType.Console)
            {
                var foundConsole = detected ?? await Task.Run(() => ProjectTypeDiscovery.Discover(root));
                if (version != _scanVersion) return;
                var folders = foundConsole.ConsoleDirectories;
                FrontendDirectoryBox.ItemsSource = folders.ToArray();
                FrontendDirectoryBox.Text = frontendSelection.Length > 0 ? frontendSelection
                    : folders.Count == 1 ? folders[0] : folders.Count == 0 ? root : "";
                ScanNotesList.ItemsSource = foundConsole.Notes.ToArray();
                ScanStatusText.Text = folders.Count == 0 ? "Using the selected root folder. Review its command next."
                    : $"Found {folders.Count} console app folder(s).";
                if (folders.Count > 1 && frontendSelection.Length == 0)
                {
                    ManualConfigurationWarning.Text = "Multiple console apps found. Choose the app to run.";
                    ManualConfigurationWarning.Visibility = Visibility.Visible;
                }
                UpdateExistingFolderConflict();
                return;
            }
            if (type == ProjectType.FlutterDart)
            {
                var packages = detected?.Dart ?? await Task.Run(() => DartProjectDiscovery.Discover(root));
                if (version != _scanVersion) return;
                var folders = packages.FlutterDirectories.Concat(packages.DartDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                FrontendDirectoryBox.ItemsSource = folders.ToArray();
                // Multiple packages require an explicit choice rather than guessing which app to run.
                FrontendDirectoryBox.Text = frontendSelection.Length > 0 ? frontendSelection
                    : folders.Length == 1 ? folders[0] : "";
                _selectedPackageIsFlutter = packages.FlutterDirectories.Contains(FrontendDirectoryBox.Text, StringComparer.OrdinalIgnoreCase);
                UpdateFlutterTargetPresentation();
                ScanNotesList.ItemsSource = (detected?.Notes ?? []).Concat(packages.Notes).Distinct().ToArray();
                if (folders.Length != 1 && frontendSelection.Length == 0)
                {
                    ManualConfigurationWarning.Text = folders.Length == 0
                        ? $"No {AppLabel} package found. Browse or type its working folder."
                        : $"Multiple {AppLabel} packages found. Choose the app to run.";
                    ManualConfigurationWarning.Visibility = Visibility.Visible;
                }
                UpdateExistingFolderConflict();
                return;
            }
            var found = detected?.FullStack ?? await Task.Run(() => FullStackProjectDiscovery.Discover(root));
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
            ScanNotesList.ItemsSource = (detected?.Notes ?? []).Concat(found.Notes).Distinct().ToArray();
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
            ManualConfigurationWarning.Text = $"Choose existing working folders manually before continuing. Scan detail: {ex.Message}";
            ManualConfigurationWarning.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == _scanVersion) SetBusy(false);
        }
    }

    private void BrowseApi_Click(object sender, RoutedEventArgs e) => BrowseServiceFolder(ApiDirectoryBox, "Choose the Backend API working folder");

    private void BrowseFrontend_Click(object sender, RoutedEventArgs e) => BrowseServiceFolder(FrontendDirectoryBox, $"Choose the {AppLabel} working folder");

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
        if (_busy) return;
        if (!TryResolveRoot(out var root, out var message))
        {
            ShowValidation(message);
            RootPathBox.Focus();
            return;
        }

        if (!_showingServices)
        {
            if (!await DetectProjectTypeAsync(root)) return;
            if (string.IsNullOrWhiteSpace(ProjectNameBox.Text) || ProjectNameBox.Text == "My Angular project" ||
                ProjectNameBox.Text == _suggestedName)
            {
                _suggestedName = new DirectoryInfo(root).Name;
                ProjectNameBox.Text = string.IsNullOrWhiteSpace(_suggestedName) ? "My project" : _suggestedName;
            }
            SelectedRootText.Text = root;
            _showingServices = true;
            UpdateStepPresentation();
            PageScrollViewer.ScrollToTop();
            if (!string.Equals(_scannedRoot, root, StringComparison.OrdinalIgnoreCase))
                await ScanAsync();
            ProjectNameBox.Focus();
            return;
        }
        var name = ProjectNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("Enter a project name.");
            ProjectNameBox.Focus();
            return;
        }

        if (ConsoleType.IsChecked == true)
        {
            if (!TryResolveServiceFolder(FrontendDirectoryBox.Text, root, "Console app", out var consoleDirectory)) return;
            UpdateExistingFolderConflict();
            if (_hasExistingFolderConflict)
            {
                ShowValidation(ExistingFolderWarning.Text);
                return;
            }
            Result = new ProjectProfile
            {
                Name = name, RootPath = Path.GetRelativePath(_settingsDirectory, root),
                Services =
                [
                    new() { Name = "Console app", Kind = "Console", WorkingDirectory = Path.GetRelativePath(root, consoleDirectory),
                        StartCommand = "dotnet run", Url = "", UiPath = "" }
                ]
            };
            ReviewProject();
            return;
        }

        if (SelectedType == ProjectType.FlutterDart)
        {
            await ContinueAppAsync(name, root);
            return;
        }

        if (!string.Equals(_scannedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            await ScanAsync();
            if (SelectedType != ProjectType.FullStack || !TryResolveRoot(out var currentRoot, out _) ||
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
            if (currentScanVersion == _scanVersion) SetBusy(false);
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
                    ApiConfiguration = new() { Environment = "Local" },
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
        ReviewProject();
    }

    private async Task ContinueAppAsync(string name, string root)
    {
        var type = SelectedType;
        if (!string.Equals(_scannedRoot, root, StringComparison.OrdinalIgnoreCase))
            await ScanAsync();
        if (SelectedType != type || !TryResolveRoot(out var currentRoot, out _) ||
            !string.Equals(currentRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        if (!TryResolveServiceFolder(FrontendDirectoryBox.Text, root, AppLabel, out var appDirectory)) return;
        UpdateExistingFolderConflict();
        if (_hasExistingFolderConflict)
        {
            ShowValidation(ExistingFolderWarning.Text);
            return;
        }
        var folderChoice = FrontendDirectoryBox.Text;
        var version = _scanVersion;
        SetBusy(true);
        (bool Flutter, bool Dart) package;
        try
        {
            package = await Task.Run(() =>
            {
                var flutter = DartProjectDiscovery.IsFlutterProject(appDirectory);
                return (flutter, !flutter && DartProjectDiscovery.IsDartProject(appDirectory));
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowValidation($"Could not inspect the {AppLabel} folder: {ex.Message}");
            return;
        }
        finally { if (version == _scanVersion) SetBusy(false); }
        if (version != _scanVersion || SelectedType != type || FrontendDirectoryBox.Text != folderChoice ||
            !TryResolveRoot(out currentRoot, out _) || !string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (!package.Flutter && !package.Dart)
        {
            ShowValidation("Choose a Flutter or Dart working folder with a valid pubspec.yaml. Flutter packages must declare the Flutter SDK dependency.");
            return;
        }
        var web = package.Flutter && FlutterTargetBox.SelectedIndex == 1;
        var service = new ServiceProfile
        {
            Name = package.Dart ? "Dart app" : web ? "Flutter web app" : "Flutter app",
            Kind = package.Dart ? "Dart" : web ? "Flutter Web" : "Flutter",
            WorkingDirectory = Path.GetRelativePath(root, appDirectory),
            StartCommand = package.Dart ? "dart run"
                : web ? "flutter run -d web-server --web-hostname 127.0.0.1 --web-port 4200"
                : "flutter run -d windows --no-resident",
            CleanCommand = package.Flutter ? "flutter clean" : "",
            SetupCommand = package.Flutter ? "flutter pub get" : "dart pub get",
            Url = web ? "http://127.0.0.1:4200" : "",
            UiPath = web ? "/" : ""
        };
        Result = new ProjectProfile
        {
            Name = name, RootPath = Path.GetRelativePath(_settingsDirectory, root),
            Services = [service]
        };
        ReviewProject();
    }

    private void ReviewProject()
    {
        // Keep command edits when returning from review; changed discovery inputs get a fresh draft.
        var key = string.Join("\n", SelectedType, RootPathBox.Text, ApiDirectoryBox.Text,
            FrontendDirectoryBox.Text, FlutterTargetBox.SelectedIndex, string.Join(",", Result.Services.Select(service => service.Kind)));
        var candidate = _reviewKey == key && _reviewDraft is not null ? _reviewDraft : Result;
        candidate.Name = ProjectNameBox.Text.Trim();
        var editor = new ProfileEditorWindow(candidate, _settingsDirectory, _savedProjects, isSetupReview: true) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            _reviewDraft = editor.Draft;
            _reviewKey = key;
            ContinueButton.Focus();
            return;
        }
        Result = editor.Result;
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

    private async void ServiceFolder_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateExistingFolderConflict();
        await RefreshPackageKindAsync();
    }

    private async void ServiceFolder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExistingFolderConflict();
        await RefreshPackageKindAsync();
    }

    private void UpdateExistingFolderConflict()
    {
        if (ExistingFolderWarning == null ||
            ApiDirectoryBox == null || FrontendDirectoryBox == null) return;
        _hasExistingFolderConflict = false;
        ExistingFolderWarning.Visibility = Visibility.Collapsed;
        if (!TryResolveRoot(out var root, out _)) return;

        var conflicts = new List<(string SelectedService, ProjectProfile Project, ServiceProfile Service)>();
        var selectedFolders = SelectedType switch
        {
            ProjectType.Console => new[] { ("Console app", FrontendDirectoryBox.Text) },
            ProjectType.FullStack => new[] { ("Backend API", ApiDirectoryBox.Text), ("Angular frontend", FrontendDirectoryBox.Text) },
            _ => new[] { (AppLabel, FrontendDirectoryBox.Text) }
        };
        foreach (var (selectedService, text) in selectedFolders)
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
        ExistingFolderWarning.Text = string.Join(" ", conflicts.Select(conflict =>
            $"{conflict.SelectedService} folder overlaps saved '{conflict.Project.Name}' / '{conflict.Service.Name}'.")) +
            " Choose different folders or select the saved project from the project list.";
        ExistingFolderWarning.Visibility = Visibility.Visible;
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
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
        BackButton.IsEnabled = RootPathBox.IsEnabled = BrowseRootButton.IsEnabled = !busy;
        FullStackType.IsEnabled = ConsoleType.IsEnabled = FlutterDartType.IsEnabled = !busy;
        FlutterTargetBox.IsEnabled = ProjectNameBox.IsEnabled = !busy;
        ApiDirectoryBox.IsEnabled = FrontendDirectoryBox.IsEnabled = !busy;
        BrowseApiButton.IsEnabled = BrowseAppButton.IsEnabled = !busy;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _scanVersion++;
        _appFolderVersion++;
    }

    private void ShowValidation(string message) =>
        MessageBox.Show(this, message, "Check new project", MessageBoxButton.OK, MessageBoxImage.Warning);
}
