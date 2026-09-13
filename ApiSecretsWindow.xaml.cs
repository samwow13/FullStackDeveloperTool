using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ApiSecretsWindow : Window
{
    private readonly ApiSecretStore _store;
    private ApiSecretSnapshot? _snapshot;
    private Dictionary<string, string> _working = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitTextKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ApiAppSetting> _appSettings = new Dictionary<string, ApiAppSetting>();
    private bool _production;
    private bool _ready;
    private bool _busy;
    private bool _suppressEditor;
    private bool _suppressSelection;
    private string? _selectedKey;
    private string _editorBaselineKey = "";
    private string _editorBaselineValue = "";

    public bool SavedChanges { get; private set; }
    public bool SavedLocalChanges { get; private set; }
    public bool SavedProdChanges { get; private set; }

    public ApiSecretsWindow(string workingDirectory, string serviceName, bool production)
    {
        InitializeComponent();
        _store = new ApiSecretStore(workingDirectory);
        _production = production;
        ServiceNameText.Text = serviceName;
        _ready = true;
        UpdateProfileAppearance();
        UpdateControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadStoreAsync(_production);

    private string EditorValue => RevealCheckBox.IsChecked == true ? RevealedValueBox.Text : ValuePasswordBox.Password;
    private bool HasEditorChanges => !string.Equals(KeyBox.Text, _editorBaselineKey, StringComparison.Ordinal)
        || !string.Equals(EditorValue, _editorBaselineValue, StringComparison.Ordinal);
    private bool IsOriginalNonText(string key) => _snapshot is not null &&
        _snapshot.OriginalLeaves.TryGetValue(key, out var original) && original.ValueKind != JsonValueKind.String;
    private bool HasStagedTypeChange(string key) => _explicitTextKeys.Contains(key) && IsOriginalNonText(key);

    private int PendingChangeCount
    {
        get
        {
            if (_snapshot == null) return 0;
            return _snapshot.Values.Keys.Union(_working.Keys, StringComparer.OrdinalIgnoreCase).Count(key =>
                !_snapshot.Values.TryGetValue(key, out var saved) || !_working.TryGetValue(key, out var staged)
                || !string.Equals(saved, staged, StringComparison.Ordinal) || HasStagedTypeChange(key));
        }
    }

    private async Task LoadStoreAsync(bool production)
    {
        SetBusy(true);
        SetStatus("Loading configuration keys…");
        try
        {
            // Nothing displayed or staged is replaced until the new store has loaded successfully.
            var loaded = await Task.Run(() =>
            {
                var snapshot = _store.Load(production);
                IReadOnlyDictionary<string, ApiAppSetting> settings = new Dictionary<string, ApiAppSetting>();
                var settingsAvailable = true;
                try { settings = _store.ReadAppSettings(production); }
                catch (Exception) { settingsAvailable = false; }
                return (snapshot, settings, settingsAvailable);
            });
            _snapshot = loaded.snapshot;
            _working = new Dictionary<string, string>(loaded.snapshot.Values, StringComparer.OrdinalIgnoreCase);
            _explicitTextKeys.Clear();
            _appSettings = loaded.settings;
            _production = production;
            _selectedKey = null;
            SetEditor("", "");
            RefreshRows();
            UpdateProfileAppearance();
            AppSettingsHint.Text = loaded.settingsAvailable
                ? "The key list includes names from appsettings files. Their values are not displayed or changed here. The dashboard applies saved overrides above those files when starting the API."
                : "Appsettings key names could not be read. Saved secrets are still available; appsettings files are never changed here.";
            SetStatus(_snapshot.HasProtectedStore
                ? $"Encrypted {(_production ? "Prod" : "Local")} configuration loaded. {_snapshot.Values.Count} saved override(s)."
                : _snapshot.HasLegacyStore
                    ? "Legacy plaintext configuration may exist. Import legacy values, review the masked draft, then save an encrypted launcher copy."
                    : "No encrypted configuration saved yet. Enter values or import an existing legacy file, then save.");
        }
        catch (ApiSecretStoreException ex)
        {
            SetStatus(ex.Message + " Existing drafts were kept.", true);
        }
        catch (Exception)
        {
            SetStatus("The secrets store could not be loaded. Check the API project, user-secrets configuration, file access and JSON format. Existing drafts were kept.", true);
        }
        finally { SetBusy(false); }
    }

    private async void Local_Click(object sender, RoutedEventArgs e)
    {
        if (!_production || _busy || !ConfirmDiscardAll("switch to the Local store")) return;
        await LoadStoreAsync(false);
    }

    private async void Prod_Click(object sender, RoutedEventArgs e)
    {
        if (_production || _busy || !ConfirmDiscardAll("switch to the Prod store")) return;
        await LoadStoreAsync(true);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ConfirmDiscardAll("reload this store")) return;
        await LoadStoreAsync(_production);
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot is null || !ConfirmDiscardAll("replace the draft with this profile's legacy .NET values")) return;
        var expected = _snapshot;
        SetBusy(true);
        SetStatus("Reading legacy values into a masked draft…");
        try
        {
            var imported = await Task.Run(() => _store.ReadLegacyForImport(_production, expected));
            _snapshot = imported;
            _working = new Dictionary<string, string>(imported.Values, StringComparer.OrdinalIgnoreCase);
            _explicitTextKeys.Clear();
            _selectedKey = null;
            SetEditor("", "");
            RefreshRows();
            UpdateProfileAppearance();
            SetStatus("Legacy import staged as a replacement for this profile. Review its keys, then save to encrypt it. The original plaintext file remains unchanged for direct dotnet use.");
        }
        catch (ApiSecretStoreException ex) { SetStatus(ex.Message + " Existing drafts were kept.", true); }
        catch (Exception) { SetStatus("Legacy values could not be imported. Both stores and the current draft were preserved.", true); }
        finally { SetBusy(false); }
    }

    private void Keys_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSelection || _busy || KeysList.SelectedItem is not SecretRow selected) return;
        if (!ConfirmDiscardEditor())
        {
            _suppressSelection = true;
            KeysList.SelectedItem = KeysList.Items.Cast<SecretRow>().FirstOrDefault(row => string.Equals(row.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
            _suppressSelection = false;
            return;
        }
        _selectedKey = selected.Key;
        SetEditor(selected.Key, _working.TryGetValue(selected.Key, out var value) ? value : "");
        UpdateControls();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ConfirmDiscardEditor()) return;
        _selectedKey = null;
        _suppressSelection = true;
        KeysList.SelectedItem = null;
        _suppressSelection = false;
        SetEditor("", "");
        UpdateControls();
        KeyBox.Focus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot == null) return;
        var key = KeyBox.Text.Trim();
        try { ApiSecretStore.ValidateKey(key); }
        catch (ApiSecretStoreException ex)
        {
            SetStatus(ex.Message, true);
            KeyBox.Focus();
            return;
        }
        catch (Exception)
        {
            SetStatus("Use a key with nonempty colon-separated segments, such as ConnectionStrings:DefaultConnection. Keys cannot contain double underscores, an equals sign or a null character. Environment and listening URL settings are controlled by the dashboard.", true);
            KeyBox.Focus();
            return;
        }

        // Adding a differently named key never silently renames or removes the selected key.
        _working[key] = EditorValue;
        _explicitTextKeys.Add(key);
        _selectedKey = key;
        SetEditor(key, _working[key]);
        RefreshRows();
        UpdateControls();
        SetStatus("Value staged. Save changes to write this store.");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedKey == null || !_working.ContainsKey(_selectedKey)) return;
        if (HasEditorChanges && MessageBox.Show(this,
                "Remove the selected key's override and discard its unstaged editor changes? The removal is written only when you save.",
                "Remove override", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _working.Remove(_selectedKey);
        _explicitTextKeys.Remove(_selectedKey);
        SetEditor(_selectedKey, "");
        RefreshRows();
        UpdateControls();
        SetStatus("Override removal staged. Other configuration sources may still supply this key.");
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !ConfirmDiscardEditor()) return;
        SetEditor(_editorBaselineKey, _editorBaselineValue);
        UpdateControls();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot == null || PendingChangeCount == 0 && !_snapshot.IsLegacyImport && _snapshot.HasProtectedStore) return;
        if (HasEditorChanges)
        {
            SetStatus("Stage the value or undo editor changes before saving. Your editor draft is still here.", true);
            return;
        }

        var expected = _snapshot;
        var values = new Dictionary<string, string>(_working, StringComparer.OrdinalIgnoreCase);
        var explicitTextKeys = new HashSet<string>(_explicitTextKeys, StringComparer.OrdinalIgnoreCase);
        SetBusy(true);
        SetStatus("Saving configuration secrets…");
        try
        {
            var saved = await Task.Run(() => _store.Save(_production, expected, values, explicitTextKeys));
            _snapshot = saved;
            _working = new Dictionary<string, string>(saved.Values, StringComparer.OrdinalIgnoreCase);
            _explicitTextKeys.Clear();
            SavedChanges = true;
            if (_production) SavedProdChanges = true;
            else SavedLocalChanges = true;
            SetEditor(_selectedKey ?? "", _selectedKey != null && _working.TryGetValue(_selectedKey, out var value) ? value : "");
            RefreshRows();
            UpdateProfileAppearance();
            SetStatus($"Encrypted {(_production ? "Prod" : "Local")} secrets saved. Activate or restart that profile on the dashboard to use them. Any original .NET plaintext file was left unchanged.");
        }
        catch (ApiSecretStoreException ex) when (ex.IsConflict)
        {
            SetStatus(ex.Message + " Your drafts were kept.", true);
        }
        catch (ApiSecretStoreException ex)
        {
            SetStatus(ex.Message + " Your drafts were kept.", true);
        }
        catch (Exception)
        {
            SetStatus("Secrets could not be saved. Check the API project, user-secrets configuration and file access. Your drafts were kept.", true);
        }
        finally { SetBusy(false); }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshRows();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_suppressEditor) UpdateControls();
    }

    private void Value_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && !_suppressEditor) UpdateControls();
    }

    private void Revealed_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_suppressEditor) UpdateControls();
    }

    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressEditor) return;
        _suppressEditor = true;
        if (RevealCheckBox.IsChecked == true)
        {
            RevealedValueBox.Text = ValuePasswordBox.Password;
            ValuePasswordBox.Clear();
            ValuePasswordBox.Visibility = Visibility.Collapsed;
            RevealedValueBox.Visibility = Visibility.Visible;
            ValueLabel.Target = RevealedValueBox;
        }
        else
        {
            ValuePasswordBox.Password = RevealedValueBox.Text;
            RevealedValueBox.Clear();
            RevealedValueBox.Visibility = Visibility.Collapsed;
            ValuePasswordBox.Visibility = Visibility.Visible;
            ValueLabel.Target = ValuePasswordBox;
        }
        _suppressEditor = false;
        UpdateControls();
    }

    private void SetEditor(string key, string value)
    {
        _suppressEditor = true;
        RevealCheckBox.IsChecked = false;
        RevealedValueBox.Clear();
        RevealedValueBox.Visibility = Visibility.Collapsed;
        ValuePasswordBox.Visibility = Visibility.Visible;
        ValueLabel.Target = ValuePasswordBox;
        KeyBox.Text = key;
        ValuePasswordBox.Password = value;
        _editorBaselineKey = key;
        _editorBaselineValue = value;
        _suppressEditor = false;
    }

    private void RefreshRows()
    {
        if (!_ready) return;
        var keys = _working.Keys.Concat(_snapshot?.Values.Keys ?? [])
            .Concat(_appSettings.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var filter = SearchBox.Text.Trim();
        var rows = keys.Select(CreateRow)
            .Where(row => filter.Length == 0 || row.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Source.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _suppressSelection = true;
        KeysList.ItemsSource = rows;
        KeysList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
        _suppressSelection = false;
        EmptyKeysText.Text = filter.Length == 0
            ? "No keys yet. Add a configuration key in the editor."
            : "No configuration keys match this filter.";
        EmptyKeysText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SecretRow CreateRow(string key)
    {
        var saved = _snapshot != null && _snapshot.Values.ContainsKey(key);
        var staged = _working.ContainsKey(key);
        var changed = staged && (!saved || !string.Equals(_snapshot!.Values[key], _working[key], StringComparison.Ordinal) || HasStagedTypeChange(key));
        var state = staged && _snapshot?.IsLegacyImport == true
            ? "••••••••  Imported · unsaved"
            : staged
            ? changed ? saved ? "••••••••  Changed · unsaved" : "••••••••  Added · unsaved" : "••••••••  Saved override"
            : saved ? "Removed · unsaved" : "No secret override";
        if (staged && !changed && _snapshot!.NullKeys.Contains(key))
            state = "JSON null/container override · replace with text or remove before launching";
        var source = _appSettings.TryGetValue(key, out var setting)
            ? setting.Source
            : "Encrypted launcher override";
        return new SecretRow(key, state, source,
            (Brush)FindResource(changed || saved && !staged ? "AccentBrush" : "MutedBrush"));
    }

    private void UpdateProfileAppearance()
    {
        ProfilePanel.Background = _production ? new SolidColorBrush(Color.FromRgb(61, 29, 39)) : (Brush)FindResource("CardBrush");
        ProfilePanel.BorderBrush = _production ? new SolidColorBrush(Color.FromRgb(186, 79, 95)) : (Brush)FindResource("BorderBrush");
        ProfileHeadingText.Foreground = _production ? new SolidColorBrush(Color.FromRgb(255, 173, 179)) : (Brush)FindResource("TextBrush");
        ProfileHeadingText.Text = _production ? "PROD CONFIGURATION — editing production values" : "Local configuration";
        ProfileHelpText.Text = _production
            ? "These values configure the local API when Prod is activated. They may connect it to production data and services."
            : "Edit encrypted overrides for the API's managed Development launches.";
        StoreReferenceText.Text = _snapshot is null ? "Windows-user encrypted configuration"
            : $"Profile ID: {_snapshot.StoreId} · {(_snapshot.HasProtectedStore ? "encrypted store exists" : "not saved yet")}";
        Title = _production ? "PROD — API configuration secrets" : "Local — API configuration secrets";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!_ready) return;
        var loaded = _snapshot != null;
        var editable = loaded && !_busy;
        EditorPanel.IsEnabled = editable;
        KeysList.IsEnabled = editable;
        SearchBox.IsEnabled = !_busy;
        LocalButton.IsEnabled = !_busy && _production;
        ProdButton.IsEnabled = !_busy && !_production;
        ReloadButton.IsEnabled = !_busy;
        ImportLegacyButton.IsEnabled = editable;
        NewButton.IsEnabled = editable;
        CloseButton.IsEnabled = !_busy;
        ApplyButton.IsEnabled = editable && !string.IsNullOrWhiteSpace(KeyBox.Text)
            && (HasEditorChanges || !_working.ContainsKey(KeyBox.Text.Trim()) ||
                IsOriginalNonText(KeyBox.Text.Trim()) && !_explicitTextKeys.Contains(KeyBox.Text.Trim()));
        RemoveButton.IsEnabled = editable && _selectedKey != null && _working.ContainsKey(_selectedKey);
        CancelEditButton.IsEnabled = editable && HasEditorChanges;
        var pending = PendingChangeCount;
        SaveButton.IsEnabled = editable && (pending > 0 || _snapshot?.IsLegacyImport == true || _snapshot?.HasProtectedStore != true) && !HasEditorChanges;
        SaveButton.Content = _production ? "_Save Prod changes" : "_Save Local changes";
        PendingText.Text = !loaded ? "No store loaded."
            : (_snapshot!.IsLegacyImport ? $"Legacy import staged: {_working.Count} configuration entries."
                : !_snapshot.HasProtectedStore ? $"{pending} staged change(s). Save creates the encrypted profile."
                : $"{pending} staged change(s).") + (HasEditorChanges ? " Editor has an unstaged draft." : "");
        DraftText.Text = HasEditorChanges
            ? "Unstaged editor changes. Choose Stage value before saving."
            : IsOriginalNonText(KeyBox.Text.Trim()) && !_explicitTextKeys.Contains(KeyBox.Text.Trim())
                ? "This value has a JSON type. Choose Stage value to save the editor as text, including an empty value."
            : _snapshot?.IsLegacyImport == true ? "Imported values are staged only. Saving replaces this profile's encrypted overrides."
            : pending > 0 ? "Staged changes are kept in this window until you save." : "Choose a key, or enter a new key and value, then stage it.";
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 173, 179)) : (Brush)FindResource("MutedBrush");
    }

    private bool ConfirmDiscardEditor() => !HasEditorChanges || MessageBox.Show(this,
        "Discard the unstaged changes in the key/value editor? Your other staged changes will stay in this window.",
        "Unstaged editor changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool ConfirmDiscardAll(string action) => PendingChangeCount == 0 && !HasEditorChanges && _snapshot?.IsLegacyImport != true || MessageBox.Show(this,
        $"Discard all staged changes and the editor draft, and {action}? Nothing from these drafts will be written to the store.",
        "Unsaved configuration changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            SetStatus("Wait for the current store operation to finish before closing.");
            return;
        }
        if (!ConfirmDiscardAll("close the editor"))
        {
            e.Cancel = true;
            return;
        }
        _suppressEditor = true;
        ValuePasswordBox.Clear();
        RevealedValueBox.Clear();
        _editorBaselineValue = "";
        _working.Clear();
        _explicitTextKeys.Clear();
        _snapshot = null;
    }

    private sealed record SecretRow(string Key, string State, string Source, Brush StateBrush);
}
