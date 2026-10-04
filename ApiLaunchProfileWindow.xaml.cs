using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

internal sealed record ApiLaunchProfileDraft(string Command);

public partial class ApiLaunchProfileWindow : Window
{
    private readonly ServiceProfile _profile;
    private readonly string _directory;
    private readonly Func<ApiLaunchProfileDraft, Task> _save;
    private readonly string _initialCommand;
    private readonly DispatcherTimer _previewTimer;
    private bool _initialized;
    private bool _saving;
    private bool _saved;
    private bool _closed;
    private bool _previewExpanded;
    private bool _previewRunning;
    private int _previewVersion;

    internal ApiLaunchProfileWindow(string serviceName, ServiceProfile profile, string directory,
        Func<ApiLaunchProfileDraft, Task> save)
    {
        _profile = JsonSerializer.Deserialize<ServiceProfile>(JsonSerializer.Serialize(profile))!;
        _directory = directory;
        _save = save;
        _initialCommand = profile.ApiConfiguration?.LaunchCommand ?? ApiLaunchConfiguration.DefaultLaunchCommand;
        InitializeComponent();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await RefreshPreviewAsync(); };
        Title = $"{serviceName} - API launch profile";
        ServiceHeading.Text = serviceName;
        CommandBox.Text = _initialCommand;
        if (profile.ApiConfiguration is null)
        {
            LegacyNotice.Text = "Saving enables encrypted launcher configuration. Existing .NET user-secrets may need import through Manage secrets. The original configured command is retained.";
            LegacyNotice.ToolTip = SensitiveDataProtection.Redact(profile.StartCommand);
            LegacyNotice.Visibility = Visibility.Visible;
        }
        _initialized = true;
        UpdateDraft();
    }

    private bool HasEdits => !_saved && CommandBox.Text.Trim() != _initialCommand;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
    }

    private void CommandChanged(object sender, TextChangedEventArgs e) => UpdateDraft();

    private void UpdateDraft()
    {
        if (!_initialized || _saving) return;
        SaveButton.IsEnabled = true;
        if (_previewExpanded)
        {
            ++_previewVersion;
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private async void PreviewExpanded(object sender, RoutedEventArgs e)
    {
        _previewExpanded = true;
        await RefreshPreviewAsync();
    }

    private void PreviewCollapsed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _previewExpanded = false;
        _previewTimer.Stop();
    }

    private async Task RefreshPreviewAsync()
    {
        if (!_initialized || _closed || _saving || _previewRunning) return;
        _previewRunning = true;
        var version = ++_previewVersion;
        const string environment = "Local";
        var command = CommandBox.Text.Trim();
        PreviewBox.Text = "Reading launch command…";
        EnvironmentText.Text = "";
        try
        {
            var profile = JsonSerializer.Deserialize<ServiceProfile>(JsonSerializer.Serialize(_profile))!;
            profile.ApiConfiguration = new() { Environment = environment, LaunchCommand = command };
            var preview = await Task.Run(() => ApiLaunchConfiguration.GetLaunchCommandPreview(profile, _directory, environment));
            if (_closed || version != _previewVersion) return;
            PreviewBox.Text = preview;
            EnvironmentText.Text = "ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT: Development · encrypted values hidden";
        }
        catch (InvalidOperationException ex)
        {
            if (!_closed && version == _previewVersion) PreviewBox.Text = ex.Message;
        }
        catch (Exception)
        {
            if (!_closed && version == _previewVersion) PreviewBox.Text = "The effective command could not be read. Check the API project folder.";
        }
        finally
        {
            _previewRunning = false;
            if (!_closed && !_saving && _previewExpanded && version != _previewVersion)
            {
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        var command = CommandBox.Text.Trim();
        try
        {
            ApiLaunchConfiguration.ValidateLaunchCommand(command);
            _saving = true;
            _previewTimer.Stop();
            ++_previewVersion;
            SaveButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            CommandBox.IsEnabled = false;
            SetStatus("Saving launch profile…");
            await _save(new(command));
            _saved = true;
            DialogResult = true;
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception) { SetStatus("Launch profile could not be saved. Your draft is retained; check settings access and retry.", error: true); }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                SaveButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                CommandBox.IsEnabled = true;
                if (_previewExpanded) _ = RefreshPreviewAsync();
            }
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 172, 169))
            : (Brush)FindResource("MutedBrush");
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _saving) return;
        e.Handled = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saving && !_saved) { e.Cancel = true; return; }
        if (HasEdits && MessageBox.Show(this, "Discard unsaved launch changes?", "API launch profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _closed = true;
        _previewTimer.Stop();
        ++_previewVersion;
    }
}
