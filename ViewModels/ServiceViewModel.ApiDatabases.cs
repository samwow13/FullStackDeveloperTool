using System.Collections.ObjectModel;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public sealed partial class ServiceViewModel
{
    // Only credential-free metadata survives discovery. Connection strings remain in the
    // configuration helper or explicit editor and never become binding values.
    private sealed record DiscoveredApiDatabase(string Key, string? Name, string Source);
    private IReadOnlyList<DiscoveredApiDatabase> _discoveredApiDatabases = [];
    private bool _refreshingApiDatabaseDiscovery;
    private bool _apiDatabaseDiscoveryAvailable;
    private long _apiDatabaseDiscoveryRevision;
    private long _nextApiDatabaseDiscoveryRefresh;

    public ObservableCollection<ApiDatabaseServiceViewModel> ApiDatabases { get; } = [];
    public int DatabaseServiceCount => ApiDatabases.Count;
    public int HealthyDatabaseServiceCount => ApiDatabases.Count(database => database.IsHealthy);
    public bool HasDatabaseCard => ApiDatabases.Count > 0;

    public async Task RefreshApiDatabaseDiscoveryAsync(bool force = false)
    {
        if (!HasApiConfiguration || SelectedConfiguration == "Prod") return;
        if (force)
        {
            _apiDatabaseDiscoveryRevision++;
            _nextApiDatabaseDiscoveryRefresh = 0;
        }
        if (_refreshingApiDatabaseDiscovery || Environment.TickCount64 < _nextApiDatabaseDiscoveryRefresh) return;
        _refreshingApiDatabaseDiscovery = true;
        _nextApiDatabaseDiscoveryRefresh = Environment.TickCount64 + 30_000;
        var revision = _apiDatabaseDiscoveryRevision;
        var folder = Directory;
        var selection = SelectedConfiguration;
        var command = Profile.StartCommand;
        var launchCommand = Profile.ApiConfiguration?.LaunchCommand;
        var url = Profile.Url;
        try
        {
            var discovered = await Task.Run(() => ReadDatabaseMetadata(
                ApiDatabaseConfiguration.LoadForDiscovery(Profile, folder)));
            if (!IsCurrent()) return;
            _discoveredApiDatabases = discovered;
            _apiDatabaseDiscoveryAvailable = true;
            NotifyDatabaseCard();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsCurrent()) return;
            // Keep previous explicit discoveries visible, but do not imply that unreadable
            // configuration or an unresolved legacy store is still the effective source.
            _apiDatabaseDiscoveryAvailable = false;
            NotifyDatabaseCard();
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
            connection.Key, connection.DatabaseDisplayName, connection.Source)).ToArray();

    private void UpdateApiDatabaseServices()
    {
        var observations = _discoveredApiDatabases.Select(database =>
            (Identity: database.Key, database.Name, Source: $"{database.Key} · {database.Source}")).ToList();
        var verifiedName = LastVerifiedDatabaseName;
        var matches = observations.Where(database => database.Name is not null &&
            string.Equals(database.Name, verifiedName, StringComparison.Ordinal)).ToArray();
        string? verifiedIdentity = matches.Length == 1 && !Runner.ConfigurationNeedsRestart ? matches[0].Identity : null;
        if (verifiedName is not null && matches.Length != 1)
        {
            verifiedIdentity = "verified-query";
            observations.Add((verifiedIdentity, verifiedName, "API-reported live query"));
        }
        if (_databaseChangeCardName is { } changingName && !observations.Any(database => database.Name == changingName))
            observations.Add(("database-change", changingName, "Local database change"));

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
                (observation.Name == _databaseChangeCardName || observation.Name == _databaseChangeExpectedName);
            var healthy = !changing && observation.Identity == verifiedIdentity && IsVerifiedDatabaseHealthy;
            var status = changing ? DatabaseCardStatus : healthy ? "Connected · Healthy"
                : !_apiDatabaseDiscoveryAvailable && observation.Identity != "verified-query"
                    ? "Unavailable · configuration unreadable"
                : observation.Identity == "verified-query" ? DatabaseCardStatus
                : Runner.ConfigurationNeedsRestart ? "Detected · restart required"
                : "Detected · health unverified";
            var details = changing || observation.Identity == "verified-query" || healthy ? DatabaseCardDetails
                : $"Found in {observation.Source} for {Name}'s Local configuration. " +
                  (observation.Name is null ? "A safe database root name could not be read. " : "") +
                  (!_apiDatabaseDiscoveryAvailable ? "Latest configuration discovery is unavailable; previous discovery retained. " : "") +
                  (Runner.ConfigurationNeedsRestart ? $"Restart {Name} to apply its saved configuration. " : "") +
                  "Configuration discovery does not verify an active database connection or query.";
            database.SetObservation(observation.Name ?? "Database", observation.Source, status, details, healthy);
        }
        NotifyIfChanged(DatabaseServiceCount, nameof(DatabaseServiceCount));
        NotifyIfChanged(HealthyDatabaseServiceCount, nameof(HealthyDatabaseServiceCount));
        NotifyIfChanged(HasDatabaseCard, nameof(HasDatabaseCard));
    }
}

public sealed class ApiDatabaseServiceViewModel(ServiceViewModel service, string identity) : ObservableObject
{
    private string? _sourceLabel;
    internal string Identity { get; } = identity;
    public ServiceViewModel Service { get; } = service;
    public string Name { get; private set; } = "Database";
    public string SourceLabel => $"Database · {Service.Name}";
    public string Status { get; private set; } = "Detected · health unverified";
    public string StatusColor => IsHealthy ? "#81D5AE" : "#FFD27A";
    public string Details { get; private set; } = "";
    public bool IsHealthy { get; private set; }
    public string ConnectionSource { get; private set; } = "";

    internal void SetObservation(string name, string source, string status, string details, bool healthy)
    {
        if (Name != name) { Name = name; Changed(nameof(Name)); }
        if (ConnectionSource != source) { ConnectionSource = source; Changed(nameof(ConnectionSource)); }
        if (Status != status) { Status = status; Changed(nameof(Status)); }
        if (Details != details) { Details = details; Changed(nameof(Details)); }
        if (IsHealthy != healthy)
        {
            IsHealthy = healthy;
            Changed(nameof(IsHealthy));
            Changed(nameof(StatusColor));
        }
        if (_sourceLabel != SourceLabel) { _sourceLabel = SourceLabel; Changed(nameof(SourceLabel)); }
    }
}
