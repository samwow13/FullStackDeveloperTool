using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ChangeDatabaseWindow : Window
{
    private readonly ServiceProfile _profile;
    private readonly string _directory;
    private readonly Func<ApiDatabaseConfiguration, string, string, Task<bool>> _saveAndRestart;
    private readonly Action<ApiDatabaseConfiguration>? _configurationDiscovered;
    private ApiDatabaseConfiguration? _snapshot;
    private ApiDatabaseConnectionSetting? _selectedConnection;
    private string _baselineValue = "";
    private string _baselineDatabaseName = "";
    private bool _ready;
    private bool _busy;
    private bool _suppressEditor;
    private bool _suppressSelection;
    private bool _databaseNameNeedsApply;
    private bool _completed;
    private bool _retryRequired;
    private bool _configurationSaved;

    public ChangeDatabaseWindow(ServiceProfile profile, string directory,
        Func<ApiDatabaseConfiguration, string, string, Task<bool>> saveAndRestart,
        Action<ApiDatabaseConfiguration>? configurationDiscovered = null)
    {
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

    private string EditorValue => RevealCheckBox.IsChecked == true
        ? RevealedConnectionBox.Text : ConnectionPasswordBox.Password;

    private bool HasChanges => _selectedConnection is not null &&
        (!string.Equals(EditorValue, _baselineValue, StringComparison.Ordinal) ||
         !string.Equals(DatabaseNameBox.Text, _baselineDatabaseName, StringComparison.Ordinal));

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadConfigurationAsync();

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ConfirmDiscard("reload Local configuration")) return;
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        SetBusy(true);
        SetStatus("Finding the API's Local database configuration…");
        try
        {
            var loaded = await Task.Run(() => ApiDatabaseConfiguration.Load(
                _profile, _directory, "Local", importLegacyForReview: true));
            _snapshot = loaded;
            _configurationDiscovered?.Invoke(loaded);
            SourceFolderText.Text = $"Folder: {loaded.WorkingDirectory}";
            SourceProjectText.Text = $"Project: {loaded.ProjectFilePath}";
            LegacyDisclosureText.Visibility = loaded.HasStagedLegacyValues ? Visibility.Visible : Visibility.Collapsed;
            _suppressSelection = true;
            // Only key names become picker items or accessibility content.
            ConnectionKeyBox.ItemsSource = loaded.Connections.Select(connection => connection.Key).ToArray();
            var selected = loaded.Connections.FirstOrDefault(connection =>
                connection.Key.Equals("DefaultConnectionString", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault(connection =>
                    connection.Key.Equals("ConnectionStrings:DefaultConnectionString", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault(connection =>
                    connection.Key.Equals("ConnectionStrings:DefaultConnection", StringComparison.OrdinalIgnoreCase))
                ?? loaded.Connections.FirstOrDefault();
            ConnectionKeyBox.SelectedItem = selected?.Key;
            _suppressSelection = false;
            SetConnection(selected);
            _retryRequired = false;
            SetStatus(selected is null
                ? "No database connection string was found. Check this API's Local configuration."
                : loaded.HasStagedLegacyValues
                    ? "Local values loaded for review. Save creates an encrypted launcher copy and restarts the API."
                    : "Local connection loaded. Change the database name or connection string, then save and restart.");
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
            _suppressSelection = false;
            SetBusy(false);
        }
        if (_selectedConnection is not null)
        {
            if (DatabaseNameBox.IsEnabled) DatabaseNameBox.Focus();
            else ConnectionPasswordBox.Focus();
        }
        else CancelButton.Focus();
    }

    private void ConnectionKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSelection || _busy) return;
        var selectedKey = ConnectionKeyBox.SelectedItem as string;
        var selected = _snapshot?.Connections.FirstOrDefault(connection =>
            string.Equals(connection.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(selected, _selectedConnection)) return;
        if (!ConfirmDiscard("switch connection keys"))
        {
            _suppressSelection = true;
            ConnectionKeyBox.SelectedItem = _selectedConnection?.Key;
            _suppressSelection = false;
            return;
        }
        SetConnection(selected);
        SetStatus("Connection loaded. Changes remain in this window until you save and restart.");
    }

    private void SetConnection(ApiDatabaseConnectionSetting? connection)
    {
        _selectedConnection = connection;
        _baselineValue = connection?.Value ?? "";
        _baselineDatabaseName = connection?.DatabaseDisplayName ?? "";
        _suppressEditor = true;
        RevealCheckBox.IsChecked = false;
        RevealedConnectionBox.Clear();
        RevealedConnectionBox.Visibility = Visibility.Collapsed;
        ConnectionPasswordBox.Visibility = Visibility.Visible;
        ConnectionValueLabel.Target = ConnectionPasswordBox;
        ConnectionPasswordBox.Password = _baselineValue;
        DatabaseNameBox.Text = _baselineDatabaseName;
        _databaseNameNeedsApply = false;
        _suppressEditor = false;
        ConnectionSourceText.Text = connection is not null
            ? $"Source: {connection.Source} · Local profile" : "No connection string available.";
        UpdateControls();
    }

    private void Connection_PasswordChanged(object sender, RoutedEventArgs e) => ConnectionValueChanged();
    private void Connection_TextChanged(object sender, TextChangedEventArgs e) => ConnectionValueChanged();

    private void ConnectionValueChanged()
    {
        if (!_ready || _suppressEditor) return;
        _suppressEditor = true;
        DatabaseNameBox.Text = ApiDatabaseConfiguration.ReadDatabaseDisplayName(EditorValue) ?? "";
        _databaseNameNeedsApply = false;
        _suppressEditor = false;
        UpdateControls();
    }

    private void DatabaseName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppressEditor || _selectedConnection is null) return;
        _databaseNameNeedsApply = true;
        try
        {
            var value = ApiDatabaseConfiguration.WithDatabaseName(EditorValue, DatabaseNameBox.Text);
            ReplaceEditorValue(value);
            _databaseNameNeedsApply = false;
        }
        catch (Exception)
        {
            // Keep incomplete name drafts intact; report validation only on Save.
        }
        UpdateControls();
    }

    private void ReplaceEditorValue(string value)
    {
        _suppressEditor = true;
        if (RevealCheckBox.IsChecked == true) RevealedConnectionBox.Text = value;
        else ConnectionPasswordBox.Password = value;
        _suppressEditor = false;
    }

    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressEditor) return;
        _suppressEditor = true;
        if (RevealCheckBox.IsChecked == true)
        {
            RevealedConnectionBox.Text = ConnectionPasswordBox.Password;
            ConnectionPasswordBox.Clear();
            ConnectionPasswordBox.Visibility = Visibility.Collapsed;
            RevealedConnectionBox.Visibility = Visibility.Visible;
            ConnectionValueLabel.Target = RevealedConnectionBox;
            RevealedConnectionBox.Focus();
        }
        else
        {
            ConnectionPasswordBox.Password = RevealedConnectionBox.Text;
            RevealedConnectionBox.Clear();
            RevealedConnectionBox.Visibility = Visibility.Collapsed;
            ConnectionPasswordBox.Visibility = Visibility.Visible;
            ConnectionValueLabel.Target = ConnectionPasswordBox;
            ConnectionPasswordBox.Focus();
        }
        _suppressEditor = false;
        UpdateControls();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot is null || _selectedConnection is null || (!HasChanges && !_retryRequired)) return;
        var value = EditorValue;
        if (_databaseNameNeedsApply)
        {
            try
            {
                value = ApiDatabaseConfiguration.WithDatabaseName(value, DatabaseNameBox.Text);
                ReplaceEditorValue(value);
                _databaseNameNeedsApply = false;
            }
            catch (Exception)
            {
                SetStatus("Enter a valid database name for this connection string. Your draft was kept.", true);
                DatabaseNameBox.Focus();
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus("Enter a database connection string. Your draft was kept.", true);
            if (RevealCheckBox.IsChecked == true) RevealedConnectionBox.Focus();
            else ConnectionPasswordBox.Focus();
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
        _baselineDatabaseName = DatabaseNameBox.Text;
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
        var editable = !_busy && _snapshot is not null && _selectedConnection is not null;
        EditorPanel.IsEnabled = editable;
        var canEditDatabaseName = _databaseNameNeedsApply || ApiDatabaseConfiguration.ReadDatabaseName(EditorValue) is not null;
        DatabaseNameBox.IsEnabled = editable && canEditDatabaseName;
        DatabaseNameHint.Text = canEditDatabaseName
            ? "Changing this name keeps the host, credentials and other connection options."
            : ApiDatabaseConfiguration.ReadDatabaseDisplayName(EditorValue) is not null
                ? "SQLite database root detected. File names cannot be changed with this database-name editor."
                : "A safe database name could not be read. Update the connection string.";
        CancelButton.IsEnabled = !_busy;
        ReloadButton.IsEnabled = !_busy;
        SaveButton.IsEnabled = editable && (HasChanges || _retryRequired);
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
        // Let the connection picker close its open list before Escape closes the modal.
        if (ConnectionKeyBox.IsDropDownOpen) return;
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
        if (!_completed && !ConfirmDiscard("close the window"))
        {
            e.Cancel = true;
            return;
        }
        _suppressEditor = true;
        ConnectionPasswordBox.Clear();
        RevealedConnectionBox.Clear();
        DatabaseNameBox.Clear();
        _baselineValue = "";
        _baselineDatabaseName = "";
        ConnectionKeyBox.ItemsSource = null;
        _selectedConnection = null;
        _snapshot = null;
    }
}
