using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ToolCredentialManagerWindow : Window
{
    private readonly string? _preferredKind;
    private readonly string? _websiteOrigin;
    private readonly IReadOnlyList<DeveloperTool> _tools;
    private readonly ObservableCollection<ProfileRow> _rows = [];
    private bool _initialized;
    private bool _busy;
    private bool _childOpen;
    private bool _closed;

    public ToolCredentialProfile? SelectedProfile { get; private set; }

    public ToolCredentialManagerWindow(string? preferredKind = null, string? websiteOrigin = null,
        string? selectedId = null, IReadOnlyList<DeveloperTool>? tools = null)
    {
        _preferredKind = preferredKind;
        ToolCredentialEditorWindow.TryWebsiteOrigin(websiteOrigin, out var origin);
        _websiteOrigin = origin;
        _tools = tools ?? [];
        InitializeComponent();
        ProfilesList.ItemsSource = _rows;
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        _initialized = true;
        if (string.IsNullOrWhiteSpace(preferredKind))
        {
            UseButton.Visibility = Visibility.Collapsed;
            UseButton.IsDefault = false;
            CloseButton.IsDefault = true;
        }
        UpdateSelection();
        Loaded += async (_, _) => { await LoadProfilesAsync(selectedId); if (!_closed) ProfilesList.Focus(); };
        Closed += (_, _) => _closed = true;
    }

    private async Task LoadProfilesAsync(string? selectedId = null)
    {
        if (_busy || _closed) return;
        selectedId ??= (ProfilesList.SelectedItem as ProfileRow)?.Profile.Id;
        ClearError();
        SetBusy(true, "Loading login profiles…");
        try
        {
            var profiles = await Task.Run(ToolCredentialStore.ListProfiles);
            if (_closed) return;
            ReplaceProfiles(profiles, selectedId);
        }
        catch (ToolCredentialStoreException ex)
        {
            if (!_closed) ShowError(ex.Message);
        }
        catch (Exception)
        {
            if (!_closed) ShowError("Could not load login profiles from Windows Credential Manager. Try Refresh.");
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void ReplaceProfiles(IReadOnlyList<ToolCredentialProfile> profiles, string? selectedId)
    {
        _rows.Clear();
        foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new ProfileRow(profile));
        ProfilesList.SelectedItem = _rows.FirstOrDefault(row => string.Equals(row.Profile.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        UpdateSelection();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadProfilesAsync();

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var profile = (ProfilesList.SelectedItem as ProfileRow)?.Profile;
        var idle = !_busy && !_childOpen;
        EditButton.IsEnabled = idle && profile is not null;
        DeleteButton.IsEnabled = idle && profile is not null;
        UseButton.IsEnabled = idle && profile is not null && IsCompatible(profile);
        UseButton.ToolTip = profile is not null && !IsCompatible(profile)
            ? "Choose a profile with the matching login type and exact website origin."
            : "Select this profile for the tool. Save tools applies the assignment.";
        EmptyHint.Visibility = _rows.Count == 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsCompatible(ToolCredentialProfile profile)
    {
        if (string.IsNullOrWhiteSpace(_preferredKind)) return true;
        if (!string.Equals(profile.Kind, _preferredKind, StringComparison.OrdinalIgnoreCase)) return false;
        if (!_preferredKind.Equals("Website", StringComparison.OrdinalIgnoreCase)) return true;
        return _websiteOrigin is not null
            && ToolCredentialEditorWindow.TryWebsiteOrigin(profile.Origin, out var origin)
            && string.Equals(origin, _websiteOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await EditProfileAsync(null);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ProfileRow selected) await EditProfileAsync(selected.Profile);
    }

    private async Task EditProfileAsync(ToolCredentialProfile? profile)
    {
        if (_busy || _childOpen || _closed) return;
        ClearError();
        _childOpen = true;
        UpdateSelection();
        ToolCredentialProfile? saved = null;
        try
        {
            var editor = new ToolCredentialEditorWindow(profile, _preferredKind, _websiteOrigin) { Owner = this };
            if (editor.ShowDialog() == true) saved = editor.Result;
        }
        finally
        {
            _childOpen = false;
            if (!_closed) UpdateSelection();
        }
        if (_closed || saved is null) return;
        var existing = _rows.FirstOrDefault(row => string.Equals(row.Profile.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _rows.Remove(existing);
        var row = new ProfileRow(saved);
        _rows.Add(row);
        ProfilesList.SelectedItem = row;
        await LoadProfilesAsync(saved.Id);
        if (!_closed) ProfilesList.Focus();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _childOpen || _closed || ProfilesList.SelectedItem is not ProfileRow row) return;
        var profile = row.Profile;
        if (_tools.Any(tool => string.Equals(tool.CredentialProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("Remove this profile's assignments from tools and save those changes before deleting the profile.");
            return;
        }
        _childOpen = true;
        UpdateSelection();
        bool confirmed;
        try { confirmed = ConfirmDelete(profile.Name); }
        finally { _childOpen = false; UpdateSelection(); }
        if (!confirmed || _closed) return;
        ClearError();
        SetBusy(true, "Deleting login profile…");
        var deleted = false;
        try
        {
            await Task.Run(() => ToolCredentialStore.Delete(profile.Id, expectedRevision: profile.Revision));
            if (_closed) return;
            _rows.Remove(row);
            deleted = true;
        }
        catch (ToolCredentialStoreException ex)
        {
            if (!_closed) ShowError(ex.Message);
        }
        catch (Exception)
        {
            if (!_closed) ShowError("Credential deletion was not confirmed. Refresh profiles before retrying.");
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
        if (deleted) await LoadProfilesAsync();
        if (!_closed) ProfilesList.Focus();
    }

    private bool ConfirmDelete(string name)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Delete login profile", Icon = Icon,
            Width = Math.Min(450, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)FindResource("BackgroundBrush"), Foreground = (Brush)FindResource("TextBrush")
        };
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = $"Delete \"{name}\" from Windows Credential Manager? This removes the saved login immediately.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 86, Margin = new Thickness(0, 0, 8, 0) };
        var delete = new Button { Content = "Delete", MinWidth = 86, Style = (Style)FindResource("DangerButton") };
        delete.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }

    private async void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_preferredKind) || _busy || _childOpen || _closed
            || ProfilesList.SelectedItem is not ProfileRow row || !IsCompatible(row.Profile)) return;
        var id = row.Profile.Id;
        ClearError();
        SetBusy(true, "Checking selected login profile…");
        try
        {
            var profiles = await Task.Run(ToolCredentialStore.ListProfiles);
            if (_closed) return;
            var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            ReplaceProfiles(profiles, id);
            if (profile is null || !IsCompatible(profile))
            {
                ShowError("Selected profile is unavailable or no longer matches this tool. Choose a matching profile.");
                return;
            }
            SelectedProfile = profile;
            SetBusy(false);
            DialogResult = true;
        }
        catch (ToolCredentialStoreException ex)
        {
            if (!_closed) ShowError(ex.Message);
        }
        catch (Exception)
        {
            if (!_closed) ShowError("Could not check the selected login profile. Try Refresh.");
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        ProfilesList.IsEnabled = !busy;
        ActionsPanel.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        WorkText.Text = message;
        WorkPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy || _childOpen)
        {
            e.Cancel = true;
            ShowError("Finish the open credential dialog or wait for the credential operation before closing.");
            return;
        }
        if (DialogResult != true) SelectedProfile = null;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorPanel.BringIntoView();
    }

    private sealed record ProfileRow(ToolCredentialProfile Profile)
    {
        public string Name => Profile.Name;
        public string Summary => $"{(Profile.Kind.Equals("Website", StringComparison.OrdinalIgnoreCase) ? "Website login" : "Windows account")} · {Profile.UserName}";
        public string Details => Profile.Origin is null ? Summary : $"{Summary}\n{Profile.Origin}";
    }
}
