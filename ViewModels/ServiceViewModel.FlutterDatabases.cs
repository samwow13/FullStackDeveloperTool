using System.Collections.ObjectModel;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public sealed partial class ServiceViewModel
{
    private ProjectProfile? _flutterDatabaseProject;
    private string? _flutterDatabaseProjectRoot;
    private CancellationToken _flutterDatabaseLifetime;
    private bool _refreshingFlutterDatabases;
    private long _nextFlutterDatabaseRefresh;
    private ServiceState _previousFlutterDatabaseState = ServiceState.Checking;
    private bool _refreshingFlutterConnections;
    private long _nextFlutterConnectionRefresh;
    private DateTimeOffset _flutterConnectionCheckedAt;
    private ProcessIdentity[] _flutterConnectionProcesses = [];

    public ObservableCollection<FlutterDatabaseViewModel> FlutterDatabases { get; } = [];
    private bool IsNativeFlutter => Profile.Kind.Equals("Flutter", StringComparison.OrdinalIgnoreCase);

    internal void ConfigureFlutterDatabases(ProjectProfile project, string projectRoot, CancellationToken lifetime)
    {
        _flutterDatabaseProject = project;
        _flutterDatabaseProjectRoot = projectRoot;
        _flutterDatabaseLifetime = lifetime;
    }

    private void UpdateFlutterDatabaseState()
    {
        if (_previousFlutterDatabaseState != _snapshot.State)
        {
            _previousFlutterDatabaseState = _snapshot.State;
            _nextFlutterDatabaseRefresh = 0;
            _nextFlutterConnectionRefresh = 0;
        }
        if (_snapshot.State != ServiceState.Running ||
            DateTimeOffset.UtcNow - _flutterConnectionCheckedAt > TimeSpan.FromSeconds(30) ||
            !_flutterConnectionProcesses.SequenceEqual(CurrentFlutterAppProcesses()))
            foreach (var database in FlutterDatabases) database.SetConnected(false);
    }

    private ProcessIdentity[] CurrentFlutterAppProcesses() => Runner.FlutterAppProcessIdentities
        .OrderBy(process => process.Id).ThenBy(process => process.StartedUtcTicks).ToArray();

    /// <summary>Observe app-owned open SQLite files separately so slow native inspection cannot hold file discovery.</summary>
    public async Task RefreshFlutterDatabaseConnectionsAsync()
    {
        if (!IsNativeFlutter || Runner.Snapshot.State != ServiceState.Running || _refreshingFlutterConnections ||
            _flutterDatabaseLifetime.IsCancellationRequested || Environment.TickCount64 < _nextFlutterConnectionRefresh) return;
        var processes = CurrentFlutterAppProcesses();
        if (processes.Length == 0) return;
        _refreshingFlutterConnections = true;
        _nextFlutterConnectionRefresh = Environment.TickCount64 + 10_000;
        try
        {
            var paths = await Task.Run(() => FlutterSqliteConnectionProbe.ReadOpenPaths(processes, _flutterDatabaseLifetime),
                _flutterDatabaseLifetime);
            if (_flutterDatabaseLifetime.IsCancellationRequested || !IsNativeFlutter ||
                Runner.Snapshot.State != ServiceState.Running || !processes.SequenceEqual(CurrentFlutterAppProcesses())) return;
            _flutterConnectionProcesses = processes;
            _flutterConnectionCheckedAt = DateTimeOffset.UtcNow;
            var connectedPaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var database in FlutterDatabases) database.SetConnected(connectedPaths.Contains(database.Path));
            foreach (var path in paths)
            {
                var database = FlutterDatabases.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (database is null)
                {
                    if (FlutterDatabases.Count >= 128) continue;
                    database = new FlutterDatabaseViewModel(new(path, System.IO.Path.GetFileName(path),
                        "File observed in the native Flutter app process."));
                    FlutterDatabases.Add(database);
                }
                database.SetAvailable(true);
                database.SetConnected(true);
            }
        }
        catch (OperationCanceledException) when (_flutterDatabaseLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!_flutterDatabaseLifetime.IsCancellationRequested)
                foreach (var database in FlutterDatabases) database.SetConnected(false);
        }
        finally { _refreshingFlutterConnections = false; }
    }

    /// <summary>Discover existing local SQLite files without opening a database connection or saving settings.</summary>
    public async Task RefreshFlutterDatabasesAsync()
    {
        if (!IsNativeFlutter || _refreshingFlutterDatabases || _flutterDatabaseLifetime.IsCancellationRequested ||
            Environment.TickCount64 < _nextFlutterDatabaseRefresh) return;
        _refreshingFlutterDatabases = true;
        _nextFlutterDatabaseRefresh = Environment.TickCount64 + 10_000;
        var folder = Directory;
        var root = _flutterDatabaseProjectRoot ?? folder;
        var configuredPath = _flutterDatabaseProject?.SQLiteDatabasePath;
        var previousPaths = FlutterDatabases.Select(database => database.Path).ToArray();
        try
        {
            var result = await Task.Run(() =>
            {
                var observations = FlutterSqliteDiscovery.Discover(folder, root, configuredPath, _flutterDatabaseLifetime);
                var available = previousPaths.ToDictionary(path => path, FlutterSqliteDiscovery.IsAvailable,
                    StringComparer.OrdinalIgnoreCase);
                return (Observations: observations, Available: available);
            }, _flutterDatabaseLifetime);
            if (_flutterDatabaseLifetime.IsCancellationRequested || !IsNativeFlutter || folder != Directory ||
                root != (_flutterDatabaseProjectRoot ?? Directory) || configuredPath != _flutterDatabaseProject?.SQLiteDatabasePath) return;

            foreach (var database in FlutterDatabases)
                if (result.Available.TryGetValue(database.Path, out var available)) database.SetAvailable(available);
            foreach (var observation in result.Observations)
            {
                var database = FlutterDatabases.FirstOrDefault(item =>
                    item.Path.Equals(observation.Path, StringComparison.OrdinalIgnoreCase));
                if (database is null)
                {
                    if (FlutterDatabases.Count >= 128) continue;
                    database = new FlutterDatabaseViewModel(observation);
                    FlutterDatabases.Add(database);
                }
                else database.SetObservation(observation);
            }
        }
        catch (OperationCanceledException) when (_flutterDatabaseLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            if (!_flutterDatabaseLifetime.IsCancellationRequested)
                foreach (var database in FlutterDatabases) database.SetAvailable(false);
        }
        finally { _refreshingFlutterDatabases = false; }
    }
}

public sealed class FlutterDatabaseViewModel(FlutterSqliteObservation observation) : ObservableObject
{
    private FlutterSqliteObservation _observation = observation;
    private bool _available = true;
    private bool _connected;

    public string Path => _observation.Path;
    public string Name => _observation.Name;
    public string Status => !_available ? "Unavailable · file not verified" : _connected ? "In use · SQLite" : "Detected · SQLite";
    public string StatusColor => _available ? "#81D5AE" : "#FFD27A";
    public bool HasCardDetails => !_available;
    public string CardDetails => !_available
        ? $"{Path}\nSQLite file is missing, unreadable, or no longer has a valid header." : "";

    internal void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        Changed(nameof(Status));
    }

    internal void SetObservation(FlutterSqliteObservation value)
    {
        var changed = _observation != value;
        _observation = value;
        SetAvailable(true);
        if (!changed) return;
        Changed(nameof(Name));
        Changed(nameof(CardDetails));
    }

    internal void SetAvailable(bool available)
    {
        if (_available == available) return;
        _available = available;
        if (!available) _connected = false;
        Changed(nameof(Status));
        Changed(nameof(StatusColor));
        Changed(nameof(HasCardDetails));
        Changed(nameof(CardDetails));
    }
}
