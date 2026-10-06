using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class InstalledDesktopAppsWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ServiceEditorTarget> _installed = [];
    private bool _initialized;
    private bool _loading;
    private bool _closed;
    private bool _discoveryFailed;
    public ServiceEditorTarget? Result { get; private set; }

    public InstalledDesktopAppsWindow()
    {
        InitializeComponent();
        _initialized = true;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Loaded += async (_, _) => { SearchInput.Focus(); await LoadAppsAsync(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (!_loading) _lifetime.Dispose();
        };
    }

    private async Task LoadAppsAsync()
    {
        if (_loading || _closed) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        UseSelectedButton.IsEnabled = false;
        AppsList.IsEnabled = false;
        DiscoveryProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Checking installed apps…";
        var token = _lifetime.Token;
        try
        {
            var installed = await Task.Run(() => DeveloperToolLauncher.FindInstalledDesktopTools(token), token);
            if (_closed) return;
            _installed = installed.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            _discoveryFailed = false;
            ApplySearch();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_closed) return;
            _installed = [];
            _discoveryFailed = true;
            AppsList.ItemsSource = _installed;
            StatusText.Text = "Installed apps could not be read. Refresh or choose an executable.";
        }
        finally
        {
            _loading = false;
            if (_closed) _lifetime.Dispose();
            else
            {
                RefreshButton.IsEnabled = true;
                AppsList.IsEnabled = true;
                DiscoveryProgress.Visibility = Visibility.Collapsed;
                UpdateSelection();
            }
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_loading && !_closed && !_discoveryFailed) ApplySearch();
    }

    private void ApplySearch()
    {
        var selected = AppsList.SelectedItem as ServiceEditorTarget;
        var words = SearchInput.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = _installed.Where(app => words.All(word =>
            app.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
            app.FileName.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        AppsList.ItemsSource = matches;
        AppsList.SelectedItem = selected is not null ? matches.FirstOrDefault(app => app.FileName.Equals(selected.FileName, StringComparison.OrdinalIgnoreCase)) : null;
        StatusText.Text = _installed.Count == 0 ? "No installed desktop apps found." : matches.Length == 0
            ? "No matching apps." : words.Length == 0 ? $"{matches.Length} apps" : $"{matches.Length} of {_installed.Count} apps";
        UpdateSelection();
    }

    private void Apps_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (_initialized) UseSelectedButton.IsEnabled = !_loading && AppsList.SelectedItem is ServiceEditorTarget;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _closed || AppsList.SelectedItem is not ServiceEditorTarget app) return;
        Result = app;
        DialogResult = true;
    }

    private void Apps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !_loading && !_closed && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(AppsList, source) is ListBoxItem { IsSelected: true })
        {
            UseSelected_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a desktop app", Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false
        };
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            Result = new(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
            DialogResult = true;
        }
        catch (Exception) { StatusText.Text = "Application picker could not open. Try again."; }
    }
}
