using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DatabaseEndpointPicker : UserControl
{
    private readonly ObservableCollection<DatabaseConnectionSource> _sources = [];
    private CancellationTokenSource? _loading;
    private SettingsStore? _settings;
    public event EventHandler? EndpointChanged;
    public DatabaseConnectionSource? Source => SourcePicker.SelectedItem as DatabaseConnectionSource;
    public string Database => DatabasePicker.Text;
    public IReadOnlyList<DatabaseConnectionSource> Sources => _sources.ToArray();
    public string Identity => Source is { } source ? $"{source.Server} / {Database}" : "Choose a connection";
    public bool IsReady => Source is not null && !string.IsNullOrWhiteSpace(Database);

    public DatabaseEndpointPicker()
    {
        InitializeComponent();
        SourcePicker.ItemsSource = _sources;
        DatabasePicker.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => EndpointChanged?.Invoke(this, EventArgs.Empty)));
        IsEnabledChanged += (_, _) => { if (!IsEnabled) _loading?.Cancel(); };
        Unloaded += (_, _) => _loading?.Cancel();
    }

    public void Initialize(string heading, IEnumerable<DatabaseConnectionSource> sources, DatabaseConnectionSource? selected, string? database = null, SettingsStore? store = null)
    {
        _settings = store;
        Heading.Text = heading;
        _sources.Clear();
        foreach (var source in sources) _sources.Add(source);
        Select(selected, database);
    }

    public void Select(DatabaseConnectionSource? source, string? database)
    {
        if (source is not null) AddSource(source);
        SourcePicker.SelectedItem = source is null ? null : _sources.First(existing => existing.Id == source.Id);
        DatabasePicker.Text = database ?? source?.DefaultDatabase ?? "";
    }

    public void AddSource(DatabaseConnectionSource source)
    {
        if (_sources.All(existing => existing.Id != source.Id)) _sources.Add(source);
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        _loading?.Cancel();
        DatabasePicker.ItemsSource = null;
        DatabasePicker.Text = Source?.DefaultDatabase ?? "";
        ServerLabel.Text = Source?.Server ?? "Configured sources and saved connections";
        ServerLabel.ToolTip = ServerLabel.Text;
        Notice.Text = "Enter a database name or load the databases visible to this role.";
        EndpointChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (Source is not { } source) return;
        _loading?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _loading = request;
        LoadButton.IsEnabled = false;
        Notice.Text = "Loading databases…";
        try
        {
            var catalog = await PostgresSchemaReader.ReadDatabasesAsync(source, request.Token);
            request.Token.ThrowIfCancellationRequested();
            // Keep a manually entered database even when catalog discovery is restricted.
            var currentDatabase = Database;
            DatabasePicker.ItemsSource = catalog.Databases;
            DatabasePicker.Text = currentDatabase;
            Notice.Text = catalog.Warning ?? $"{catalog.Databases.Count} databases visible to this role.";
        }
        catch (OperationCanceledException) { if (ReferenceEquals(Source, source)) Notice.Text = "Database listing canceled or timed out."; }
        catch (Exception ex) { if (ReferenceEquals(Source, source)) Notice.Text = PostgresSchemaReader.DescribeError(ex); }
        finally { if (ReferenceEquals(_loading, request)) { _loading = null; LoadButton.IsEnabled = true; } }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DatabaseConnectionWindow(_settings) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } source) Select(source, source.DefaultDatabase);
    }
}
