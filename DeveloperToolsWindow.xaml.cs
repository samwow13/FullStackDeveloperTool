using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class DeveloperToolsWindow : Window
{
    private readonly string _settingsDirectory;
    private readonly ObservableCollection<DeveloperTool> _tools;
    public List<DeveloperTool> Result { get; private set; }

    public DeveloperToolsWindow(IEnumerable<DeveloperTool> tools, string settingsDirectory)
    {
        InitializeComponent();
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        Result = tools.Select(Clone).ToList();
        _tools = new ObservableCollection<DeveloperTool>(Result);
        SettingsDirectoryHint.Text = $"Relative executable paths start at: {_settingsDirectory}";
        ToolsList.ItemsSource = _tools;
        ToolsList.SelectedItem = _tools.FirstOrDefault();
        UpdateSelection();
    }

    private void ToolsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (ToolForm == null) return;
        var tool = ToolsList.SelectedItem as DeveloperTool;
        ToolForm.DataContext = tool;
        ToolForm.Visibility = tool == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyToolsHint.Visibility = tool == null ? Visibility.Visible : Visibility.Collapsed;
        RemoveToolButton.IsEnabled = tool != null;
        UpdateTargetHint();
    }

    private void ToolName_LostFocus(object sender, RoutedEventArgs e) => ToolsList.Items.Refresh();

    private void KindSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTargetHint();

    private void UpdateTargetHint()
    {
        if (TargetLabel == null || TargetHint == null || BrowseTargetButton == null) return;
        var kind = KindSelector.SelectedValue as string;
        BrowseTargetButton.Visibility = kind == "Website" ? Visibility.Collapsed : Visibility.Visible;
        switch (kind)
        {
            case "PgAdmin":
                TargetLabel.Text = "pgAdmin executable override (optional)";
                TargetHint.Text = "Leave this blank to find your installed pgAdmin 4 automatically. " +
                    "It opens pgAdmin's own desktop/login window, so you do not need to track a changing local port.";
                break;
            case "Website":
                TargetLabel.Text = "Website URL";
                TargetHint.Text = "Use a full http:// or https:// address. For pgAdmin hosted on a server, " +
                    "save its login page URL here. The link opens in your default browser.";
                break;
            default:
                TargetLabel.Text = "Application executable";
                TargetHint.Text = "Choose an existing .exe file. Relative paths and environment variables such as " +
                    "%LOCALAPPDATA% or %ProgramFiles% are supported. Enter the file path without command-line arguments.";
                break;
        }
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e) => AddTool(new DeveloperTool
    {
        Name = "New desktop app", Kind = "Application"
    });

    private void AddWebsite_Click(object sender, RoutedEventArgs e) => AddTool(new DeveloperTool
    {
        Name = "New website", Kind = "Website", Target = "https://"
    });

    private void AddPgAdmin_Click(object sender, RoutedEventArgs e)
    {
        var tool = DeveloperTool.PgAdmin();
        tool.Id = Guid.NewGuid().ToString("N");
        AddTool(tool);
    }

    private void AddTool(DeveloperTool tool)
    {
        while (_tools.Any(existing => existing.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase)))
            tool.Id = Guid.NewGuid().ToString("N");
        _tools.Add(tool);
        ToolsList.SelectedItem = tool;
        ToolsList.ScrollIntoView(tool);
    }

    private void RemoveTool_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsList.SelectedItem is not DeveloperTool tool) return;
        var index = ToolsList.SelectedIndex;
        _tools.Remove(tool);
        if (_tools.Count > 0) ToolsList.SelectedIndex = Math.Min(index, _tools.Count - 1);
        UpdateSelection();
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsList.SelectedItem is not DeveloperTool tool) return;
        var dialog = new OpenFileDialog
        {
            Title = tool.Kind == "PgAdmin" ? "Choose pgAdmin4.exe" : "Choose an application executable",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(tool.Target))
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(tool.Target.Trim()), _settingsDirectory);
                var directory = Path.GetDirectoryName(path);
                if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
                if (File.Exists(path)) dialog.FileName = path;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        if (dialog.ShowDialog(this) != true) return;
        tool.Target = PortableExecutablePath(dialog.FileName);
        // Configuration models intentionally have no UI notifications.
        ToolForm.DataContext = null;
        ToolForm.DataContext = tool;
        UpdateTargetHint();
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
        var candidate = _tools.Select(Clone).ToList();
        try
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in candidate)
            {
                tool.Name = tool.Name.Trim();
                tool.Kind = tool.Kind.Trim();
                tool.Target = tool.Target.Trim();
                if (string.IsNullOrWhiteSpace(tool.Id) || !ids.Add(tool.Id))
                    throw new ArgumentException($"The shortcut '{tool.Name}' has a missing or duplicate ID. Remove it and add it again.");
                DeveloperToolLauncher.Validate(tool, _settingsDirectory, requireExistingFile: true);
            }
            Result = candidate;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "Check developer tools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DeveloperTool Clone(DeveloperTool tool) => new()
    {
        Id = tool.Id, Name = tool.Name, Kind = tool.Kind, Target = tool.Target
    };
}
