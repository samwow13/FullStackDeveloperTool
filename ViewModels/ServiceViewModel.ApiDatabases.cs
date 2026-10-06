using System.Collections.ObjectModel;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public sealed partial class ServiceViewModel
{
    // Only credential-free metadata survives discovery. Connection strings remain in the
    // configuration helper or explicit editor and never become binding values.
    private sealed record DiscoveredApiDatabase(string Key, string? Name);
    private IReadOnlyList<DiscoveredApiDatabase> _discoveredApiDatabases = [];
    private bool _refreshingApiDatabaseDiscovery;
    private bool _apiDatabaseDiscoveryAvailable;
    private long _apiDatabaseDiscoveryRevision;
    private long _nextApiDatabaseDiscoveryRefresh;

    public ObservableCollection<ApiDatabaseServiceViewModel> ApiDatabases { get; } = [];
    public int DatabaseServiceCount => ApiDatabases.Count;
    public int ConnectedDatabaseServiceCount => ApiDatabases.Count(database => database.IsConnected);
    public bool HasDatabaseCard => ApiDatabases.Count > 0;
    private bool HasCurrentApiDatabaseDiscovery => _apiDatabaseDiscoveryAvailable && HasApiConfiguration && !ProductionWarning;

    internal bool IsDatabaseConfigurationDiscovered(string key, string expectedName) =>
        HasCurrentApiDatabaseDiscovery && _discoveredApiDatabases.Any(database =>
            database.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(database.Name, expectedName, StringComparison.Ordinal));

    public async Task<bool> RefreshApiDatabaseDiscoveryAsync(bool force = false)
    {
        if (!HasApiConfiguration || SelectedConfiguration == "Prod") return false;
        if (_refreshingApiDatabaseDiscovery)
        {
            // A forced caller must wait for its own fresh result rather than treating a
            // cached observation or an unfinished read as completed rediscovery.
            if (force) _nextApiDatabaseDiscoveryRefresh = 0;
            return false;
        }
        if (force)
        {
            _apiDatabaseDiscoveryRevision++;
            _nextApiDatabaseDiscoveryRefresh = 0;
        }
        if (Environment.TickCount64 < _nextApiDatabaseDiscoveryRefresh) return false;
        _refreshingApiDatabaseDiscovery = true;
        _nextApiDatabaseDiscoveryRefresh = Environment.TickCount64 + 30_000;
        var revision = _apiDatabaseDiscoveryRevision;
        var folder = Directory;
        var selection = SelectedConfiguration;
        var command = Profile.StartCommand;
        var launchCommand = Profile.ApiConfiguration?.LaunchCommand;
        var url = Profile.Url;
        var runVersion = Runner.ManagedApiRunVersion;
        try
        {
            var discovered = await Task.Run(() => ReadDatabaseMetadata(
                ApiDatabaseConfiguration.LoadForDiscovery(Profile, folder)));
            if (!IsCurrent()) return false;
            _discoveredApiDatabases = discovered;
            _apiDatabaseDiscoveryAvailable = true;
            TryCompleteDatabaseChangeFromDiscovery(runVersion);
            NotifyDatabaseCard();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsCurrent()) return false;
            // Keep previous explicit discoveries visible, but do not imply that unreadable
            // configuration or an unresolved legacy store is still the effective source.
            _apiDatabaseDiscoveryAvailable = false;
            NotifyDatabaseCard();
            return false;
        }
        finally { _refreshingApiDatabaseDiscovery = false; }

        bool IsCurrent() => revision == _apiDatabaseDiscoveryRevision && folder == Directory &&
            selection == SelectedConfiguration && command == Profile.StartCommand &&
            launchCommand == Profile.ApiConfiguration?.LaunchCommand && url == Profile.Url && HasApiConfiguration;
    }

    internal void ObserveDatabaseConfiguration(ApiDatabaseConfiguration configuration)
    {
        if (!configuration.WorkingDirectory.Equals(Directory, StringComparison.OrdinalIgnoreCase) || SelectedConfiguration == "Prod") return;
        _apiDatabaseDiscoveryRevision++;
        _nextApiDatabaseDiscoveryRefresh = Environment.TickCount64 + 30_000;
        _discoveredApiDatabases = ReadDatabaseMetadata(configuration);
        _apiDatabaseDiscoveryAvailable = true;
        NotifyDatabaseCard();
    }

    private static IReadOnlyList<DiscoveredApiDatabase> ReadDatabaseMetadata(ApiDatabaseConfiguration configuration) =>
        configuration.Connections.Select(connection => new DiscoveredApiDatabase(
            connection.Key, connection.DatabaseDisplayName)).ToArray();

    private void UpdateApiDatabaseServices()
    {
        var observations = _discoveredApiDatabases.Select(database =>
            (Identity: database.Key, database.Name)).ToList();
        if (_databaseChangeCardName is { } changingName && _databaseChangeKey is { } changeKey &&
            !observations.Any(database => database.Identity.Equals(changeKey, StringComparison.OrdinalIgnoreCase)))
            observations.Add((changeKey, changingName));

        var identities = observations.Select(database => database.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = ApiDatabases.Count - 1; index >= 0; index--)
            if (!identities.Contains(ApiDatabases[index].Identity)) ApiDatabases.RemoveAt(index);
        foreach (var observation in observations)
        {
            var database = ApiDatabases.FirstOrDefault(item =>
                item.Identity.Equals(observation.Identity, StringComparison.OrdinalIgnoreCase));
            if (database is null)
            {
                database = new(this, observation.Identity);
                ApiDatabases.Add(database);
            }
            var changing = _databaseChangeStatus is not null &&
                observation.Identity.Equals(_databaseChangeKey, StringComparison.OrdinalIgnoreCase);
            var connected = !changing && HasCurrentApiDatabaseDiscovery && observation.Name is not null && !Runner.ConfigurationNeedsRestart;
            var status = changing ? DatabaseCardStatus
                : !HasCurrentApiDatabaseDiscovery ? _apiDatabaseDiscoveryAvailable
                    ? "Unavailable · Local configuration inactive" : "Unavailable · configuration unreadable"
                : observation.Name is null ? "Unavailable · database name unreadable"
                : Runner.ConfigurationNeedsRestart ? "Detected · restart required"
                : "Connected";
            var cardDetails = changing ? DatabaseCardDetails
                : (observation.Name is null ? "A safe database root name could not be read. " : "") +
                  (!_apiDatabaseDiscoveryAvailable ? "Latest configuration discovery is unavailable; previous discovery retained. " : "") +
                  (ProductionWarning ? "Local database configuration is inactive while Production is selected or running. " : "") +
                  (Runner.ConfigurationNeedsRestart ? $"Restart {Name} to apply its saved configuration." : "");
            database.SetObservation(observation.Name, status, connected, cardDetails.Trim());
        }
        NotifyIfChanged(DatabaseServiceCount, nameof(DatabaseServiceCount));
        NotifyIfChanged(ConnectedDatabaseServiceCount, nameof(ConnectedDatabaseServiceCount));
        NotifyIfChanged(HasDatabaseCard, nameof(HasDatabaseCard));
    }
}

public sealed class ApiDatabaseServiceViewModel(ServiceViewModel service, string identity) : ObservableObject
{
    private bool _hasDatabaseName;
    internal string Identity { get; } = identity;
    public ServiceViewModel Service { get; } = service;
    public string Name { get; private set; } = "Database";
    public string Title => _hasDatabaseName ? $"{Name} Database" : "Database";
    public string Status { get; private set; } = "Reading configuration…";
    public string StatusColor => IsConnected ? "#81D5AE" : "#FFD27A";
    public string CardDetails { get; private set; } = "";
    public bool HasCardDetails => !string.IsNullOrWhiteSpace(CardDetails);
    public bool IsConnected { get; private set; }

    internal void SetObservation(string? name, string status, bool connected, string cardDetails)
    {
        var titleChanged = Name != (name ?? "Database") || _hasDatabaseName != (name is not null);
        _hasDatabaseName = name is not null;
        if (Name != (name ?? "Database"))
        {
            Name = name ?? "Database";
            Changed(nameof(Name));
        }
        if (titleChanged) Changed(nameof(Title));
        if (Status != status) { Status = status; Changed(nameof(Status)); }
        if (CardDetails != cardDetails)
        {
            var hadCardDetails = HasCardDetails;
            CardDetails = cardDetails;
            Changed(nameof(CardDetails));
            if (hadCardDetails != HasCardDetails) Changed(nameof(HasCardDetails));
        }
        if (IsConnected != connected)
        {
            IsConnected = connected;
            Changed(nameof(IsConnected));
            Changed(nameof(StatusColor));
        }
    }
}
