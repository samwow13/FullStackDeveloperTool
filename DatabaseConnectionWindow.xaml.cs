using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace FullStackLauncher;

public partial class DatabaseConnectionWindow : Window
{
    private readonly SavedDatabaseConnectionStore _store;
    private CancellationTokenSource? _connectionRequest;
    private bool _saving;
    public DatabaseConnectionSource? Result { get; private set; }
    private DatabaseProvider Provider => ProviderPicker.SelectedIndex == 1 ? DatabaseProvider.SqlServer : DatabaseProvider.PostgreSql;

    public DatabaseConnectionWindow(SettingsStore? store = null)
    {
        _store = new SavedDatabaseConnectionStore(store);
        InitializeComponent();
        UpdateFields();
        Closing += (_, e) =>
        {
            if (_connectionRequest is null) return;
            e.Cancel = true;
            if (!_saving) _connectionRequest.Cancel();
        };
        Closed += (_, _) => { ConnectionString.Clear(); PasswordInput.Clear(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Cancel_Click(this, new RoutedEventArgs());
        };
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (SecurityHint is null) return;
        PasswordInput.Clear();
        ConnectionString.Clear();
        Notice.Text = "";
        UpdateFields();
    }

    private void UpdateFields()
    {
        var sql = Provider == DatabaseProvider.SqlServer;
        var advanced = AdvancedMode.IsChecked == true;
        StructuredFields.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedFields.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        PortFields.Visibility = CertificateFields.Visibility = sql ? Visibility.Collapsed : Visibility.Visible;
        AuthenticationFields.Visibility = sql ? Visibility.Visible : Visibility.Collapsed;
        CredentialFields.Visibility = !sql || AuthenticationPicker.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ServerHint.Text = sql ? @"Use the same server name as SSMS: server.example.com, server\INSTANCE, or server.example.com,1433. Windows authentication uses the Windows account running this launcher."
            : "Use the PostgreSQL hostname, such as localhost or server.example.com.";
        FormatHint.Text = sql ? "SqlClient format: Server=…;Initial Catalog=…;Integrated Security=True;Encrypt=True (or User ID=…;Password=… for SQL authentication)."
            : "Npgsql format: Host=…;Port=5432;Database=…;Username=…;Password=…;SSL Mode=VerifyFull";
        SecurityHint.Text = sql ? "Remote SQL Server connections require encryption and a trusted certificate matching the server name. Certificate checks cannot be disabled. Install your company's trusted CA if needed."
            : DatabaseConnectionSecurity.RemoteTlsNotice;
    }

    private string ReadConnectionString()
    {
        if (AdvancedMode.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString.Password)) throw new ConnectionInputException("Enter a connection string for the selected database type.");
            return ConnectionString.Password;
        }
        if (string.IsNullOrWhiteSpace(ServerInput.Text)) throw new ConnectionInputException("Enter the database server name.");
        var database = DatabaseInput.Text.Trim();
        if (Provider == DatabaseProvider.SqlServer)
        {
            var windows = AuthenticationPicker.SelectedIndex == 0;
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerInput.Text.Trim(), IntegratedSecurity = windows,
                Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false
            };
            if (database.Length > 0) builder.InitialCatalog = database;
            if (!windows)
            {
                if (string.IsNullOrWhiteSpace(UsernameInput.Text) || PasswordInput.Password.Length == 0)
                    throw new ConnectionInputException("Enter the SQL Server username and password.");
                builder.UserID = UsernameInput.Text.Trim();
                builder.Password = PasswordInput.Password;
            }
            return builder.ConnectionString;
        }
        if (!int.TryParse(PortInput.Text, out var port) || port is < 1 or > 65535)
            throw new ConnectionInputException("Enter a PostgreSQL port from 1 to 65535.");
        if (string.IsNullOrWhiteSpace(UsernameInput.Text) || PasswordInput.Password.Length == 0)
            throw new ConnectionInputException("Enter the PostgreSQL username and password, or use a connection string for another authentication method.");
        var postgres = new NpgsqlConnectionStringBuilder
        {
            Host = ServerInput.Text.Trim(), Port = port, Username = UsernameInput.Text.Trim(), Password = PasswordInput.Password
        };
        if (database.Length > 0) postgres.Database = database;
        if (!string.IsNullOrWhiteSpace(RootCertificateInput.Text)) postgres.RootCertificate = RootCertificateInput.Text.Trim();
        return postgres.ConnectionString;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionRequest is not null) return;
        var label = ConnectionLabel.Text.Trim();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl))
        { Notice.Text = "Enter a label of 1–100 characters without line breaks."; return; }
        var provider = Provider;
        DatabaseConnectionSource candidate;
        string connectionString;
        try
        {
            connectionString = ReadConnectionString();
            candidate = new($"temporary/{Guid.NewGuid():N}", "", label, connectionString, provider);
        }
        catch (ConnectionInputException ex) { Notice.Text = ex.Message; return; }
        catch (ArgumentException)
        { Notice.Text = "The connection is invalid for the selected database type. Check the fields or connection-string keywords. Remote servers require a trusted TLS certificate."; return; }

        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _connectionRequest = request;
        SaveButton.IsEnabled = ConnectionFields.IsEnabled = false;
        Notice.Text = $"Connecting to {candidate.ProviderLabel}…";
        try
        {
            // Authentication verifies access. No schema, account, or row writes are made.
            await using (var connection = candidate.CreateDbConnection()) await connection.OpenAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _saving = true;
            CancelButton.IsEnabled = false;
            Notice.Text = "Connected. Saving the encrypted connection…";
            Result = await Task.Run(() => _store.Save(label, connectionString, provider));
        }
        catch (SavedDatabaseConnectionException ex) { Notice.Text = ex.Message; }
        catch (OperationCanceledException) { Notice.Text = "Connection canceled or timed out. Nothing was saved."; }
        catch (Exception ex) { Notice.Text = DatabaseSchemaReader.DescribeError(ex); }
        finally
        {
            _connectionRequest = null;
            _saving = false;
            SaveButton.IsEnabled = CancelButton.IsEnabled = ConnectionFields.IsEnabled = true;
        }
        if (Result is not null) { ConnectionString.Clear(); PasswordInput.Clear(); DialogResult = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (_connectionRequest is not null)
        {
            _connectionRequest.Cancel();
            Notice.Text = "Canceling the connection…";
        }
        else DialogResult = false;
    }

    private sealed class ConnectionInputException(string message) : Exception(message);
}
