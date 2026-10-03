using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

/// <summary>One service's cancellable, read-only source/metadata inventory.</summary>
public partial class ApiEndpointsWindow : Window
{
    private CancellationTokenSource? _scanCancellation;
    private ApiEndpointInventory? _inventory;
    private string? _openApiFile;
    private bool _closed;
    private bool _initialized;
    private DateTimeOffset _checkedAt;
    private readonly DispatcherTimer _filterTimer;
    private int _filterVersion;
    private static readonly string[] CommonVerbs = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    public ApiEndpointsWindow(string projectName, string serviceName, string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        InitializeComponent();
        _filterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        UpdateContext(projectName, serviceName);
        SourceText.Text = workingDirectory;
        VerbFilter.ItemsSource = new[] { "All methods" };
        VerbFilter.SelectedIndex = 0;
        _initialized = true;
    }

    internal string WorkingDirectory { get; }
    internal event Action<ApiEndpointInventory?>? InventoryChanged;

    internal void UpdateContext(string projectName, string serviceName)
    {
        Title = $"{serviceName} - API endpoints";
        ServiceHeading.Text = serviceName;
        ProjectHeading.Text = projectName;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void ScanSourceClick(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        _openApiFile = null;
        await ScanAsync();
    }

    private async void OpenApiClick(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        var picker = new OpenFileDialog
        {
            Title = "Choose this API's OpenAPI or Swagger JSON document",
            Filter = "OpenAPI / Swagger JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        _openApiFile = picker.FileName;
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_closed || _scanCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetBusy(true);
        // Retire a previous snapshot before a refresh, so an error never looks like fresh counts.
        _inventory = null;
        InventoryChanged?.Invoke(null);
        CountsText.Text = "";
        VerbsText.Text = "";
        OperationsGrid.ItemsSource = null;
        ControllersGrid.ItemsSource = null;
        SelectedDetails.Text = "";
        WarningsPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        EmptyText.Text = "Reading API metadata…";
        EmptyText.Visibility = Visibility.Visible;
        SourceText.Text = _openApiFile ?? WorkingDirectory;
        SetStatus(_openApiFile is null ? "Scanning attributed controller source…" : "Reading local OpenAPI JSON…");
        try
        {
            var result = _openApiFile is { } file
                ? await ApiEndpointDiscovery.LoadOpenApiAsync(file, cancellation.Token)
                : await ApiEndpointDiscovery.AnalyzeSourceAsync(WorkingDirectory, cancellation.Token);
            var controllerRows = await Task.Run(() => BuildControllerRows(result), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _inventory = result;
            _checkedAt = DateTimeOffset.Now;
            ShowInventory(controllerRows);
            InventoryChanged?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ShowUnavailable("Scan canceled. Refresh to try again.");
        }
        catch (ApiEndpointDiscoveryException ex)
        {
            if (!_closed) ShowUnavailable(ex.Message, isError: true);
        }
        catch (Exception)
        {
            // Source/JSON errors must not echo arbitrary application data or exception payloads.
            if (!_closed) ShowUnavailable("API metadata could not be read. Check the selected folder or JSON file, then refresh.", isError: true);
        }
        finally
        {
            _scanCancellation = null;
            if (!_closed) SetBusy(false);
        }
    }

    private void ShowInventory(ControllerRow[] controllerRows)
    {
        if (_inventory is not { } result) return;
        var source = result.SourceKind == "Source";
        SourceText.Text = $"{result.SourceLabel} · {result.SourcePath}";
        CountsText.Text = $"{result.TotalOperationCount:N0} discovered endpoint operations" +
            (source ? $" · {result.ControllerCount:N0} controllers · {result.ControllerActionCount:N0} declared action methods" : "");
        var counts = result.HttpMethodCounts.ToDictionary(item => item.HttpMethod, item => item.Count, StringComparer.OrdinalIgnoreCase);
        VerbsText.Text = string.Join("   ·   ", CommonVerbs.Concat(counts.Keys.Except(CommonVerbs, StringComparer.OrdinalIgnoreCase))
            .Select(verb => $"{verb} {counts.GetValueOrDefault(verb):N0}"));
        SetStatus($"{(result.IsPartial ? "Partial inventory — review limitations below" : "Inventory read") } · {_checkedAt:t}" +
            (source ? $" · {result.ScannedFileCount:N0} source files" : " · controller/action counts unavailable from OpenAPI"), result.IsPartial);
        WarningsText.Text = string.Join(Environment.NewLine, result.Warnings.Select(warning => "• " + warning));
        WarningsPanel.Visibility = result.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var previousVerb = VerbFilter.SelectedItem as string;
        VerbFilter.ItemsSource = new[] { "All methods" }.Concat(CommonVerbs)
            .Concat(counts.Keys.Except(CommonVerbs, StringComparer.OrdinalIgnoreCase)).ToArray();
        VerbFilter.SelectedItem = previousVerb ?? "All methods";
        if (VerbFilter.SelectedIndex < 0) VerbFilter.SelectedIndex = 0;
        ControllersGrid.ItemsSource = controllerRows;
        CopyButton.IsEnabled = true;
        SelectedDetails.Text = ApiEndpointInventory.ActionCountingSemantics +
            " Controller rows summarize resolved operations and may overlap; their totals need not add to the inventory total. OpenAPI grouping uses the first tag on each operation.";
        ApplyFilter();
    }

    private static ControllerRow[] BuildControllerRows(ApiEndpointInventory result)
    {
        var source = result.SourceKind == "Source";
        return result.Operations.SelectMany(operation => operation.Origins.Select(origin => (operation, origin)))
            .GroupBy(entry => source
                ? string.IsNullOrWhiteSpace(entry.origin.ControllerIdentity ?? entry.origin.Controller) ? "Unattributed" : entry.origin.ControllerIdentity ?? entry.origin.Controller
                : string.IsNullOrWhiteSpace(entry.origin.Tag) ? "Untagged" : entry.origin.Tag)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ControllerRow(group.Key,
                source ? group.Select(entry => (entry.origin.File, entry.origin.DeclarationOffset)).Distinct().Count().ToString("N0") : "n/a",
                group.Select(entry => entry.operation).Distinct().Count(),
                string.Join(" · ", group.Select(entry => entry.operation).Distinct().GroupBy(operation => operation.HttpMethod)
                    .OrderBy(verbs => verbs.Key).Select(verbs => $"{verbs.Key} {verbs.Count():N0}")))).ToArray();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) QueueFilter(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_initialized) QueueFilter(); }

    private void QueueFilter()
    {
        _filterVersion++;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async void ApplyFilter()
    {
        if (_inventory is not { } result) return;
        _filterTimer.Stop();
        var version = ++_filterVersion;
        var verb = VerbFilter.SelectedItem as string;
        var search = SearchText.Text.Trim();
        var visible = await Task.Run(() => result.Operations.Where(operation =>
            (verb is null or "All methods" || operation.HttpMethod.Equals(verb, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 || operation.Route.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             operation.OriginSummary.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray());
        if (_closed || version != _filterVersion || !ReferenceEquals(result, _inventory)) return;
        OperationsGrid.ItemsSource = visible;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = result.TotalOperationCount == 0
            ? "No supported endpoint operations were discovered. Review limitations or choose an OpenAPI JSON file."
            : "No endpoints match these filters.";
    }

    private void OperationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not ApiEndpointOperation operation) return;
        SelectedDetails.Text = $"{operation.HttpMethod} {operation.Route}{Environment.NewLine}{operation.OriginSummary}" +
            (operation.Origins.Count > 1 ? $"{Environment.NewLine}Repeated verb/route declarations were merged; all origins are shown." : "");
    }

    private void SetBusy(bool busy)
    {
        SourceButton.IsEnabled = OpenApiButton.IsEnabled = RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void SetStatus(string text, bool warning = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = warning ? new SolidColorBrush(Color.FromRgb(255, 210, 122)) : (Brush)FindResource("AccentBrush");
    }

    private void ShowUnavailable(string message, bool isError = false)
    {
        SetStatus(message, isError);
        EmptyText.Text = isError ? "Inventory unavailable." : "Scan canceled.";
        EmptyText.Visibility = Visibility.Visible;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        SetStatus("Canceling scan…");
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (_inventory is not { } result) return;
        var text = new StringBuilder().AppendLine($"{ServiceHeading.Text} - API endpoint inventory")
            .AppendLine($"{result.SourceLabel}: {result.SourcePath}")
            .AppendLine($"Read {_checkedAt:g}; {(result.IsPartial ? "partial" : "static metadata inventory")}")
            .AppendLine(CountsText.Text).AppendLine(VerbsText.Text)
            .AppendLine(ApiEndpointInventory.CountingSemantics).AppendLine(ApiEndpointInventory.ActionCountingSemantics);
        foreach (var warning in result.Warnings) text.AppendLine("Limitation: " + warning);
        text.AppendLine().AppendLine("HTTP method\tRoute template\tDeclared by");
        foreach (var operation in result.Operations)
            text.AppendLine($"{operation.HttpMethod}\t{operation.Route}\t{operation.OriginSummary.Replace(Environment.NewLine, "; ")}");
        try { Clipboard.SetText(text.ToString()); SetStatus("Inventory copied."); }
        catch { SetStatus("Clipboard is unavailable. Try copying again.", warning: true); }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _filterTimer.Stop();
        _scanCancellation?.Cancel();
        _inventory = null;
    }

    private sealed record ControllerRow(string Name, string ActionCount, int OperationCount, string Verbs);
}
