using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ToolCredentialEditorWindow : Window
{
    private readonly ToolCredentialProfile? _profile;
    private bool _initialized;
    private bool _busy;
    private bool _closed;

    public ToolCredentialProfile? Result { get; private set; }

    public ToolCredentialEditorWindow(ToolCredentialProfile? profile = null,
        string? preferredKind = null, string? websiteOrigin = null)
    {
        _profile = profile;
        InitializeComponent();
        Title = profile is null ? "Add login profile" : "Edit login profile";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        NameInput.Text = profile?.Name ?? "";
        UserNameInput.Text = profile?.UserName ?? "";
        OriginInput.Text = profile?.Origin ?? websiteOrigin ?? "";
        var kind = profile?.Kind ?? preferredKind ?? "Windows";
        if (kind.Equals("Website", StringComparison.OrdinalIgnoreCase)) WebsiteKind.IsChecked = true;
        else WindowsKind.IsChecked = true;
        PasswordLabel.Content = profile is null ? "_Password" : "New _password (optional)";
        PasswordInput.ToolTip = profile is null ? "Saved in Windows Credential Manager."
            : "Leave blank to keep the saved password. Existing passwords are never shown here.";
        _initialized = true;
        UpdateKindControls();
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
        Closed += (_, _) => { _closed = true; PasswordInput.Clear(); };
    }

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ClearError();
        UpdateKindControls();
    }

    private void UpdateKindControls()
    {
        var website = WebsiteKind.IsChecked == true;
        OriginPanel.Visibility = website ? Visibility.Visible : Visibility.Collapsed;
        UserNameInput.ToolTip = website ? "User name or email used by this website."
            : @"Windows account, for example DOMAIN\user or user@domain.";
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_busy) ClearError();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_busy) ClearError();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        var name = NameInput.Text.Trim();
        var userName = UserNameInput.Text.Trim();
        var website = WebsiteKind.IsChecked == true;
        var kind = website ? "Website" : "Windows";
        string? origin = null;
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            ShowError("Enter a profile name without control characters.");
            NameInput.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(userName) || userName.Any(char.IsControl))
        {
            ShowError("Enter a user name without control characters.");
            UserNameInput.Focus();
            return;
        }
        if (website && !TryWebsiteOrigin(OriginInput.Text, out origin))
        {
            ShowError("Enter an exact HTTPS website origin. Loopback HTTP is allowed. Remove paths, credentials, queries, and fragments.");
            OriginInput.Focus();
            return;
        }
        using var password = PasswordInput.SecurePassword;
        if (password.Length == 0 && _profile is null)
        {
            ShowError("Enter a password for this new profile.");
            PasswordInput.Focus();
            return;
        }
        ClearError();
        SetBusy(true);
        try
        {
            var saved = await Task.Run(() =>
            {
                if (password.Length == 0 && _profile is not null)
                {
                    // Read the old password only for this explicit save, never while opening or editing.
                    using var existing = ToolCredentialStore.Read(_profile.Id);
                    using var retainedPassword = existing.Password.Copy();
                    return ToolCredentialStore.Save(_profile.Id, name, userName, retainedPassword,
                        kind, origin, expectedRevision: _profile.Revision);
                }
                return ToolCredentialStore.Save(_profile?.Id, name, userName, password,
                    kind, origin, expectedRevision: _profile?.Revision);
            });
            if (_closed) return;
            Result = saved;
            PasswordInput.Clear();
            SetBusy(false);
            DialogResult = true;
        }
        catch (ToolCredentialStoreException ex)
        {
            if (!_closed) ShowError(ex.Message);
        }
        catch (Exception)
        {
            if (!_closed)
                ShowError("Credential save was not confirmed. Check the profile fields, then refresh profiles before retrying if the saved profile changed.");
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    internal static bool TryWebsiteOrigin(string? value, out string? origin)
    {
        origin = null;
        if (!ToolCredentialStore.TryWebsiteOrigin(value, out var normalized, requireOriginOnly: true)) return false;
        origin = normalized;
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        FieldsPanel.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        ShowError("Wait for the credential save to finish before closing.");
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorPanel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetHelpText(SaveButton, "");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorPanel.BringIntoView();
        AutomationProperties.SetHelpText(SaveButton, message);
    }
}
