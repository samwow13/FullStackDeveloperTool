using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class DatabaseComparisonPanel : UserControl
{
    private CancellationTokenSource? _discoveryRequest;
    private CancellationTokenSource? _databaseListRequest;
    private CancellationTokenSource? _comparisonRequest;
    private readonly SemaphoreSlim _comparisonGate = new(1, 1);
    private int _refreshGeneration;
    private IReadOnlyList<DatabaseConnectionSource> _sources = [];
    private ServiceViewModel? _subscribedService;
    private bool _changingSelection;

    public DatabaseComparisonPanel()
    {
        InitializeComponent();
        ProductionDatabasePicker.AddHandler(TextBox.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                if (_changingSelection) return;
                ClearComparison();
                UpdateButtons();
            }));
    }

    private async void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeService();
        await RefreshConnectionsAsync();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshGeneration++;
        CancelRequests();
        UnsubscribeService();
    }

    private async void Panel_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _refreshGeneration++;
        CancelRequests();
        UnsubscribeService();
        ClearComparison();
        if (IsLoaded)
        {
            SubscribeService();
            await RefreshConnectionsAsync();
        }
    }

    private void SubscribeService()
    {
        if (DataContext is not ServiceViewModel service || ReferenceEquals(service, _subscribedService)) return;
        _subscribedService = service;
        service.PropertyChanged += Service_PropertyChanged;
    }

    private void UnsubscribeService()
    {
        if (_subscribedService is null) return;
        _subscribedService.PropertyChanged -= Service_PropertyChanged;
        _subscribedService = null;
    }

    private async void Service_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServiceViewModel.VerifiedDatabaseName) || !IsLoaded) return;
        _refreshGeneration++;
        _comparisonRequest?.Cancel();
        ClearComparison();
        if (_subscribedService?.VerifiedDatabaseName is null)
        {
            StatusText.Text = "Local API database health is unverified. Compare after the local API is healthy again.";
            if (_subscribedService?.HasSavedDatabaseComparisonSet == true)
                _subscribedService.SetDatabaseComparisonHeader("Saved comparison set · waiting for local API health");
            UpdateButtons();
            return;
        }
        await RefreshConnectionsAsync(preserveCurrentChoice: true);
    }

    private (MainWindow Host, ProjectProfile Project, ServiceViewModel Service)? Context()
    {
        if (Window.GetWindow(this) is not MainWindow host || DataContext is not ServiceViewModel service ||
            host.ProjectForDatabaseCard(service) is not { } project) return null;
        return (host, project, service);
    }

    private async Task RefreshConnectionsAsync(string? preferredProductionSourceId = null,
        bool preserveCurrentChoice = false)
    {
        var refreshGeneration = ++_refreshGeneration;
        var previousLocalSourceId = preserveCurrentChoice
            ? (LocalConnectionPicker.SelectedItem as DatabaseConnectionSource)?.Id : null;
        var previousProductionSourceId = preserveCurrentChoice
            ? (ProductionConnectionPicker.SelectedItem as DatabaseConnectionSource)?.Id : null;
        var previousProductionDatabase = preserveCurrentChoice ? ProductionDatabasePicker.Text : null;
        _discoveryRequest?.Cancel();
        ClearComparison();
        var shouldCompareSavedSet = false;
        var shouldCheckSavedSetHeader = false;
        var context = Context();
        if (context is not { } current) return;
        var knownName = current.Service.VerifiedDatabaseName ?? current.Service.LastVerifiedDatabaseName;
        if (knownName is null)
        {
            StatusText.Text = "No database was verified for this API run.";
            UpdateButtons();
            return;
        }

        using var request = new CancellationTokenSource();
        _discoveryRequest = request;
        StatusText.Text = "Finding local and deployed database connections…";
        UpdateButtons();
        try
        {
            var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(
                current.Host.DatabaseProjectsSnapshot, current.Host.DatabaseSettingsStore), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || !ReferenceEquals(DataContext, current.Service) ||
                !ReferenceEquals(current.Host.ProjectForDatabaseCard(current.Service), current.Project) ||
                current.Service.LastVerifiedDatabaseName != knownName) return;

            _sources = discovery.Sources;
            var discoveredLocal = DashboardDatabaseComparisonService.GetLocalCandidates(
                _sources, current.Project.Id, current.Service.Profile.Id, knownName);
            var local = discoveredLocal.Where(current.Service.Runner.MatchesAppliedLocalDatabaseConnection).ToArray();
            var production = DashboardDatabaseComparisonService.GetProductionCandidates(_sources, current.Project.Id);
            var saved = current.Service.Profile.ProductionDatabase;
            var savedLocalSourceId = current.Service.Profile.ComparisonLocalSourceId;
            var savedLocalAvailable = savedLocalSourceId is not null &&
                local.Any(source => source.Id == savedLocalSourceId);
            var savedProductionAvailable = saved is not null &&
                production.Any(source => DashboardDatabaseComparisonService.MatchesProductionSelection(saved, source));
            var localChoice = local.FirstOrDefault(source => source.Id == previousLocalSourceId)
                ?? local.FirstOrDefault(source => source.Id == savedLocalSourceId)
                ?? local.FirstOrDefault(source => source.Id == current.Project.Database?.SourceId)
                ?? (local.Length == 1 ? local[0] : null);
            var productionChoice = production.FirstOrDefault(source => source.Id == preferredProductionSourceId)
                ?? production.FirstOrDefault(source => source.Id == previousProductionSourceId)
                ?? production.FirstOrDefault(source =>
                    DashboardDatabaseComparisonService.MatchesProductionSelection(saved, source));

            _changingSelection = true;
            try
            {
                LocalConnectionPicker.ItemsSource = local;
                LocalConnectionPicker.SelectedItem = localChoice;
                ProductionConnectionPicker.ItemsSource = production;
                ProductionConnectionPicker.SelectedItem = productionChoice;
                ProductionDatabasePicker.ItemsSource = null;
                ProductionDatabasePicker.Text = preferredProductionSourceId is null &&
                    previousProductionSourceId == productionChoice?.Id && previousProductionDatabase is not null
                    ? previousProductionDatabase : preferredProductionSourceId is null &&
                    DashboardDatabaseComparisonService.MatchesProductionSelection(saved, productionChoice)
                    ? saved?.DatabaseName ?? "" : productionChoice?.DefaultDatabase ?? "";
            }
            finally { _changingSelection = false; }

            if (local.Length == 0)
                StatusText.Text = discoveredLocal.Count == 0
                    ? "No matching Local connection for this API and verified database name. Save its Local database connection in API configuration, then refresh."
                    : "Local connection no longer matches the running API's applied connection. Restart the API with its current Local configuration, then refresh.";
            else if (localChoice is null)
                StatusText.Text = "Multiple Local connections match the verified database name. Choose the API connection before comparing.";
            else if (production.Count == 0)
                StatusText.Text = "No deployed connection found. Add a remote PostgreSQL or SQL Server connection, or configure this API's Prod database settings.";
            else if (saved is null)
                StatusText.Text = "Choose both databases, then save this API's comparison set.";
            else if (!savedProductionAvailable)
                StatusText.Text = "Saved deployed connection is unavailable or its endpoint changed. Choose and save a comparison set again.";
            else if (preferredProductionSourceId is not null && productionChoice?.Id == preferredProductionSourceId)
                StatusText.Text = "New deployed connection selected. Save comparison set to keep this pair.";
            else if (!DashboardDatabaseComparisonService.MatchesProductionSelection(saved, productionChoice))
                StatusText.Text = "Current choices differ from the saved set. Save them to replace it, or restore the saved set.";
            else if (savedLocalSourceId is null)
                StatusText.Text = "Deployed target saved. Choose the local connection and save the comparison set to show its status in the card header.";
            else if (!savedLocalAvailable)
                StatusText.Text = "Saved local connection is unavailable or no longer matches this API run. Save a new comparison set after checking Local configuration.";
            else if (localChoice?.Id != savedLocalSourceId ||
                ProductionDatabasePicker.Text.Trim() != saved.DatabaseName)
                StatusText.Text = "Current choices differ from the saved set. Save them to replace it, or restore the saved set.";
            else
                StatusText.Text = $"Saved comparison set: local API / {knownName} and deployed / {saved.DatabaseName}.";

            if (current.Service.VerifiedDatabaseName is null)
                StatusText.Text = "Local API health is unverified. Start or recover the managed Local API to check or save the comparison set.";
            if (!string.IsNullOrWhiteSpace(discovery.Notice)) StatusText.Text += " " + discovery.Notice;
            if (current.Service.HasSavedDatabaseComparisonSet)
            {
                if (current.Service.VerifiedDatabaseName is null)
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · waiting for local API health");
                else if (!savedLocalAvailable)
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · local connection unavailable");
                else if (!savedProductionAvailable)
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · deployed connection unavailable");
                else if (preferredProductionSourceId is null)
                {
                    current.Service.SetDatabaseComparisonHeader("Checking saved local and deployed schemas…");
                    shouldCompareSavedSet = IsSavedSetSelected(current.Service);
                    shouldCheckSavedSetHeader = !shouldCompareSavedSet;
                }
            }
            UpdateButtons();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!request.IsCancellationRequested && refreshGeneration == _refreshGeneration && IsLoaded &&
                ReferenceEquals(DataContext, current.Service))
            {
                StatusText.Text = "Connection discovery failed. Review API settings and saved connections, then refresh.";
                if (current.Service.HasSavedDatabaseComparisonSet)
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · connection check failed");
                UpdateButtons();
            }
        }
        finally
        {
            if (ReferenceEquals(_discoveryRequest, request)) _discoveryRequest = null;
            UpdateButtons();
        }
        if (shouldCompareSavedSet && !request.IsCancellationRequested && refreshGeneration == _refreshGeneration)
            await CompareAsync(automatic: true, refreshGeneration);
        else if (shouldCheckSavedSetHeader && !request.IsCancellationRequested && refreshGeneration == _refreshGeneration)
            await CompareSavedSetHeaderAsync(refreshGeneration);
    }

    internal async Task RefreshSavedComparisonOnExpandAsync()
    {
        if (!IsLoaded || DataContext is not ServiceViewModel { HasSavedDatabaseComparisonSet: true } service ||
            _discoveryRequest is not null || _comparisonRequest is not null) return;
        if (IsSavedSetSelected(service)) await RefreshConnectionsAsync();
        else await CompareSavedSetHeaderAsync();
    }

    private bool IsSavedSetSelected(ServiceViewModel service) =>
        LocalConnectionPicker.SelectedItem is DatabaseConnectionSource local &&
        service.Profile.ComparisonLocalSourceId == local.Id &&
        DashboardDatabaseComparisonService.MatchesProductionSelection(
            service.Profile.ProductionDatabase, ProductionConnectionPicker.SelectedItem as DatabaseConnectionSource) &&
        service.Profile.ProductionDatabase?.DatabaseName == ProductionDatabasePicker.Text.Trim();

    private async Task CompareSavedSetHeaderAsync(int? refreshGeneration = null)
    {
        await _comparisonGate.WaitAsync();
        try
        {
            if (!IsLoaded || refreshGeneration is { } generation && generation != _refreshGeneration ||
                Context() is not { } current ||
                current.Service.Profile.ComparisonLocalSourceId is not { } localSourceId ||
                current.Service.Profile.ProductionDatabase is not { } deployed ||
                current.Service.VerifiedDatabaseName is not { } verifiedName) return;

            using var request = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            _comparisonRequest = request;
            var checkGeneration = _refreshGeneration;
            var runVersion = current.Service.Runner.ManagedApiRunVersion;
            var deployedSourceId = deployed.SourceId;
            var deployedDatabaseName = deployed.DatabaseName;
            bool IsCurrentSavedSet() => checkGeneration == _refreshGeneration && IsLoaded &&
                ReferenceEquals(DataContext, current.Service) &&
                ReferenceEquals(current.Host.ProjectForDatabaseCard(current.Service), current.Project) &&
                current.Service.Runner.ManagedApiRunVersion == runVersion &&
                current.Service.VerifiedDatabaseName == verifiedName &&
                current.Service.Profile.ComparisonLocalSourceId == localSourceId &&
                current.Service.Profile.ProductionDatabase?.SourceId == deployedSourceId &&
                current.Service.Profile.ProductionDatabase?.DatabaseName == deployedDatabaseName;
            current.Service.SetDatabaseComparisonHeader("Checking saved local and deployed schemas…");
            UpdateButtons();
            try
            {
                var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(
                    current.Host.DatabaseProjectsSnapshot, current.Host.DatabaseSettingsStore), request.Token);
                request.Token.ThrowIfCancellationRequested();
                var localMatches = discovery.Sources.Where(source => source.Id == localSourceId).ToArray();
                if (localMatches.Length != 1 ||
                    !current.Service.Runner.MatchesAppliedLocalDatabaseConnection(localMatches[0]))
                {
                    if (IsCurrentSavedSet())
                        current.Service.SetDatabaseComparisonHeader("Saved comparison set · local connection unavailable");
                    return;
                }

                var result = await DashboardDatabaseComparisonService.CompareAsync(
                    current.Project.Id, current.Service.Profile.Id, verifiedName, localMatches[0], deployed,
                    discovery.Sources, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!IsCurrentSavedSet() ||
                    !current.Service.Runner.MatchesAppliedLocalDatabaseConnection(localMatches[0])) return;

                current.Service.SetDatabaseComparisonHeader(
                    DatabaseComparisonPresentation.Format(result).Header, result.ComparedAtUtc);
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentSavedSet())
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · check canceled or timed out");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsCurrentSavedSet())
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · schema check failed");
            }
            finally
            {
                if (ReferenceEquals(_comparisonRequest, request)) _comparisonRequest = null;
                UpdateButtons();
            }
        }
        finally { _comparisonGate.Release(); }
    }

    private void Endpoint_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection) return;
        if (ReferenceEquals(sender, ProductionConnectionPicker) && ProductionDatabasePicker is not null)
        {
            _changingSelection = true;
            try
            {
                ProductionDatabasePicker.ItemsSource = null;
                ProductionDatabasePicker.Text = (ProductionConnectionPicker.SelectedItem as DatabaseConnectionSource)?.DefaultDatabase ?? "";
            }
            finally { _changingSelection = false; }
        }
        ClearComparison();
        StatusText.Text = "Selection changed. Compare checks these choices; Save comparison set keeps the pair for this API.";
        UpdateButtons();
    }

    private DatabaseSelection? SelectedProduction()
    {
        if (ProductionConnectionPicker.SelectedItem is not DatabaseConnectionSource source ||
            string.IsNullOrWhiteSpace(ProductionDatabasePicker.Text)) return null;
        return DashboardDatabaseComparisonService.CreateProductionSelection(source,
            ProductionDatabasePicker.Text.Trim());
    }

    private bool SaveComparisonSet(out DatabaseConnectionSource? local, out DatabaseSelection? selection)
    {
        local = LocalConnectionPicker.SelectedItem as DatabaseConnectionSource;
        selection = SelectedProduction();
        if (local is null || selection is null || Context() is not { } current ||
            current.Service.LastVerifiedDatabaseName is not { } verifiedName)
        {
            StatusText.Text = "Choose the verified local connection and deployed database first.";
            return false;
        }
        var locals = DashboardDatabaseComparisonService.GetLocalCandidates(
            _sources, current.Project.Id, current.Service.Profile.Id, verifiedName);
        var chosenLocalId = local.Id;
        if (locals.Count(source => source.Id == chosenLocalId) != 1 ||
            !current.Service.Runner.MatchesAppliedLocalDatabaseConnection(local))
        {
            StatusText.Text = "Local connection no longer matches the running API. Refresh connections before saving.";
            return false;
        }
        var candidates = DashboardDatabaseComparisonService.GetProductionCandidates(_sources, current.Project.Id);
        var chosen = selection;
        if (candidates.Count(source => DashboardDatabaseComparisonService.MatchesProductionSelection(chosen, source)) != 1)
        {
            StatusText.Text = "This deployed connection is unavailable. Refresh connections and choose another.";
            return false;
        }
        if (ProductionConnectionPicker.SelectedItem is not DatabaseConnectionSource production || production.Provider != local.Provider)
        {
            StatusText.Text = "Choose a deployed database using the same database type as the local database.";
            return false;
        }
        if (!current.Host.TrySaveDatabaseComparisonSet(
            current.Project, current.Service.Profile, local.Id, selection, out var error))
        {
            StatusText.Text = error;
            return false;
        }
        return true;
    }

    private async void SaveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveComparisonSet(out var local, out var selection) || Context() is not { } current) return;
        ClearComparison();
        StatusText.Text = $"Comparison set saved: local / {current.Service.LastVerifiedDatabaseName} and deployed / {selection!.DatabaseName}.";
        current.Service.SetDatabaseComparisonHeader(current.Service.VerifiedDatabaseName is null
            ? "Saved comparison set · waiting for local API health"
            : "Checking saved local and deployed schemas…");
        if (current.Service.VerifiedDatabaseName is not null && local is not null)
            await CompareAsync(automatic: true);
    }

    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync(automatic: false);

    private async Task CompareAsync(bool automatic, int? refreshGeneration = null)
    {
        if (automatic)
            await _comparisonGate.WaitAsync();
        else if (!_comparisonGate.Wait(0))
            return;
        try
        {
            if (!IsLoaded || refreshGeneration is { } generation && generation != _refreshGeneration) return;
            await CompareCoreAsync(automatic);
        }
        finally { _comparisonGate.Release(); }
    }

    private async Task CompareCoreAsync(bool automatic)
    {
        if (_comparisonRequest is not null || Context() is not { } current ||
            LocalConnectionPicker.SelectedItem is not DatabaseConnectionSource local ||
            current.Service.VerifiedDatabaseName is not { } verifiedName ||
            SelectedProduction() is not { } production) return;
        var comparesSavedSet = IsSavedSetSelected(current.Service);
        if (automatic && !comparesSavedSet) return;
        if (!current.Service.Runner.MatchesAppliedLocalDatabaseConnection(local))
        {
            StatusText.Text = "Local connection does not match the running API's applied connection. Restart the API, then refresh.";
            if (comparesSavedSet)
                current.Service.SetDatabaseComparisonHeader("Saved comparison set · local connection changed");
            return;
        }

        _discoveryRequest?.Cancel();
        ClearComparison();
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        _comparisonRequest = request;
        var runVersion = current.Service.Runner.ManagedApiRunVersion;
        var comparisonGeneration = _refreshGeneration;
        StatusText.Text = "Reading local and deployed schema metadata in read-only snapshots…";
        if (comparesSavedSet)
            current.Service.SetDatabaseComparisonHeader("Checking saved local and deployed schemas…");
        UpdateButtons();
        try
        {
            // Fresh discovery resolves saved source IDs again before any network read.
            var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(
                current.Host.DatabaseProjectsSnapshot, current.Host.DatabaseSettingsStore), request.Token);
            request.Token.ThrowIfCancellationRequested();
            var freshLocalMatches = discovery.Sources.Where(source => source.Id == local.Id).ToArray();
            if (freshLocalMatches.Length != 1 ||
                !current.Service.Runner.MatchesAppliedLocalDatabaseConnection(freshLocalMatches[0]))
            {
                if (comparisonGeneration == _refreshGeneration && IsLoaded &&
                    ReferenceEquals(DataContext, current.Service))
                {
                    StatusText.Text = "Saved Local connection changed after the API started. Restart the API with current Local settings, then compare.";
                    if (comparesSavedSet && IsSavedSetSelected(current.Service))
                        current.Service.SetDatabaseComparisonHeader("Saved comparison set · local connection changed");
                }
                return;
            }
            var freshLocal = freshLocalMatches[0];
            var result = await DashboardDatabaseComparisonService.CompareAsync(
                current.Project.Id, current.Service.Profile.Id, verifiedName, freshLocal, production,
                discovery.Sources, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || !ReferenceEquals(DataContext, current.Service)) return;
            if (!ReferenceEquals(current.Host.ProjectForDatabaseCard(current.Service), current.Project) ||
                current.Service.VerifiedDatabaseName != verifiedName ||
                current.Service.Runner.ManagedApiRunVersion != runVersion ||
                !current.Service.Runner.MatchesAppliedLocalDatabaseConnection(freshLocal) ||
                (LocalConnectionPicker.SelectedItem as DatabaseConnectionSource)?.Id != local.Id ||
                !DashboardDatabaseComparisonService.MatchesProductionSelection(
                    production, ProductionConnectionPicker.SelectedItem as DatabaseConnectionSource) ||
                ProductionDatabasePicker.Text.Trim() != production.DatabaseName)
            {
                StatusText.Text = "The API or production selection changed during comparison. Compare again.";
                if (comparesSavedSet && comparisonGeneration == _refreshGeneration &&
                    IsSavedSetSelected(current.Service))
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · check needs refresh");
                return;
            }

            var presentation = DatabaseComparisonPresentation.Format(result);
            ComparisonVerdict.Text = presentation.Header;
            SchemaSummary.Text = presentation.SchemaSummary;
            MigrationSummary.Text = presentation.MigrationSummary;
            ComparedAt.Text = $"Checked {result.ComparedAtUtc.ToLocalTime():g} · Local: {result.LocalDatabase} on {result.LocalServer} · Deployed: {result.ProductionDatabase} on {result.ProductionServer}";
            var saved = current.Service.Profile.ProductionDatabase;
            var targetSource = ProductionConnectionPicker.SelectedItem as DatabaseConnectionSource;
            var isSaved = current.Service.Profile.ComparisonLocalSourceId == local.Id &&
                DashboardDatabaseComparisonService.MatchesProductionSelection(saved, targetSource) &&
                saved?.DatabaseName == production.DatabaseName;
            if (isSaved) current.Service.SetDatabaseComparisonHeader(presentation.Header, result.ComparedAtUtc);
            StatusText.Text = (isSaved ? "" : "This pair is not saved. Use Save comparison set to show its status in the card header." + Environment.NewLine) +
                string.Join(Environment.NewLine, result.Warnings);
        }
        catch (OperationCanceledException)
        {
            if (comparisonGeneration == _refreshGeneration && IsLoaded &&
                ReferenceEquals(DataContext, current.Service))
            {
                StatusText.Text = "Schema comparison canceled or timed out. No partial result was shown.";
                if (comparesSavedSet && current.Service.Runner.ManagedApiRunVersion == runVersion &&
                    current.Service.VerifiedDatabaseName == verifiedName && IsSavedSetSelected(current.Service))
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · check canceled or timed out");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (comparisonGeneration == _refreshGeneration && IsLoaded &&
                ReferenceEquals(DataContext, current.Service))
            {
                StatusText.Text = ex is InvalidOperationException ? ex.Message : DatabaseSchemaComparer.DescribeError(ex);
                if (comparesSavedSet && current.Service.Runner.ManagedApiRunVersion == runVersion &&
                    current.Service.VerifiedDatabaseName == verifiedName && IsSavedSetSelected(current.Service))
                    current.Service.SetDatabaseComparisonHeader("Saved comparison set · schema check failed");
            }
        }
        finally
        {
            if (ReferenceEquals(_comparisonRequest, request)) _comparisonRequest = null;
            UpdateButtons();
        }
    }

    private async void ListDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (ProductionConnectionPicker.SelectedItem is not DatabaseConnectionSource source || _databaseListRequest is not null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _databaseListRequest = request;
        StatusText.Text = "Listing databases visible to the selected production role…";
        UpdateButtons();
        try
        {
            var catalog = await DatabaseSchemaReader.ReadDatabasesAsync(source, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || !ReferenceEquals(ProductionConnectionPicker.SelectedItem, source)) return;
            var typedName = ProductionDatabasePicker.Text;
            _changingSelection = true;
            try
            {
                ProductionDatabasePicker.ItemsSource = catalog.Databases;
                ProductionDatabasePicker.Text = string.IsNullOrWhiteSpace(typedName) ? catalog.CurrentDatabase : typedName;
            }
            finally { _changingSelection = false; }
            StatusText.Text = catalog.Warning ?? $"{catalog.Databases.Count} databases visible to the selected production role.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Database listing canceled or timed out."; }
        catch (Exception ex) { StatusText.Text = DatabaseSchemaReader.DescribeError(ex); }
        finally
        {
            if (ReferenceEquals(_databaseListRequest, request)) _databaseListRequest = null;
            UpdateButtons();
        }
    }

    private async void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        if (Context() is not { } current) return;
        var dialog = new DatabaseConnectionWindow(current.Host.DatabaseSettingsStore) { Owner = current.Host };
        if (dialog.ShowDialog() != true || dialog.Result is not { } source) return;
        await RefreshConnectionsAsync(source.Id);
        if (!source.IsRemoteOnly)
            StatusText.Text = "Connection saved. Choose a remote connection using the same database type as the production target.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshConnectionsAsync(preserveCurrentChoice: true);

    private async void RestoreSavedSet_Click(object sender, RoutedEventArgs e) =>
        await RefreshConnectionsAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _comparisonRequest?.Cancel();

    private void ClearComparison()
    {
        if (ComparisonVerdict is null) return;
        ComparisonVerdict.Text = "No current schema comparison.";
        SchemaSummary.Text = "";
        MigrationSummary.Text = "";
        ComparedAt.Text = "";
    }

    private void UpdateButtons()
    {
        if (CompareButton is null) return;
        var healthy = Context()?.Service.VerifiedDatabaseName is not null;
        var comparing = _comparisonRequest is not null;
        var busy = comparing || _discoveryRequest is not null;
        var sameProvider = LocalConnectionPicker.SelectedItem is DatabaseConnectionSource local &&
            ProductionConnectionPicker.SelectedItem is DatabaseConnectionSource production && local.Provider == production.Provider;
        SaveTargetButton.IsEnabled = healthy && !busy && sameProvider &&
            LocalConnectionPicker.SelectedItem is DatabaseConnectionSource selectedLocal &&
            Context()?.Service.Runner.MatchesAppliedLocalDatabaseConnection(selectedLocal) == true &&
            SelectedProduction() is not null;
        CompareButton.IsEnabled = healthy && !busy && sameProvider && LocalConnectionPicker.SelectedItem is DatabaseConnectionSource &&
            SelectedProduction() is not null;
        CompareButton.ToolTip = SaveTargetButton.ToolTip = "Local and deployed databases must use the same database type.";
        RefreshButton.IsEnabled = !busy;
        RestoreSavedSetButton.Visibility = Context()?.Service is { HasSavedDatabaseComparisonSet: true } service &&
            !IsSavedSetSelected(service)
            ? Visibility.Visible : Visibility.Collapsed;
        RestoreSavedSetButton.IsEnabled = healthy && !busy;
        LocalConnectionPicker.IsEnabled = !busy;
        ProductionConnectionPicker.IsEnabled = !busy;
        ProductionDatabasePicker.IsEnabled = !busy;
        AddConnectionButton.IsEnabled = !busy;
        ListDatabasesButton.IsEnabled = !busy && _databaseListRequest is null &&
            ProductionConnectionPicker.SelectedItem is DatabaseConnectionSource;
        CancelButton.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelRequests()
    {
        _discoveryRequest?.Cancel();
        _databaseListRequest?.Cancel();
        _comparisonRequest?.Cancel();
    }
}
