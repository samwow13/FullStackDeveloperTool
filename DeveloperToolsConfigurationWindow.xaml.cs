using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DeveloperToolsConfigurationWindow : Window
{
    private readonly string _settingsDirectory;
    private readonly ObservableCollection<DeveloperTool> _tools;
    private readonly IReadOnlyList<DeveloperTool> _savedTools;
    private bool _initialized;
    private bool _closed;
    private bool _discovering;
    private bool _installedLoaded;
    private bool _viewingCommand;
    private readonly CancellationTokenSource _installedDiscoveryLifetime = new();
    private IReadOnlyList<ServiceEditorTarget> _installedApps = [];
    public List<DeveloperTool> Result { get; private set; }

    public DeveloperToolsConfigurationWindow(IEnumerable<DeveloperTool> tools, string settingsDirectory, IReadOnlyList<DeveloperTool>? savedTools = null)
    {
        InitializeComponent();
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        Result = tools.Select(Clone).ToList();
        _savedTools = (savedTools ?? Result).Select(Clone).ToArray();
        _tools = new ObservableCollection<DeveloperTool>(Result);
        SavedToolsList.ItemsSource = _tools;
        _initialized = true;
        SavedToolsList.SelectedItem = _tools.FirstOrDefault();
        UpdateSelection();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Closed += (_, _) =>
        {
            _closed = true;
            _installedDiscoveryLifetime.Cancel();
            if (!_discovering) _installedDiscoveryLifetime.Dispose();
        };
        Loaded += (_, _) => SavedToolsList.Focus();
    }

    private void SavedToolsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = SavedToolsList.SelectedItem is DeveloperTool && !_viewingCommand;
        EditToolButton.IsEnabled = selected;
        ViewCommandButton.IsEnabled = selected;
        RemoveToolButton.IsEnabled = selected;
        EmptySavedHint.Visibility = _tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditTool_Click(object sender, RoutedEventArgs e)
    {
        if (SavedToolsList.SelectedItem is not DeveloperTool tool) return;
        var index = _tools.IndexOf(tool);
        var editor = new DeveloperToolEditorWindow(tool, _settingsDirectory, tools: CredentialReferences()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _tools[index] = Clone(editor.Result);
        SavedToolsList.SelectedIndex = index;
        UpdateSelection();
    }

    private void RemoveTool_Click(object sender, RoutedEventArgs e)
    {
        if (SavedToolsList.SelectedItem is not DeveloperTool tool) return;
        var index = SavedToolsList.SelectedIndex;
        _tools.Remove(tool);
        if (_tools.Count > 0) SavedToolsList.SelectedIndex = Math.Min(index, _tools.Count - 1);
        UpdateSelection();
    }

    private async void ToolTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || e.Source != ToolTabs) return;
        if (InstalledAppsTab.IsSelected && !_installedLoaded) await RefreshInstalledAppsAsync();
    }

    private async void RefreshInstalled_Click(object sender, RoutedEventArgs e) => await RefreshInstalledAppsAsync();

    private async Task RefreshInstalledAppsAsync()
    {
        if (_closed || _discovering) return;
        _discovering = true;
        RefreshInstalledButton.IsEnabled = false;
        AddInstalledButton.IsEnabled = false;
        InstalledAppsList.ItemsSource = null;
        InstalledStatusText.Text = "Checking installed apps…";
        InstalledStatusText.Visibility = Visibility.Visible;
        try
        {
            var token = _installedDiscoveryLifetime.Token;
            var installed = await Task.Run(() => DeveloperToolLauncher.FindInstalledDesktopTools(token), token);
            if (_closed) return;
            _installedApps = installed.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            _installedLoaded = true;
            ApplyInstalledSearch();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (_closed) return;
            _installedLoaded = false;
            InstalledStatusText.Text = $"Could not check installed apps: {ex.Message}";
            InstalledStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _discovering = false;
            if (_closed) _installedDiscoveryLifetime.Dispose();
            else
            {
                RefreshInstalledButton.IsEnabled = true;
                AddInstalledButton.IsEnabled = InstalledAppsList.SelectedItem is ServiceEditorTarget;
            }
        }
    }

    private void InstalledSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_closed && !_discovering && _installedLoaded) ApplyInstalledSearch();
    }

    private void ApplyInstalledSearch()
    {
        var selected = InstalledAppsList.SelectedItem as ServiceEditorTarget;
        var words = InstalledSearchInput.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = _installedApps.Where(app => words.All(word =>
            app.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            app.FileName.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        InstalledAppsList.ItemsSource = matches;
        InstalledAppsList.SelectedItem = selected is not null ? matches.FirstOrDefault(app => app.FileName.Equals(selected.FileName, StringComparison.OrdinalIgnoreCase)) : null;
        InstalledStatusText.Text = _installedApps.Count == 0 ? "No installed desktop apps found." : matches.Length == 0
            ? "No matching apps." : words.Length == 0 ? $"{matches.Length} apps" : $"{matches.Length} of {_installedApps.Count} apps";
        InstalledStatusText.Visibility = Visibility.Visible;
    }

    private void InstalledSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_discovering || InstalledAppsList.Items.Count == 0) return;
        if (InstalledAppsList.SelectedIndex < 0) InstalledAppsList.SelectedIndex = 0;
        InstalledAppsList.Focus();
    }

    private void InstalledApps_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddInstalled_Click(sender, e);
    }

    private void InstalledAppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) AddInstalledButton.IsEnabled = !_discovering && InstalledAppsList.SelectedItem is ServiceEditorTarget;
    }

    private void AddInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledAppsList.SelectedItem is not ServiceEditorTarget installed || _discovering || _viewingCommand) return;
        var draft = new DeveloperTool { Name = installed.Name, Kind = "Application", Target = installed.FileName };
        var editor = new DeveloperToolEditorWindow(draft, _settingsDirectory, isNew: true, tools: CredentialReferences()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var tool = Clone(editor.Result);
        while (_tools.Any(existing => existing.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase)))
            tool.Id = Guid.NewGuid().ToString("N");
        _tools.Add(tool);
        ToolTabs.SelectedIndex = 0;
        SavedToolsList.SelectedItem = tool;
        SavedToolsList.ScrollIntoView(tool);
        UpdateSelection();
    }

    private async void ViewCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _viewingCommand || SavedToolsList.SelectedItem is not DeveloperTool selected) return;
        var tool = Clone(selected);
        _viewingCommand = true;
        ToolTabs.IsEnabled = false;
        UpdateSelection();
        var website = tool.Kind.Equals("Website", StringComparison.OrdinalIgnoreCase);
        var command = "";
        string? error = null;
        try
        {
            var target = await Task.Run(() => DeveloperToolLauncher.Resolve(tool, _settingsDirectory));
            command = target.IsWebsite ? target.FileName : $"\"{target.FileName}\"";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            if (!website && ex is FileNotFoundException { FileName: { Length: > 0 } fileName }) command = $"\"{fileName}\"";
        }
        finally
        {
            _viewingCommand = false;
            if (!_closed)
            {
                ToolTabs.IsEnabled = true;
                UpdateSelection();
            }
        }
        if (!_closed) ShowCommand(tool.Name, website, command, error);
    }

    private void ShowCommand(string name, bool website, string command, string? error)
    {
        var dialog = new Window
        {
            Owner = this, Title = $"{name} · {(website ? "Website" : "Desktop app")}", Icon = Icon,
            Width = 600, Height = 290, MinWidth = 480, MinHeight = 240,
            MaxHeight = SystemParameters.WorkArea.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = (Brush)FindResource("BackgroundBrush"), Foreground = (Brush)FindResource("TextBrush")
        };
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = website ? "Website URL" : "Launch command", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        var text = new TextBox { Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
        AutomationProperties.SetName(text, website ? "Website URL" : "Desktop launch command");
        var status = new TextBlock { Text = error ?? "", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 10, 0, 0), Visibility = error == null ? Visibility.Collapsed : Visibility.Visible };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Content = "Copy", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsEnabled = command.Length > 0 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(command); copy.Content = "Copied"; }
            catch (ExternalException) { status.Text = "Could not copy command. Try again."; status.Visibility = Visibility.Visible; }
        };
        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, IsDefault = true };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(text, 1);
        Grid.SetRow(status, 2);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(label);
        grid.Children.Add(text);
        grid.Children.Add(status);
        grid.Children.Add(buttons);
        dialog.Content = grid;
        dialog.Loaded += (_, _) => text.Focus();
        dialog.ShowDialog();
        ViewCommandButton.Focus();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Result = _tools.Select(Clone).ToList();
        DialogResult = true;
    }

    private void ManageCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingCommand || _closed) return;
        new ToolCredentialManagerWindow(tools: CredentialReferences()) { Owner = this }.ShowDialog();
    }

    // Credential deletion is immediate; canceled drafts must retain their saved profiles.
    private DeveloperTool[] CredentialReferences() => _savedTools.Concat(_tools).ToArray();

    private static DeveloperTool Clone(DeveloperTool tool) => tool.Clone();
}
