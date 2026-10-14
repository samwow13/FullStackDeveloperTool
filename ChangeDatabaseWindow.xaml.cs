using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ChangeDatabaseWindow : Window, IDashboardReplacementWindow
{
    public bool IsDashboardReplacementReady => !_busy;
    public bool IsDashboardReplacementClosePending => false;

    private readonly ServiceProfile _profile;
    private readonly string _directory;
    private readonly Func<ApiDatabaseConfiguration, string, string, Task<bool>> _saveAndRestart;
    private readonly Action<ApiDatabaseConfiguration>? _configurationDiscovered;
    private ApiDatabaseConfiguration? _snapshot;
    private ApiDatabaseConnectionSetting? _selectedConnection;
    private string _baselineValue = "";
    private bool _ready;
    private bool _busy;
    private bool _suppressEditor;
    private bool _completed;
    private bool _retryRequired;
    private bool _configurationSaved;
    private Action? _clearDashboardResume;
    private ChangeDatabaseEditorResume? _restoredDatabaseResume;
    private bool _dashboardResumePending;
    private bool _dashboardResumeClose;
    private bool _resumeReviewRequired;
    private bool _hasRestoredDatabaseDraft;
    private string? _reviewedSnapshotFingerprint;
    private string? _restoredConnectionKey;

    internal void ConfigureDashboardResume(ChangeDatabaseEditorResume? state, Action clearDashboardResume)
    {
        _clearDashboardResume = clearDashboardResume;
        _restoredDatabaseResume = state;
        _dashboardResumePending = state is not null;
    }

    internal ChangeDatabaseEditorResume CaptureDashboardResume() => new(
        _selectedConnection?.Key ?? _restoredConnectionKey, EditorValue, _baselineValue,
        _resumeReviewRequired ? _reviewedSnapshotFingerprint : _snapshot?.GetDashboardResumeFingerprint() ?? _reviewedSnapshotFingerprint,
        _retryRequired, _configurationSaved, _resumeReviewRequired);

    internal void MarkDashboardResumeCheckpointed()
    {
        _dashboardResumePending = true;
        _dashboardResumeClose = true;
    }

    private void ClearDashboardResume()
    {
        if (!_dashboardResumePending) return;
        _clearDashboardResume?.Invoke();
        _dashboardResumePending = false;
    }

    private void ApplyRestoredDatabaseResume()
    {
        if (_restoredDatabaseResume is not { } state) return;
        _restoredDatabaseResume = null;
        _hasRestoredDatabaseDraft = true;
        _restoredConnectionKey = state.ConnectionKey;
        var selected = state.ConnectionKey is null ? null : _snapshot?.Connections.FirstOrDefault(connection =>
            connection.Key.Equals(state.ConnectionKey, StringComparison.OrdinalIgnoreCase));
        _selectedConnection = selected;
        _baselineValue = state.BaselineValue;
        _reviewedSnapshotFingerprint = state.SnapshotFingerprint;
        _retryRequired = state.RetryRequired;
        _configurationSaved = state.ConfigurationSaved;
        _resumeReviewRequired = state.ReviewRequired || _snapshot is null || selected is null ||
            state.SnapshotFingerprint is null || _snapshot.GetDashboardResumeFingerprint() != state.SnapshotFingerprint;
        _suppressEditor = true;
        ConnectionStringBox.Text = state.Value;
        _suppressEditor = false;
        ConnectionSourceText.Text = selected is null ? "" : $"Setting: {selected.Key} · Source: {selected.Source} · Local profile";
        if (_resumeReviewRequired)
        {
            var failure = _snapshot is null ? StatusText.Text + " " : "";
            SetStatus(failure + "Saved connection draft restored, but Local configuration changed or could not be rechecked. Saving is blocked. Copy the draft, cancel, then reopen Change DB to review current configuration before reapplying it.", true);
        }
        else SetStatus(_retryRequired
            ? "Saved connection draft restored. Configuration was saved earlier; choose Save & restart explicitly to retry its unverified restart."
            : "Connection draft restored. Review, then choose Save & restart when ready.");
    }

    public ChangeDatabaseWindow(ServiceProfile profile, string directory,
        Func<ApiDatabaseConfiguration, string, string, Task<bool>> saveAndRestart,
        Action<ApiDatabaseConfiguration>? configurationDiscovered = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        InitializeComponent();
        _profile = profile;
        _directory = directory;
        _saveAndRestart = saveAndRestart;
        _configurationDiscovered = configurationDiscovered;
        ServiceNameText.Text = profile.Name;
        SourceFolderText.Text = $"Folder: {directory}";
        SourceProjectText.Text = "Project: discovering…";
        _ready = true;
        UpdateControls();
    }

    private string EditorValue => ConnectionStringBox.Text;

    private bool HasChanges => (_selectedConnection is not null || _hasRestoredDatabaseDraft) &&
        !string.Equals(EditorValue, _baselineValue, StringComparison.Ordinal);

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadConfigurationAsync();

    private async Task LoadConfigurationAsync()
    {
        SetBusy(true);
        SetStatus("Finding the API's Local database configuration…");
        try
        {
            var loaded = await Task.Run(() => ApiDatabaseConfiguration.Load(
                _profile, _directory, "Local", importLegacyForReview: true));
            _snapshot = loaded;
            _reviewedSnapshotFingerprint = loaded.GetDashboardResumeFingerprint();
            _configurationDiscovered?.Invoke(loaded);
            SourceFolderText.Text = $"Folder: {loaded.WorkingDirectory}";
            SourceProjectText.Text = $"Project: {loaded.ProjectFilePath}";
            LegacyDisclosureText.Visibility = loaded.HasStagedLegacyValues ? Visibility.Visible : Visibility.Collapsed;
            var selected = loaded.Connections.FirstOrDefault(connection =>
                connection.Key.Equals("DefaultConnectionString", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault(connection =>
                    connection.Key.Equals("ConnectionStrings:DefaultConnectionString", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault(connection =>
                    connection.Key.Equals("ConnectionStrings:DefaultConnection", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault();
            SetConnection(selected);
            _retryRequired = false;
            SetStatus(selected is null
                ? "No database connection string was found. Check this API's Local configuration."
                : loaded.HasStagedLegacyValues
                    ? "Local values loaded for review. Save creates an encrypted launcher copy and restarts the API."
                    : "Local connection loaded. Edit the connection string, then save and restart.");
        }
        catch (ApiSecretStoreException ex)
        {
            SetStatus(ex.Message + (_snapshot is null ? " No changes were saved." : " Your draft was kept."), true);
        }
        catch (InvalidOperationException ex)
        {
            // The configuration helper emits fixed, value-free recovery messages.
            SetStatus(ex.Message + (_snapshot is null ? " No changes were saved." : " Your draft was kept."), true);
        }
        catch (Exception)
        {
            SetStatus("Local database configuration could not be loaded. Check the API project and configuration access." +
                (_snapshot is null ? " No changes were saved." : " Your draft was kept."), true);
        }
        finally
        {
            ApplyRestoredDatabaseResume();
            SetBusy(false);
        }
        if (_selectedConnection is not null) ConnectionStringBox.Focus();
        else CancelButton.Focus();
    }

    private void SetConnection(ApiDatabaseConnectionSetting? connection)
    {
        _selectedConnection = connection;
        _baselineValue = connection?.Value ?? "";
        _suppressEditor = true;
        ConnectionStringBox.Text = _baselineValue;
        _suppressEditor = false;
        ConnectionSourceText.Text = connection is not null
            ? $"Setting: {connection.Key} · Source: {connection.Source} · Local profile" : "";
        UpdateControls();
    }

    private void Connection_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressEditor) return;
        UpdateControls();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _resumeReviewRequired || _completed || _snapshot is null || _selectedConnection is null || (!HasChanges && !_retryRequired)) return;
        var value = EditorValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus("Enter a database connection string. Your draft was kept.", true);
            ConnectionStringBox.Focus();
            return;
        }

        SetBusy(true);
        SetStatus("Saving Local database configuration and restarting the API…");
        try
        {
            _completed = await _saveAndRestart(_snapshot, _selectedConnection.Key, value);
            _retryRequired = !_completed;
        }
        catch (ApiSecretStoreException ex)
        {
            SetStatus(ex.Message + " Your draft was kept.", true);
        }
        catch (Exception)
        {
            SetStatus("Database change could not be completed. Check the API status and configuration. Your draft was kept.", true);
        }
        finally { SetBusy(false); }

        if (_completed) DialogResult = true;
    }

    public void MarkConfigurationSaved()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(MarkConfigurationSaved);
            return;
        }
        _configurationSaved = true;
        _retryRequired = true;
        // Saved values become the draft baseline; retry availability is tracked separately.
        _baselineValue = EditorValue;
        _reviewedSnapshotFingerprint = _snapshot?.GetDashboardResumeFingerprint();
        UpdateControls();
    }

    public void SetStatus(string message, bool error = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(message, error));
            return;
        }
        StatusText.Text = message;
        StatusText.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(255, 173, 179)) : (Brush)FindResource("MutedBrush");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!_ready) return;
        var editable = !_busy && (_hasRestoredDatabaseDraft || _snapshot is not null && _selectedConnection is not null);
        EditorPanel.IsEnabled = editable;
        CancelButton.IsEnabled = !_busy;
        SaveButton.IsEnabled = editable && !_resumeReviewRequired && !_completed && _snapshot is not null &&
            _selectedConnection is not null && (HasChanges || _retryRequired);
    }

    private bool ConfirmDiscard(string action) => !HasChanges || MessageBox.Show(this,
        _configurationSaved
            ? $"Discard the additional unsaved database connection draft and {action}? Saved Local configuration remains in place; this does not roll back the saved change."
            : $"Discard the unsaved database connection draft and {action}?", "Unsaved database change",
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (!_busy) Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        if (!_dashboardResumeClose && !_completed && !ConfirmDiscard("close the window"))
        {
            e.Cancel = true;
            return;
        }
        if (!_dashboardResumeClose)
        {
            try { ClearDashboardResume(); }
            catch (Exception)
            {
                e.Cancel = true;
                SetStatus("The old database draft checkpoint could not be cleared. The editor remains open; retry closing after checking local storage access.", true);
                return;
            }
        }
        _suppressEditor = true;
        ConnectionStringBox.Clear();
        _baselineValue = "";
        _selectedConnection = null;
        _snapshot = null;
    }
}
