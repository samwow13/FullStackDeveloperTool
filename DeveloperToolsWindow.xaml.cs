using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DeveloperToolsWindow : Window
{
    private readonly string _settingsDirectory;
    private readonly ObservableCollection<DeveloperTool> _tools;
    private readonly IReadOnlyList<DeveloperTool> _savedTools;
    public List<DeveloperTool> Result { get; private set; }

    public DeveloperToolsWindow(IEnumerable<DeveloperTool> tools, string settingsDirectory)
    {
        InitializeComponent();
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        Result = tools.Select(Clone).ToList();
        _savedTools = Result.Select(Clone).ToArray();
        _tools = new ObservableCollection<DeveloperTool>(Result);
        ToolsList.ItemsSource = _tools;
        UpdateEmptyState();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += (_, _) => AddDesktopButton.Focus();
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var picker = new InstalledDesktopAppsWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.Result is not { } app) return;
        AddTool(new DeveloperTool { Name = app.Name, Kind = "Application", Target = app.FileName });
    }

    private void AddWebsite_Click(object sender, RoutedEventArgs e) => AddTool(new DeveloperTool
    {
        Name = "", Kind = "Website", Target = "https://"
    });

    private void AddTool(DeveloperTool tool)
    {
        var editor = new DeveloperToolEditorWindow(tool, _settingsDirectory, isNew: true, tools: _savedTools.Concat(_tools).ToArray()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var candidate = Clone(editor.Result);
        while (_tools.Any(existing => existing.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase)))
            candidate.Id = Guid.NewGuid().ToString("N");
        _tools.Add(candidate);
        ToolsList.SelectedItem = candidate;
        ToolsList.ScrollIntoView(candidate);
        UpdateEmptyState();
    }

    private void Configuration_Click(object sender, RoutedEventArgs e)
    {
        var configuration = new DeveloperToolsConfigurationWindow(_tools, _settingsDirectory, _savedTools) { Owner = this };
        if (configuration.ShowDialog() != true) return;
        _tools.Clear();
        foreach (var tool in configuration.Result) _tools.Add(Clone(tool));
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyToolsHint.Visibility = _tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ValidationText.Visibility = Visibility.Collapsed;
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
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private static DeveloperTool Clone(DeveloperTool tool) => tool.Clone();
}
