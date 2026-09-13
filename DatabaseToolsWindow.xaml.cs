using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class DatabaseToolsWindow : Window
{
    private const string ComparisonMeaning = "The source is your reference; the target is the database you may update. Only in source means the target lacks that object; only in target means the source lacks it. Changed means their catalog definitions differ. Renames appear as two separate objects.\n\nMatching EF migration IDs do not prove schemas match, and schema differences do not generate a migration automatically. The migration plan uses the selected repository's existing EF migrations. Row data, sequence counters, server configuration and cloud infrastructure are outside this comparison. Compare again after deployment to review remaining drift.\n\nEach database is read in its own consistent snapshot. The two captures are not an atomic cross-server snapshot. Catalog visibility and PostgreSQL versions can affect results. Definitions may include comments or routine bodies; exports are explicit and stay wherever you save them.";
    private readonly ProjectProfile? _project;
    private readonly ProjectProfile[] _projects;
    private readonly SettingsStore _store;
    private readonly DatabaseConnectionSource? _initialSource;
    private readonly string? _initialDatabase;
    private DatabaseSchemaComparison? _comparison;
    private DatabaseMigrationPlan? _plan;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _initializing = true;
    private bool _applying;

    public DatabaseToolsWindow(ProjectProfile? project, IEnumerable<ProjectProfile> projects, SettingsStore store,
        DatabaseConnectionSource? source, string? database)
    {
        _project = project; _projects = projects.ToArray(); _store = store;
        _initialSource = source; _initialDatabase = database;
        InitializeComponent();
        CoverageText.Text = ComparisonMeaning;
        SourceEndpoint.EndpointChanged += Endpoint_Changed;
        TargetEndpoint.EndpointChanged += Endpoint_Changed;
        ProjectPicker.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!_initializing) { ClearPlan(); UpdateControls(); }
        }));
        Loaded += async (_, _) => await ReloadAsync(true);
        _initializing = false;
    }

    private async Task ReloadAsync(bool initial = false)
    {
        await RunAsync("Finding configured connections and migration projects…", async token =>
        {
            var oldSource = initial ? _initialSource : SourceEndpoint.Source;
            var oldTarget = initial ? null : TargetEndpoint.Source;
            var oldSourceDatabase = initial ? _initialDatabase : SourceEndpoint.Database;
            var oldTargetDatabase = TargetEndpoint.Database;
            var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(_projects, _store), token);
            token.ThrowIfCancellationRequested();
            var sources = discovery.Sources.OrderByDescending(x => x.ProjectId == _project?.Id).ThenBy(x => x.Label).ToList();
            foreach (var temporary in SourceEndpoint.Sources.Concat(TargetEndpoint.Sources).Where(s => s.Id.StartsWith("temporary/", StringComparison.Ordinal)))
                if (sources.All(s => s.Id != temporary.Id)) sources.Add(temporary);
            var source = sources.FirstOrDefault(s => s.Id == oldSource?.Id)
                ?? (initial ? sources.FirstOrDefault(s => s.ProjectId == _project?.Id && s.Id.Contains("/local/", StringComparison.Ordinal)) ?? sources.FirstOrDefault() : null);
            var target = sources.FirstOrDefault(s => s.Id == oldTarget?.Id)
                ?? (initial ? sources.FirstOrDefault(s => s.ProjectId == _project?.Id && s.Id != source?.Id && s.Id.Contains("/prod/", StringComparison.Ordinal)) : null);
            _initializing = true;
            try
            {
                SourceEndpoint.Initialize("1 · Source / reference", sources, source, oldSourceDatabase, _store);
                TargetEndpoint.Initialize("2 · Target / deployment", sources, target, initial ? null : oldTargetDatabase, _store);
                if (initial)
                {
                    var folders = (_project is null ? _projects : new[] { _project }).SelectMany(p => p.Services.Select(s => _store.ResolveWorkingDirectory(p, s))).ToArray();
                    var projects = await Task.Run(() => DatabaseMigrationService.DiscoverProjects(folders), token);
                    ProjectPicker.ItemsSource = projects.Select(p => p.ProjectPath).ToArray();
                    ProjectPicker.SelectedIndex = projects.Count > 0 ? 0 : -1;
                }
            }
            finally { _initializing = false; }
            ClearComparison(); ClearPlan();
            StatusText.Text = string.IsNullOrWhiteSpace(discovery.Notice)
                ? $"{sources.Count} configured connections. Verify both hosts and database names before comparing."
                : discovery.Notice;
            if (source is null || target is null) StatusText.Text += " Choose the missing connection or use Add connection to connect and save one.";
        });
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Endpoint_Changed(object? sender, EventArgs e)
    {
        if (_initializing) return;
        if (SourceEndpoint.Source is { } source) TargetEndpoint.AddSource(source);
        if (TargetEndpoint.Source is { } target) SourceEndpoint.AddSource(target);
        ClearComparison(); ClearPlan();
        StatusText.Text = "Database selection changed. Compare or prepare a new migration plan.";
        UpdateControls();
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceEndpoint.Source; var database = SourceEndpoint.Database;
        _initializing = true;
        try { SourceEndpoint.Select(TargetEndpoint.Source, TargetEndpoint.Database); TargetEndpoint.Select(source, database); }
        finally { _initializing = false; }
        Endpoint_Changed(this, EventArgs.Empty);
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        ClearComparison();
        await RunAsync("Reading both schemas in read-only snapshots…", CompareCoreAsync);
    }

    private async Task CompareCoreAsync(CancellationToken token)
    {
        if (SourceEndpoint.Source is not { } source || TargetEndpoint.Source is not { } target) return;
        var sourceTask = PostgresSchemaComparer.CaptureAsync(source, SourceEndpoint.Database, token);
        var targetTask = PostgresSchemaComparer.CaptureAsync(target, TargetEndpoint.Database, token);
        var snapshots = await Task.WhenAll(sourceTask, targetTask);
        token.ThrowIfCancellationRequested();
        _comparison = PostgresSchemaComparer.Compare(snapshots[0], snapshots[1]);
        var comparison = _comparison;
        var sameEndpoint = string.Equals(source.Server, target.Server, StringComparison.OrdinalIgnoreCase) && SourceEndpoint.Database == TargetEndpoint.Database;
        var differences = comparison.Differences;
        ComparisonSummary.Text = $"{differences.Count} differences · {differences.Count(d => d.Change == DatabaseSchemaChange.SourceOnly)} only in source · {differences.Count(d => d.Change == DatabaseSchemaChange.TargetOnly)} only in target · {differences.Count(d => d.Change == DatabaseSchemaChange.Changed)} changed · {comparison.UnchangedCount} matching objects\nSource: {SourceEndpoint.Identity}  →  Target: {TargetEndpoint.Identity}";
        if (sameEndpoint) ComparisonSummary.Text += "\nBoth selections name the same host and database; this does not verify a separate production database.";
        if (comparison.Source.ServerVersion.Split('.')[0] != comparison.Target.ServerVersion.Split('.')[0])
            ComparisonSummary.Text += "\nPostgreSQL major versions differ; some definitions may be formatted differently.";
        var warnings = comparison.Source.Warnings.Select(w => "Source: " + w).Concat(comparison.Target.Warnings.Select(w => "Target: " + w)).ToList();
        if (comparison.Source.MigrationHistory.Warning is { } sw) warnings.Add("Source history: " + sw);
        if (comparison.Target.MigrationHistory.Warning is { } tw) warnings.Add("Target history: " + tw);
        if (warnings.Count > 0) ComparisonSummary.Text += "\nCoverage notices are available in Coverage & results.";
        CoverageText.Text = ComparisonMeaning + $"\n\nSource: PostgreSQL {comparison.Source.ServerVersion}, captured {comparison.Source.CapturedAtUtc.LocalDateTime:g}\nTarget: PostgreSQL {comparison.Target.ServerVersion}, captured {comparison.Target.CapturedAtUtc.LocalDateTime:g}\n\n" + string.Join("\n", comparison.Source.Coverage.Union(comparison.Target.Coverage)) + "\n\n" + string.Join("\n", warnings);
        FilterDifferences(); UpdateHistory();
        StatusText.Text = differences.Count == 0
            ? "No differences found within the captured catalog coverage. Review migration history and coverage notices."
            : "Comparison complete. Select a difference to inspect both definitions; use Migrations & SQL to check repository changes.";
    }

    private void FilterDifferences()
    {
        if (DifferenceGrid is null || Search is null || ChangeFilter is null) return;
        var search = Search.Text.Trim();
        var kind = ChangeFilter.SelectedIndex;
        DifferenceGrid.ItemsSource = _comparison?.Differences.Where(d =>
            (kind == 0 || kind == 1 && d.Change == DatabaseSchemaChange.SourceOnly || kind == 2 && d.Change == DatabaseSchemaChange.TargetOnly || kind == 3 && d.Change == DatabaseSchemaChange.Changed)
            && (search.Length == 0 || $"{d.Kind} {d.Schema} {d.Name}".Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (DifferenceGrid.Items.Count > 0) DifferenceGrid.SelectedIndex = 0;
        else { SourceDefinition.Clear(); TargetDefinition.Clear(); }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => FilterDifferences();
    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterDifferences();
    private void Difference_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DifferenceGrid.SelectedItem is not DatabaseSchemaDifference difference) return;
        SourceDefinition.Text = difference.SourceDefinition ?? "Object absent from source snapshot.";
        TargetDefinition.Text = difference.TargetDefinition ?? "Object absent from target snapshot.";
    }

    private void ClearComparison()
    {
        _comparison = null;
        DifferenceGrid.ItemsSource = null; SourceDefinition.Clear(); TargetDefinition.Clear();
        ComparisonSummary.Text = "Compare the current selections to see schema differences.";
        CoverageText.Text = ComparisonMeaning;
        UpdateHistory();
    }

    private void ClearPlan()
    {
        _plan = null;
        SqlPreview.Clear(); SqlHeading.Text = "REVIEW SQL";
        BackupCheck.IsChecked = false; TargetConfirmation.Clear();
        ConfirmationLabel.Text = "Prepare a new plan to review and confirm the exact target.";
        UpdateHistory();
    }

    private void UpdateHistory()
    {
        if (MigrationGrid is null) return;
        if (_plan is { } plan && HistorySide.SelectedIndex == 0)
        {
            MigrationGrid.ItemsSource = plan.RepositoryMigrations.Union(plan.AppliedMigrations).Order(StringComparer.Ordinal)
                .Select(id => new MigrationRow(id, plan.RepositoryMigrations.Contains(id) ? "In repository" : "Unknown to repository", plan.AppliedMigrations.Contains(id) ? "Applied" : "Pending")).ToArray();
            MigrationSummary.Text = plan.Summary;
        }
        else if (_comparison is { } comparison)
        {
            var source = comparison.Source.MigrationHistory; var target = comparison.Target.MigrationHistory;
            MigrationGrid.ItemsSource = source.MigrationIds.Union(target.MigrationIds).Order(StringComparer.Ordinal)
                .Select(id => new MigrationRow(id, !source.IsAvailable ? "History unavailable" : source.MigrationIds.Contains(id) ? "Applied" : "Not recorded", !target.IsAvailable ? "History unavailable" : target.MigrationIds.Contains(id) ? "Applied" : "Not recorded")).ToArray();
            MigrationSummary.Text = !comparison.CanCompareMigrationHistory
                ? "Migration history could not be compared completely. Review coverage notices; do not interpret unavailable history as pending migrations."
                : $"Database history: {comparison.MigrationIdsOnlyInSource.Count} IDs recorded only in source; {comparison.MigrationIdsOnlyInTarget.Count} only in target. Prepare a plan to verify the target against the repository.";
        }
        else { MigrationGrid.ItemsSource = null; MigrationSummary.Text = "Compare databases for their recorded history, or prepare a repository migration plan for the target."; }
    }

    private void HistorySide_Changed(object sender, SelectionChangedEventArgs e) => UpdateHistory();

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (TargetEndpoint.Source is not { } target) return;
        var projectPath = ProjectPicker.Text.Trim();
        ClearPlan();
        WorkspaceTabs.SelectedItem = MigrationTab;
        await RunAsync("Preparing a migration plan for the selected target…", async token =>
        {
            var plan = await DatabaseMigrationService.PrepareAsync(projectPath, target, TargetEndpoint.Database, Progress(), token);
            token.ThrowIfCancellationRequested();
            _plan = plan;
            SqlPreview.Text = plan.Sql;
            SqlHeading.Text = string.IsNullOrEmpty(plan.SqlSha256) ? "REVIEW SQL" : $"SQL SHA-256 · {plan.SqlSha256}";
            ConfirmationLabel.Text = $"Target: {plan.TargetLabel}\nType exactly: {plan.ConfirmationText}";
            HistorySide.SelectedIndex = 0;
            UpdateHistory();
            StatusText.Text = plan.Summary;
            Record($"Plan prepared for {plan.TargetLabel}: {plan.Summary}");
        });
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { CanApply: true } plan || TargetEndpoint.Source is not { } target || !Confirmed()) return;
        var typedTarget = TargetConfirmation.Text;
        _applying = true;
        // Retire the review for every attempted application, including canceled/uncertain outcomes.
        _plan = null;
        ClearComparison();
        await RunAsync($"Rechecking {plan.TargetLabel} before applying reviewed migrations…", async token =>
        {
            var result = await DatabaseMigrationService.ApplyAsync(plan, target, TargetEndpoint.Database, typedTarget, true, Progress(), token);
            Record(result.Message);
            StatusText.Text = result.Message;
            MigrationSummary.Text = result.Message;
            if (SourceEndpoint.IsReady && TargetEndpoint.IsReady)
            {
                try { await CompareCoreAsync(token); StatusText.Text = result.Message + " " + StatusText.Text; }
                catch (Exception ex)
                {
                    StatusText.Text = result.Message + " Post-apply comparison did not finish. Compare again to verify remaining differences. " + PostgresSchemaReader.DescribeError(ex);
                    Record(StatusText.Text);
                }
            }
        });
        _applying = false;
        ClearPlan();
        UpdateControls();
    }

    private IProgress<string> Progress() => new Progress<string>(message => { if (_busy) StatusText.Text = message; });

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation; _busy = true;
        StatusText.Text = message; UpdateControls();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            StatusText.Text = _applying ? "Migration application was interrupted. Prepare a fresh plan and verify target history before retrying; completion is not assumed." : "Operation canceled. No successful result was recorded.";
            Record(StatusText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is InvalidOperationException
                ? PostgresSchemaComparer.DescribeError(ex)
                : DatabaseMigrationService.DescribeError(ex);
            if (_applying) StatusText.Text += " Prepare a fresh plan and verify the target before retrying.";
            Record(StatusText.Text);
        }
        finally { _busy = false; _operation = null; UpdateControls(); }
    }

    private void UpdateControls()
    {
        if (ApplyButton is null) return;
        Endpoints.IsEnabled = !_busy; MigrationInputs.IsEnabled = !_busy;
        CompareButton.IsEnabled = !_busy && SourceEndpoint.IsReady && TargetEndpoint.IsReady;
        PrepareButton.IsEnabled = !_busy && TargetEndpoint.IsReady && !string.IsNullOrWhiteSpace(ProjectPicker.Text);
        ReloadButton.IsEnabled = !_busy; SwapButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = _busy;
        ApplyButton.IsEnabled = !_busy && !_applying && _plan is { CanApply: true } && Confirmed();
        BackupCheck.IsEnabled = !_busy && _plan is { CanApply: true };
        TargetConfirmation.IsEnabled = BackupCheck.IsEnabled;
        ExportComparisonButton.IsEnabled = !_busy && _comparison is not null;
        ExportSqlButton.IsEnabled = !_busy && _plan is { Sql.Length: > 0 };
    }

    private bool Confirmed() => _plan is { } plan && BackupCheck.IsChecked == true && string.Equals(TargetConfirmation.Text, plan.ConfirmationText, StringComparison.Ordinal);
    private void Confirmation_Changed(object sender, RoutedEventArgs e) => UpdateControls();
    private void ConfirmationText_Changed(object sender, TextChangedEventArgs e) => UpdateControls();
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); StatusText.Text = "Cancellation requested; waiting for the operation to settle…"; }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        StatusText.Text = "Wait for this operation to finish, or use Cancel operation. Close the window once its outcome is shown.";
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "C# project (*.csproj)|*.csproj", Title = "Select the API project containing EF migrations" };
        if (dialog.ShowDialog(this) == true) ProjectPicker.Text = dialog.FileName;
    }

    private async void ExportComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_comparison is not { } comparison) return;
        var dialog = new SaveFileDialog { Filter = "JSON report (*.json)|*.json", FileName = $"database-comparison-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Saving comparison report…", async token =>
        {
            var content = JsonSerializer.Serialize(new { Description = ComparisonMeaning, Comparison = comparison }, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            await File.WriteAllTextAsync(dialog.FileName, content, new UTF8Encoding(false), token);
            StatusText.Text = "Comparison report saved. It contains schema definitions and migration IDs, with no connection strings or table rows.";
        });
    }

    private async void ExportSql_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || string.IsNullOrEmpty(plan.Sql)) return;
        var dialog = new SaveFileDialog { Filter = "SQL script (*.sql)|*.sql", FileName = $"reviewed-migrations-{DateTime.Now:yyyyMMdd-HHmmss}.sql" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Saving reviewed SQL…", async token =>
        {
            await File.WriteAllTextAsync(dialog.FileName, plan.Sql, new UTF8Encoding(false), token);
            StatusText.Text = "Exact reviewed SQL saved. The launcher's target rechecks and transaction wrapper are provided only when applying through this tool.";
        });
    }

    private void Record(string message)
    {
        OperationResults.Text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {message}\n\n" + OperationResults.Text;
        if (OperationResults.Text.Length > 20000) OperationResults.Text = OperationResults.Text[..20000];
    }

    private sealed record MigrationRow(string Id, string SourceStatus, string TargetStatus);
}
