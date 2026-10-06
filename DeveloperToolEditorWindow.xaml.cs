using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class DeveloperToolEditorWindow : Window
{
    private readonly string _settingsDirectory;
    private readonly DeveloperTool _original;
    private readonly bool _legacyPgAdmin;
    private readonly bool _website;
    private string? _appDefaultName;
    private bool _initialized;
    private bool _closed;
    private bool _initializing = true;
    private bool _loadingCredentials;
    private bool _loadingBrowsers;
    private bool _settingCredentialSelection;
    private bool _settingBrowserSelection;
    private string? _credentialId;
    private string? _browserId;
    private readonly IReadOnlyList<DeveloperTool> _relatedTools;
    private IReadOnlyList<ToolCredentialProfile> _credentialProfiles = [];
    private IReadOnlyList<InstalledWebsiteBrowser> _installedBrowsers = [];
    private sealed record CredentialChoice(string? Id, string Name, ToolCredentialProfile? Profile = null);
    private sealed record BrowserChoice(string? Id, string Name, InstalledWebsiteBrowser? Browser = null);

    public DeveloperTool Result { get; private set; }

    public DeveloperToolEditorWindow(DeveloperTool tool, string settingsDirectory, bool isNew = false, IReadOnlyList<DeveloperTool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _original = Clone(tool);
        Result = Clone(tool);
        _legacyPgAdmin = string.Equals(tool.Kind, "PgAdmin", StringComparison.OrdinalIgnoreCase);
        _website = string.Equals(tool.Kind, "Website", StringComparison.OrdinalIgnoreCase);
        _relatedTools = tools ?? [tool.Clone()];
        _credentialId = tool.CredentialProfileId;
        _browserId = _website ? tool.WebsiteBrowser : null;
        if (!_website) _appDefaultName = isNew ? tool.Name : ExecutableDefaultName(tool.Target);

        InitializeComponent();
        Title = $"{(isNew ? "Add" : "Edit")} {(_website ? "website" : "desktop app")}";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        NameInput.Text = tool.Name ?? "";
        TargetInput.Text = tool.Target ?? "";
        UsernameSelectorInput.Text = tool.WebsiteUsernameSelector ?? "";
        PasswordSelectorInput.Text = tool.WebsitePasswordSelector ?? "";
        AutomaticPgAdmin.IsChecked = _legacyPgAdmin && string.IsNullOrWhiteSpace(tool.Target);
        _initialized = true;
        UpdateTargetControls();
        PopulateBrowserChoices();
        UpdatePendingControls();
        Closed += (_, _) => _closed = true;
        Loaded += async (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
            try
            {
                await Task.WhenAll(LoadCredentialProfilesAsync(), LoadBrowsersAsync());
            }
            finally
            {
                _initializing = false;
                UpdatePendingControls();
            }
        };
    }

    private void AutomaticPgAdmin_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ClearValidation();
        UpdateTargetControls();
    }

    private void UpdateTargetControls()
    {
        var automatic = !_website && _legacyPgAdmin && AutomaticPgAdmin.IsChecked == true;
        TargetLabel.Content = _website ? "_Website URL" : "_Executable path";
        AutomationProperties.SetName(TargetInput, _website ? "Website URL" : "Application executable path");
        TargetInput.ToolTip = _website
            ? "Complete http:// or https:// address. Opens with the selected browser."
            : $"Choose an .exe without command arguments. Relative paths start at {_settingsDirectory}. Environment variables are supported.";
        BrowseTargetButton.Visibility = _website ? Visibility.Collapsed : Visibility.Visible;
        InstalledAppsButton.Visibility = _website ? Visibility.Collapsed : Visibility.Visible;
        AutomaticPgAdmin.Visibility = !_website && _legacyPgAdmin ? Visibility.Visible : Visibility.Collapsed;
        TargetInput.IsEnabled = !automatic;
        TargetLabel.IsEnabled = !automatic;
        BrowseTargetButton.IsEnabled = !automatic;
        WebsiteLoginOptions.Visibility = _website ? Visibility.Visible : Visibility.Collapsed;
        WebsiteBrowserOptions.Visibility = _website ? Visibility.Visible : Visibility.Collapsed;
        CredentialSelector.ToolTip = _website
            ? "Fill login fields using a saved profile for this website's exact origin."
            : "Run the desktop app as the selected Windows user. Choose None to use your current account.";
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) ClearValidation();
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_website) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a desktop app",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(TargetInput.Text))
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(TargetInput.Text.Trim()), _settingsDirectory);
                var directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
                if (File.Exists(path)) dialog.FileName = path;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException) { }

        try
        {
            if (dialog.ShowDialog(this) != true) return;
            UseDesktopApp(new ServiceEditorTarget(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            ShowValidation(ex.Message);
        }
    }

    private void InstalledApps_Click(object sender, RoutedEventArgs e)
    {
        if (_website) return;
        try
        {
            var picker = new InstalledDesktopAppsWindow { Owner = this };
            if (picker.ShowDialog() == true && picker.Result is { } selected)
                UseDesktopApp(selected);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            ShowValidation(ex.Message);
        }
    }

    private void UseDesktopApp(ServiceEditorTarget app)
    {
        var target = PortableExecutablePath(app.FileName);
        if (string.IsNullOrWhiteSpace(NameInput.Text)
            || (_appDefaultName is not null && string.Equals(NameInput.Text.Trim(), _appDefaultName.Trim(), StringComparison.Ordinal)))
            NameInput.Text = app.Name;
        _appDefaultName = app.Name;
        AutomaticPgAdmin.IsChecked = false;
        TargetInput.Text = target;
    }

    private static string? ExecutableDefaultName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        try { return Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(target.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or SecurityException) { return null; }
    }

    private string PortableExecutablePath(string executable)
    {
        var fullPath = Path.GetFullPath(executable);
        var settingsPrefix = _settingsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(settingsPrefix, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(_settingsDirectory, fullPath);

        var environmentRoots = new[] { "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE" }
            .Select(name => (Name: name, Root: Environment.GetEnvironmentVariable(name)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Root))
            .OrderByDescending(entry => entry.Root!.Length);
        foreach (var entry in environmentRoots)
        {
            var prefix = Path.GetFullPath(entry.Root!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"%{entry.Name}%{Path.DirectorySeparatorChar}{fullPath[prefix.Length..]}";
        }
        return Path.GetRelativePath(_settingsDirectory, fullPath);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || _loadingCredentials || _loadingBrowsers) return;
        var candidate = Clone(_original);
        candidate.Name = NameInput.Text.Trim();
        candidate.Target = !_website && _legacyPgAdmin && AutomaticPgAdmin.IsChecked == true
            ? ""
            : TargetInput.Text.Trim();
        var choice = CredentialSelector.SelectedItem as CredentialChoice;
        var browserChoice = BrowserSelector.SelectedItem as BrowserChoice;
        if (_website) candidate.WebsiteBrowser = browserChoice?.Id;
        candidate.CredentialProfileId = choice?.Id;
        candidate.WebsiteUsernameSelector = _website ? EmptyToNull(UsernameSelectorInput.Text) : null;
        candidate.WebsitePasswordSelector = _website ? EmptyToNull(PasswordSelectorInput.Text) : null;
        try
        {
            if (_website)
            {
                WebsiteBrowserLauncher.ValidateBrowserId(candidate.WebsiteBrowser);
                if (browserChoice?.Id is not null && browserChoice.Browser is null)
                    throw new InvalidOperationException("The selected browser is unavailable. Install it or choose another browser.");
            }
            if (choice?.Id is not null)
            {
                if (choice.Profile is null) throw new InvalidOperationException("The credential profile is unavailable. Select another profile or None.");
                if (_website && !string.Equals(choice.Profile.Origin, WebsiteOrigin(candidate.Target), StringComparison.Ordinal))
                    throw new ArgumentException("The website credential profile belongs to another origin. Choose the matching profile or correct the website URL.");
            }
            DeveloperToolLauncher.Validate(candidate, _settingsDirectory, requireExistingFile: true);
            Result = candidate;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            ShowValidation(ex.Message);
            if (string.IsNullOrWhiteSpace(candidate.Name)) NameInput.Focus();
            else if (_website && browserChoice?.Id is not null && (browserChoice.Browser is null || ex is FileNotFoundException)) BrowserSelector.Focus();
            else if (TargetInput.IsEnabled) TargetInput.Focus();
        }
    }

    private void ClearValidation()
    {
        ValidationText.Text = "";
        ValidationPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationPanel.Visibility = Visibility.Visible;
        ValidationPanel.BringIntoView();
    }

    private async Task LoadCredentialProfilesAsync()
    {
        if (_closed || _loadingCredentials) return;
        _loadingCredentials = true;
        UpdatePendingControls();
        try
        {
            var profiles = await Task.Run(ToolCredentialStore.ListProfiles);
            if (_closed) return;
            _credentialProfiles = profiles;
            PopulateCredentialChoices();
        }
        catch (Exception)
        {
            if (_closed) return;
            _credentialProfiles = [];
            PopulateCredentialChoices();
            ShowValidation("Saved credential profiles could not be read. Check Windows Credential Manager and try again.");
        }
        finally
        {
            _loadingCredentials = false;
            UpdatePendingControls();
        }
    }

    private async Task LoadBrowsersAsync()
    {
        if (!_website || _closed || _loadingBrowsers) return;
        _loadingBrowsers = true;
        UpdatePendingControls();
        try
        {
            var browsers = await Task.Run(WebsiteBrowserLauncher.GetInstalledBrowsers);
            if (_closed) return;
            _installedBrowsers = browsers;
            PopulateBrowserChoices();
        }
        catch (Exception)
        {
            if (_closed) return;
            _installedBrowsers = [];
            PopulateBrowserChoices();
            ShowValidation("Installed browsers could not be read. Reopen this editor and try again.");
        }
        finally
        {
            _loadingBrowsers = false;
            UpdatePendingControls();
        }
    }

    private void UpdatePendingControls()
    {
        if (_closed) return;
        var pending = _initializing || _loadingCredentials || _loadingBrowsers;
        SaveButton.IsEnabled = !pending;
        ManageCredentialsButton.IsEnabled = !pending;
        BrowserSelector.IsEnabled = !_initializing && !_loadingBrowsers;
        CredentialSelector.IsEnabled = !_initializing && !_loadingCredentials;
    }

    private void PopulateBrowserChoices()
    {
        if (!_initialized || !_website) return;
        var choices = new List<BrowserChoice> { new(null, "Automatic") };
        choices.AddRange(_installedBrowsers.Select(browser => new BrowserChoice(browser.Id, browser.Name, browser)));
        if (_browserId is not null && !choices.Any(choice => string.Equals(choice.Id, _browserId, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new(_browserId, "Unavailable browser"));
        _settingBrowserSelection = true;
        try
        {
            BrowserSelector.ItemsSource = choices;
            BrowserSelector.SelectedItem = choices.First(choice => string.Equals(choice.Id, _browserId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _settingBrowserSelection = false; }
    }

    private void BrowserSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _settingBrowserSelection) return;
        _browserId = (BrowserSelector.SelectedItem as BrowserChoice)?.Id;
        ClearValidation();
    }

    private void PopulateCredentialChoices()
    {
        if (!_initialized) return;
        var selectedId = _credentialId;
        var choices = new List<CredentialChoice> { new(null, "None") };
        choices.AddRange(_credentialProfiles.Where(profile => profile.Kind == (_website ? "Website" : "Windows"))
            .Select(profile => new CredentialChoice(profile.Id, profile.Name, profile)));
        if (selectedId is not null && !choices.Any(choice => choice.Id == selectedId))
            choices.Add(new(selectedId, "Unavailable profile"));
        _settingCredentialSelection = true;
        try
        {
            CredentialSelector.ItemsSource = choices;
            CredentialSelector.SelectedItem = choices.First(choice => choice.Id == selectedId);
        }
        finally { _settingCredentialSelection = false; }
    }

    private void CredentialSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _settingCredentialSelection) return;
        var id = (CredentialSelector.SelectedItem as CredentialChoice)?.Id;
        _credentialId = id;
        ClearValidation();
    }

    private async void ManageCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || _loadingCredentials || _loadingBrowsers) return;
        var manager = new ToolCredentialManagerWindow(_website ? "Website" : "Windows", _website ? WebsiteOrigin(TargetInput.Text) : null,
            _credentialId, _relatedTools) { Owner = this };
        var use = manager.ShowDialog() == true;
        if (use && manager.SelectedProfile is { } selected)
        {
            _credentialId = selected.Id;
        }
        await LoadCredentialProfilesAsync();
        if (!_closed) CredentialSelector.Focus();
    }

    private void BrowserSetup_Click(object sender, RoutedEventArgs e)
    {
        try { ToolBrowserSetupWindow.Show(this); }
        catch (Exception) { ShowValidation("Browser setup could not open. Check local extension storage and try again."); }
    }

    private static string? WebsiteOrigin(string value) => ToolCredentialStore.TryWebsiteOrigin(value, out var origin)
        ? origin : null;
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DeveloperTool Clone(DeveloperTool tool) => tool.Clone();
}
