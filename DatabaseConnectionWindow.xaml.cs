using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DatabaseConnectionWindow : Window
{
    private readonly SavedDatabaseConnectionStore _store;
    private CancellationTokenSource? _connectionRequest;
    private bool _saving;
    public DatabaseConnectionSource? Result { get; private set; }
    public DatabaseConnectionWindow(SettingsStore? store = null)
    {
        _store = new SavedDatabaseConnectionStore(store);
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_connectionRequest is not null)
            {
                e.Cancel = true;
                if (!_saving) _connectionRequest.Cancel();
            }
        };
        Closed += (_, _) => ConnectionString.Clear();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Cancel_Click(this, new RoutedEventArgs());
        };
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionRequest is not null) return;
        if (string.IsNullOrWhiteSpace(ConnectionLabel.Text) || string.IsNullOrWhiteSpace(ConnectionString.Password))
        { Notice.Text = "Enter a label and a PostgreSQL connection string."; return; }
        if (ConnectionLabel.Text.Any(char.IsControl) || ConnectionString.Password.Length > 32768)
        { Notice.Text = "Use a label without line breaks and a connection string under 32,768 characters."; return; }
        DatabaseConnectionSource candidate;
        var label = ConnectionLabel.Text.Trim();
        var connectionString = ConnectionString.Password;
        try
        {
            connectionString = DatabaseConnectionSecurity.NormalizePostgresConnectionString(connectionString);
            candidate = new($"temporary/{Guid.NewGuid():N}", "", label, connectionString);
        }
        catch (ArgumentException)
        { Notice.Text = "The connection string is invalid. Check its Npgsql keywords and include a host. Remote connections require SSL Mode=VerifyFull and a trusted server certificate."; return; }

        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _connectionRequest = request;
        SaveButton.IsEnabled = ConnectionLabel.IsEnabled = ConnectionString.IsEnabled = false;
        Notice.Text = "Connecting to PostgreSQL…";
        try
        {
            // Authentication is sufficient to verify the endpoint; no account/schema/data writes are made.
            await using (var connection = candidate.CreateConnection()) await connection.OpenAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _saving = true;
            CancelButton.IsEnabled = false;
            Notice.Text = "Connected. Saving the encrypted connection…";
            Result = await Task.Run(() => _store.Save(label, connectionString));
        }
        catch (SavedDatabaseConnectionException ex) { Notice.Text = ex.Message; }
        catch (OperationCanceledException) { Notice.Text = "Connection canceled or timed out. Nothing was saved."; }
        catch (Exception ex) { Notice.Text = PostgresSchemaReader.DescribeError(ex) + " Remote TLS verifies the certificate and hostname; configure Root Certificate for a private CA."; }
        finally
        {
            _connectionRequest = null;
            _saving = false;
            SaveButton.IsEnabled = CancelButton.IsEnabled = ConnectionLabel.IsEnabled = ConnectionString.IsEnabled = true;
        }
        if (Result is not null) { ConnectionString.Clear(); DialogResult = true; }
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
}
