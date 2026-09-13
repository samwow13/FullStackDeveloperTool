using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DatabaseAccountsWindow : Window
{
    private readonly ProjectProfile? _project;
    private readonly ProjectProfile[] _projects;
    private readonly SettingsStore _store;
    private readonly DatabaseConnectionSource? _initialSource;
    private readonly string? _initialDatabase;
    private IReadOnlyList<DatabaseAccount> _accounts = [];
    private DatabaseAccountResponse? _schema;
    private DateTime? _lastReadAt;
    private CancellationTokenSource? _operation;
    private bool _initializing = true;
    private bool _busy;
    private bool _ready;
    private bool _writing;
    private bool _updatingList;
    private DatabaseAccount? Selected => AccountGrid.SelectedItem as DatabaseAccount;
    private bool Removing => Selected is not null && ActionPicker.SelectedIndex == 1;
    private bool NeedsProject => _schema is { RequiresProject: true } && string.Equals(RolePicker.SelectedItem as string, "Customer", StringComparison.Ordinal);

    public DatabaseAccountsWindow(ProjectProfile? project, IEnumerable<ProjectProfile> projects, SettingsStore store,
        DatabaseConnectionSource? initialSource = null, string? initialDatabase = null)
    {
        InitializeComponent();
        _project = project; _projects = projects.ToArray(); _store = store;
        _initialSource = initialSource; _initialDatabase = initialDatabase;
        Endpoint.EndpointChanged += (_, _) => SelectionChanged();
        Loaded += async (_, _) =>
        {
            await ReloadAsync(true);
            if (_initialSource is not null && Endpoint.IsReady) await ConnectAsync();
        };
        _initializing = false;
        UpdateEditor();
        UpdateControls();
    }

    private async Task ReloadAsync(bool initial = false)
    {
        await RunAsync("Finding saved database connections…", async token =>
        {
            var oldSource = initial ? _initialSource : Endpoint.Source;
            var oldDatabase = initial ? _initialDatabase : Endpoint.Database;
            var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(_projects, _store), token);
            token.ThrowIfCancellationRequested();
            var sources = discovery.Sources.OrderByDescending(x => x.ProjectId == _project?.Id).ThenBy(x => x.Label).ToList();
            if (oldSource is not null && sources.All(x => x.Id != oldSource.Id) && oldSource.Id.StartsWith("temporary/", StringComparison.Ordinal))
                sources.Add(oldSource);
            var selected = oldSource is null
                ? sources.FirstOrDefault(x => x.ProjectId == _project?.Id) ?? sources.FirstOrDefault()
                : sources.FirstOrDefault(x => x.Id == oldSource.Id);
            _initializing = true;
            try
            {
                Endpoint.Initialize("ACCOUNT DATABASE · Local or Prod", sources, selected, oldDatabase, store: _store);
            }
            finally { _initializing = false; }
            InvalidateAccounts();
            StatusText.Text = string.IsNullOrWhiteSpace(discovery.Notice)
                ? "Choose the target database and connect to load its accounts."
                : discovery.Notice;
        });
    }

    private void SelectionChanged()
    {
        if (_initializing) return;
        InvalidateAccounts();
        StatusText.Text = "Database selection changed. Connect to load its accounts.";
        UpdateControls();
    }

    private void InvalidateAccounts()
    {
        _ready = false;
        _schema = null;
        _lastReadAt = null;
        _accounts = [];
        AccountGrid.ItemsSource = null;
        AssignmentPicker.ItemsSource = null;
        RolePicker.ItemsSource = null;
        EmailColumn.Visibility = DisplayNameColumn.Visibility = ProjectColumn.Visibility = Visibility.Collapsed;
        UserNameInput.Clear(); EmailInput.Clear(); DisplayNameInput.Clear(); ClearSensitiveInputs();
        SchemaText.Text = "Account tables are detected when you connect.";
        CountText.Text = "No accounts loaded.";
        TargetText.Text = Endpoint.Source is { } source ? $"Selected: {source.Label}\n{Endpoint.Identity} · accounts not loaded" : "Choose a database, then connect to load its accounts.";
        ConfirmationHint.Text = Endpoint.IsReady ? Endpoint.Identity : "Connect to a database first.";
        UpdateEditor();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        if (_busy || Endpoint.Source is not { } source || !Endpoint.IsReady) return;
        var database = Endpoint.Database;
        InvalidateAccounts();
        Search.Clear();
        await RunAsync($"Connecting directly to {source.Server} / {database}…", async token =>
        {
            await LoadAccountsAsync(source, database, null, token);
            StatusText.Text = $"Connected. {_accounts.Count} accounts loaded, including administrators and inactive accounts. Select an account or choose New account.";
        });
    }

    private async Task LoadAccountsAsync(DatabaseConnectionSource source, string database, string? selectedId, CancellationToken token)
    {
        var response = await DatabaseAccountService.ExecuteAsync(source, database,
            new DatabaseAccountRequest { Operation = "list" }, token);
        if (!response.Success) throw new DatabaseAccountException(response.Message, response.OutcomeUncertain);
        token.ThrowIfCancellationRequested();
        _accounts = response.Accounts;
        _lastReadAt = DateTime.Now;
        _schema = response;
        _ready = true;
        var selectedRole = RolePicker.SelectedItem as string;
        RolePicker.ItemsSource = response.Roles;
        RolePicker.SelectedItem = response.Roles.Contains(selectedRole ?? "", StringComparer.Ordinal) ? selectedRole : response.DefaultRole;
        SchemaText.Text = $"{response.SchemaKind} · {response.AccountTable}";
        EmailColumn.Visibility = response.SupportsEmail ? Visibility.Visible : Visibility.Collapsed;
        DisplayNameColumn.Visibility = response.SupportsDisplayName ? Visibility.Visible : Visibility.Collapsed;
        ProjectColumn.Visibility = response.RequiresProject ? Visibility.Visible : Visibility.Collapsed;
        var assignedId = (AssignmentPicker.SelectedItem as DatabaseAccountProject)?.Id;
        AssignmentPicker.ItemsSource = response.Projects;
        AssignmentPicker.SelectedItem = response.Projects.FirstOrDefault(p => p.Id == assignedId);
        FilterAccounts(selectedId);
        TargetText.Text = $"CONNECTED · {source.Label}\n{source.Server} / {database}";
        ConfirmationHint.Text = $"{source.Server} / {database}";
        UpdateEditor();
    }

    private void FilterAccounts(string? preferredId = null)
    {
        if (AccountGrid is null || Search is null) return;
        var selectedId = preferredId ?? Selected?.Id;
        var search = Search.Text.Trim();
        var visible = _accounts.Where(a => search.Length == 0 ||
            $"{a.UserName} {a.DisplayName} {a.Email} {a.Role} {a.ProjectName} {a.Status}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        _updatingList = true;
        try
        {
            AccountGrid.ItemsSource = visible;
            AccountGrid.SelectedItem = visible.FirstOrDefault(a => a.Id == selectedId);
        }
        finally { _updatingList = false; }
        CountText.Text = _ready ? $"{visible.Length} of {_accounts.Count} accounts · refreshed {_lastReadAt:HH:mm:ss}" : "No accounts loaded.";
        ClearSensitiveInputs(); UpdateEditor(); UpdateControls();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterAccounts();
    private void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || Editor is null) return;
        ClearSensitiveInputs();
        ActionPicker.SelectedIndex = 0;
        UpdateEditor(); UpdateControls();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        AccountGrid.SelectedItem = null;
        ClearSensitiveInputs(); UpdateEditor(); UpdateControls(); UserNameInput.Focus();
    }

    private void UpdateEditor()
    {
        if (Editor is null || ApplyButton is null) return;
        var selected = Selected;
        var creating = selected is null;
        CreateFields.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        ExistingFields.Visibility = creating ? Visibility.Collapsed : Visibility.Visible;
        AccountConfirmationFields.Visibility = creating ? Visibility.Collapsed : Visibility.Visible;
        PasswordFields.Visibility = Removing ? Visibility.Collapsed : Visibility.Visible;
        EmailFields.Visibility = _schema is { SupportsEmail: true } ? Visibility.Visible : Visibility.Collapsed;
        DisplayNameFields.Visibility = _schema is { SupportsDisplayName: true } ? Visibility.Visible : Visibility.Collapsed;
        ProjectLabel.Visibility = NeedsProject ? Visibility.Visible : Visibility.Collapsed;
        AssignmentPicker.Visibility = ProjectLabel.Visibility;
        RoleNotice.Text = NeedsProject
            ? "Customers need an active project. Share initial credentials privately; no email is sent."
            : "The selected role controls this user's access. Share initial credentials privately; no email is sent.";
        EditorHeading.Text = creating ? "New account" : selected!.UserName;
        SelectedDetails.Text = creating ? "" : string.Join("\n", new[]
        { selected!.DisplayName, selected.Email, $"{selected.Role} · {selected.Status}", selected.ProjectName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        ActionNotice.Text = creating ? "The account will be ready to sign in with this password."
            : Removing ? selected!.CanDelete
                ? _schema is { RequiresProject: true }
                    ? "Permanently removes this customer and unbilled work. Billed work and payment history are retained; manual billing is disabled."
                    : "Permanently removes this user and their role assignments."
                : "Removal is unavailable for protected administrators or accounts whose related records require application cleanup."
            : "Replaces the password and clears failed-login lockout. No email is sent.";
        if (_schema is { OperationNotice.Length: > 0 }) ActionNotice.Text += "\n\n" + _schema.OperationNotice;
        ApplyButton.Content = creating ? "Create account" : Removing ? "Permanently remove account" : "Set password";
        ApplyButton.Style = (Style)FindResource(Removing ? "DangerButton" : "PrimaryButton");
    }

    private void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyButton is null) return;
        ClearSensitiveInputs(); UpdateEditor(); UpdateControls();
    }
    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyButton is null) return;
        ClearSensitiveInputs(); UpdateEditor(); UpdateControls();
    }
    private void Input_Changed(object sender, TextChangedEventArgs e) => UpdateControls();
    private void InputSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateControls();

    private bool CanApply()
    {
        if (_busy || !_ready || _schema is null || !Endpoint.IsReady || !string.Equals(TargetConfirmation.Text, Endpoint.Identity, StringComparison.Ordinal)) return false;
        if (Selected is { } selected && !string.Equals(AccountConfirmation.Text, selected.UserName, StringComparison.Ordinal)) return false;
        if (Removing) return Selected is { CanDelete: true };
        var password = PasswordInput.Password;
        if (password.Length < 12 || password != RepeatPasswordInput.Password
            || !password.Any(c => c is >= 'A' and <= 'Z') || !password.Any(c => c is >= 'a' and <= 'z')
            || !password.Any(c => c is >= '0' and <= '9') || password.All(char.IsLetterOrDigit)) return false;
        if (Selected is not null) return _schema.CanSetPassword;
        return _schema.CanCreate && !string.IsNullOrWhiteSpace(UserNameInput.Text) && RolePicker.SelectedItem is string
            && (!_schema.SupportsEmail || !string.IsNullOrWhiteSpace(EmailInput.Text))
            && (!_schema.SupportsDisplayName || !string.IsNullOrWhiteSpace(DisplayNameInput.Text))
            && (!NeedsProject || AssignmentPicker.SelectedItem is DatabaseAccountProject);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!CanApply() || _schema is null || Endpoint.Source is not { } source) return;
        var selectedId = Selected?.Id;
        var request = new DatabaseAccountRequest
        {
            Operation = Selected is null ? "create" : Removing ? "delete" : "set-password",
            UserId = selectedId, UserName = Selected is null ? UserNameInput.Text.Trim() : null,
            Email = Selected is null && _schema.SupportsEmail ? EmailInput.Text.Trim() : null,
            DisplayName = Selected is null && _schema.SupportsDisplayName ? DisplayNameInput.Text.Trim() : null,
            Password = Removing ? null : PasswordInput.Password,
            Role = Selected is null ? RolePicker.SelectedItem as string : null,
            ProjectId = Selected is null && NeedsProject ? (AssignmentPicker.SelectedItem as DatabaseAccountProject)?.Id : null,
            Confirmation = TargetConfirmation.Text, AccountConfirmation = AccountConfirmation.Text
        };
        var database = Endpoint.Database;
        ClearSensitiveInputs();
        _writing = true;
        await RunAsync($"Applying account change to {Endpoint.Identity}…", async token =>
        {
            var response = await DatabaseAccountService.ExecuteAsync(source, database, request, token);
            if (!response.Success)
            {
                if (response.OutcomeUncertain) InvalidateAccounts();
                StatusText.Text = response.Message;
                return;
            }
            // Retire the old list before refreshing. A confirmed write remains successful if refresh fails.
            _ready = false;
            _accounts = []; AccountGrid.ItemsSource = null;
            _writing = false;
            if (request.Operation == "create") { UserNameInput.Clear(); EmailInput.Clear(); DisplayNameInput.Clear(); }
            try
            {
                await LoadAccountsAsync(source, database, request.Operation == "delete" ? null : selectedId, token);
                StatusText.Text = response.Message;
            }
            catch (Exception)
            {
                InvalidateAccounts();
                StatusText.Text = response.Message + " Account refresh did not finish. Connect again to view current accounts.";
            }
        });
        _writing = false;
        UpdateControls();
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _operation = operation; _busy = true; StatusText.Text = message; UpdateControls();
        try { await action(operation.Token); }
        catch (DatabaseAccountException ex)
        {
            if (ex.OutcomeUncertain) InvalidateAccounts();
            StatusText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            if (_writing) InvalidateAccounts();
            StatusText.Text = _writing ? "The account change was interrupted; its outcome is not confirmed. Connect again and inspect the account before retrying." : "Operation canceled or timed out.";
        }
        catch (Exception)
        {
            if (_writing) InvalidateAccounts();
            StatusText.Text = _writing ? "The account change could not be confirmed. Connect again and inspect the account before retrying." : "Account tools could not complete this operation. Check the selected database and connection, then connect again.";
        }
        finally { _busy = false; _operation = null; UpdateControls(); }
    }

    private void UpdateControls()
    {
        if (ApplyButton is null) return;
        ConnectionInputs.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !_busy && Endpoint.IsReady;
        ReloadButton.IsEnabled = !_busy;
        AccountGrid.IsEnabled = !_busy && _ready; Search.IsEnabled = !_busy && _ready;
        NewButton.IsEnabled = !_busy && _ready && _schema is { CanCreate: true }; Editor.IsEnabled = !_busy && _ready;
        ApplyButton.IsEnabled = CanApply(); CancelButton.IsEnabled = _busy;
    }

    private void ClearSensitiveInputs()
    {
        PasswordInput?.Clear(); RepeatPasswordInput?.Clear(); TargetConfirmation?.Clear(); AccountConfirmation?.Clear();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel(); StatusText.Text = "Cancellation requested; waiting for the operation's outcome…";
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for this operation to finish, or cancel it before closing."; }
        else ClearSensitiveInputs();
    }
}
