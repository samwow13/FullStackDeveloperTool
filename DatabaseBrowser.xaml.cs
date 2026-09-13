using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class DatabaseBrowser : UserControl
{
    private const string CopyHint = "Ctrl+C copies selected cells · SQL NULL is copied as \\N · Empty strings stay empty · Values are limited to 4,096 characters";
    private readonly Dictionary<DataGridColumn, string> _rowColumnKeys = [];
    private SettingsStore? _store;
    private ProjectProfile? _project;
    private ProjectProfile[] _projects = [];
    private IReadOnlyList<DatabaseTable> _tables = [];
    private CancellationTokenSource? _request;
    private bool _updating;
    private bool _connected;
    private double _tablePaneWidth = 205;
    public event Action<ProjectProfile, DatabaseSelection>? DatabaseSelected;
    public event EventHandler? HideRequested;

    private void DatabaseAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        var accounts = new DatabaseAccountsWindow(_project, _projects, _store,
            ConnectionPicker.SelectedItem as DatabaseConnectionSource, DatabasePicker.SelectedItem as string)
        { Owner = Window.GetWindow(this) };
        accounts.ShowDialog();
    }

    private async void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        var dialog = new DatabaseConnectionWindow(_store) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } source) return;
        await RunAsync("Loading the saved database connection…", async token =>
        {
            var sources = (ConnectionPicker.ItemsSource as IEnumerable<DatabaseConnectionSource> ?? [])
                .Where(existing => existing.Id != source.Id).Append(source).ToArray();
            SetSelection(() => { ConnectionPicker.ItemsSource = sources; ConnectionPicker.SelectedItem = source; });
            await LoadDatabasesAsync(source, source.DefaultDatabase, token);
        });
    }

    private void DatabaseTools_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        var tools = new DatabaseToolsWindow(_project, _projects, _store,
            ConnectionPicker.SelectedItem as DatabaseConnectionSource, DatabasePicker.SelectedItem as string)
        { Owner = Window.GetWindow(this) };
        tools.ShowDialog();
    }

    public DatabaseBrowser()
    {
        InitializeComponent();
        // Each grid keeps a useful viewport. Expanded detail text scrolls below it rather than consuming its last rows.
        ColumnGrid.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight))
        {
            Source = ColumnsViewport, Converter = GridViewportConverter.Instance, ConverterParameter = 34d
        });
        RowsGrid.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight))
        {
            Source = RowsViewport, Converter = GridViewportConverter.Instance, ConverterParameter = 70d
        });
        RowsGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopySelectedCells(); e.Handled = true; },
            (_, e) => { e.CanExecute = RowsGrid.SelectedCells.Count > 0; e.Handled = true; }));
    }

    public Task ShowProjectAsync(ProjectProfile? project, IEnumerable<ProjectProfile> projects, SettingsStore store)
    {
        _request?.Cancel();
        _project = project;
        _projects = projects.ToArray();
        _store = store;
        return RefreshConnectionsAsync();
    }

    private Task RefreshConnectionsAsync()
    {
        var project = _project;
        var projects = _projects;
        var store = _store;
        return RunAsync("Finding existing PostgreSQL connections…", async token =>
        {
            SetSelection(() => { ConnectionPicker.ItemsSource = null; DatabasePicker.ItemsSource = null; });
            ServerText.Text = "Choose a connection";
            DatabaseTitle.Text = "PostgreSQL";
            ConnectionSettings.Visibility = Visibility.Visible;
            ConnectionNotice.Text = "";
            ClearTables();
            if (project is null || store is null)
            {
                StatusText.Text = "Select a project to find its database connections.";
                return;
            }
            var discovery = await Task.Run(() => DatabaseConnectionDiscovery.Discover(projects, store), token);
            token.ThrowIfCancellationRequested();
            var sources = discovery.Sources.OrderByDescending(x => x.ProjectId == project.Id).ThenBy(x => x.Label).ToArray();
            SetSelection(() => ConnectionPicker.ItemsSource = sources);
            ConnectionNotice.Text = discovery.Notice;
            if (sources.Length == 0)
            {
                StatusText.Text = "No PostgreSQL connections found. Use Add connection to connect and save one, or configure ConnectionStrings in the project's .NET settings or user-secrets.";
                return;
            }
            var saved = project.Database;
            var source = saved is null ? sources.FirstOrDefault(x => x.ProjectId == project.Id) ?? sources[0]
                : sources.FirstOrDefault(x => x.Id == saved.SourceId);
            if (source is null)
            {
                StatusText.Text = "The saved connection is unavailable. Choose a connection above or restore the project's configuration, then refresh.";
                return;
            }
            SetSelection(() => ConnectionPicker.SelectedItem = source);
            await LoadDatabasesAsync(source, saved?.DatabaseName, token);
        });
    }

    private async Task LoadDatabasesAsync(DatabaseConnectionSource source, string? preferredDatabase, CancellationToken token)
    {
        SetSelection(() => DatabasePicker.ItemsSource = null);
        ClearTables();
        ServerText.Text = source.Server;
        StatusText.Text = "Connecting and loading existing databases…";
        var catalog = await PostgresSchemaReader.ReadDatabasesAsync(source, token);
        token.ThrowIfCancellationRequested();
        if (catalog.Warning is not null) ConnectionNotice.Text = string.Join(" ", ConnectionNotice.Text, catalog.Warning).Trim();
        SetSelection(() => DatabasePicker.ItemsSource = catalog.Databases);
        var database = preferredDatabase ?? catalog.CurrentDatabase;
        if (!catalog.Databases.Contains(database, StringComparer.Ordinal))
        {
            StatusText.Text = $"The saved database '{database}' is no longer available to this role. Choose an existing database above.";
            return;
        }
        SetSelection(() => DatabasePicker.SelectedItem = database);
        await LoadTablesAsync(source, database, token);
    }

    private async Task LoadTablesAsync(DatabaseConnectionSource source, string database, CancellationToken token)
    {
        var previousTable = TableList.SelectedItem as DatabaseTable;
        ClearTables();
        StatusText.Text = $"Loading tables from {database}…";
        var tables = await PostgresSchemaReader.ReadTablesAsync(source, database, token);
        token.ThrowIfCancellationRequested();
        _tables = tables;
        _connected = true;
        DatabaseTitle.Text = database;
        DatabaseTitle.ToolTip = string.Join("\n", source.Label, ConnectionNotice.Text).Trim();
        ServerText.Text = $"{source.Server} · {tables.Count} tables / views · read-only";
        ConnectionSettings.Visibility = Visibility.Collapsed;
        ApplyFilter(previousTable);
        if (_project is { } project) DatabaseSelected?.Invoke(project, new() { SourceId = source.Id, DatabaseName = database });
        if (TableList.SelectedItem is DatabaseTable table) await LoadSelectedViewAsync(source, database, table, token);
        else StatusText.Text = tables.Count == 0
            ? $"Connected to {database}. No user tables or views are visible to this role."
            : $"Connected to {database}. No tables match this filter.";
    }

    private async Task LoadSelectedViewAsync(DatabaseConnectionSource source, string database, DatabaseTable table, CancellationToken token)
    {
        ClearTableViews();
        ColumnsHeading.Text = table.DisplayName;
        ColumnsHeading.ToolTip = table.DisplayName;
        if (RowsTab.IsSelected) await LoadRowsAsync(source, database, table, token);
        else if (RelationshipsTab.IsSelected) await LoadRelationshipsAsync(source, database, table, token);
        else await LoadColumnsAsync(source, database, table, token);
    }

    private async Task LoadColumnsAsync(DatabaseConnectionSource source, string database, DatabaseTable table, CancellationToken token)
    {
        StatusText.Text = $"Loading columns for {table.DisplayName}…";
        var columns = await PostgresSchemaReader.ReadColumnsAsync(source, database, table, token);
        token.ThrowIfCancellationRequested();
        ColumnGrid.ItemsSource = columns;
        ColumnsHeading.Text = $"{table.DisplayName} · {columns.Count} columns";
        if (columns.FirstOrDefault() is { } first)
        {
            ColumnGrid.CurrentCell = new DataGridCellInfo(first, ColumnGrid.Columns[1]);
            ColumnGrid.SelectedCells.Add(ColumnGrid.CurrentCell);
            ShowColumn(first);
        }
        else ColumnDetail.Text = "No columns are visible. The table may have changed or your role may not have column access. Refresh the schema.";
        StatusText.Text = $"{columns.Count} columns · Ctrl+C copies selected cells · refreshed {DateTime.Now:HH:mm:ss}";
    }

    private async Task LoadRowsAsync(DatabaseConnectionSource source, string database, DatabaseTable table, CancellationToken token)
    {
        RowsStatus.Text = "Querying up to ten rows…";
        StatusText.Text = $"Querying up to ten rows from {table.DisplayName}…";
        var preview = await PostgresSchemaReader.ReadTopRowsAsync(source, database, table, token);
        token.ThrowIfCancellationRequested();
        var data = new DataTable();
        for (var i = 0; i < preview.Columns.Count; i++)
        {
            // Binding keys are independent of database identifiers and the user's displayed column order.
            var key = $"Column{i}";
            data.Columns.Add(key, typeof(string));
            var column = new DataGridTextColumn
            {
                Header = preview.Columns[i], SortMemberPath = key,
                Binding = new Binding(key) { Converter = PreviewValueConverter.Instance },
                Width = new DataGridLength(165), MinWidth = 65,
                ElementStyle = (Style)FindResource("GridText")
            };
            _rowColumnKeys.Add(column, key);
            RowsGrid.Columns.Add(column);
        }
        foreach (var row in preview.Rows)
            data.Rows.Add(row.Select(value => (object?)value ?? DBNull.Value).ToArray());
        RowsGrid.ItemsSource = data.DefaultView;
        var rowLabel = preview.Rows.Count == 1 ? "1 row" : $"{preview.Rows.Count} rows";
        RowsStatus.Text = preview.Rows.Count == 0 ? "No rows returned for this table and role." : $"{rowLabel} · {preview.Ordering}";
        RowsStatus.ToolTip = RowsStatus.Text;
        CopyResultsButton.IsEnabled = preview.Rows.Count > 0;
        StatusText.Text = $"{table.DisplayName} · {rowLabel} · {preview.Ordering} · queried {DateTime.Now:HH:mm:ss}";
        if (RowsGrid.Items.Count > 0 && RowsGrid.Columns.Count > 0)
        {
            RowsGrid.CurrentCell = new DataGridCellInfo(RowsGrid.Items[0], RowsGrid.Columns[0]);
            RowsGrid.SelectedCells.Add(RowsGrid.CurrentCell);
            UpdateCellDetail();
        }
    }

    private async Task LoadRelationshipsAsync(DatabaseConnectionSource source, string database, DatabaseTable table, CancellationToken token)
    {
        RelationshipsStatus.Text = "Loading foreign key relationships…";
        StatusText.Text = $"Loading relationships for {table.DisplayName}…";
        var relationships = await PostgresSchemaReader.ReadRelationshipsAsync(source, database, table, token);
        token.ThrowIfCancellationRequested();
        RelationshipsList.ItemsSource = relationships.Select(relationship => new RelationshipView(relationship, table.Id)).ToArray();
        RelationshipContent.Visibility = relationships.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RelationshipEmptyState.Visibility = relationships.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var outgoing = relationships.Count(x => x.SourceTable.Id == table.Id && x.TargetTable.Id != table.Id);
        var incoming = relationships.Count(x => x.TargetTable.Id == table.Id && x.SourceTable.Id != table.Id);
        var self = relationships.Count(x => x.SourceTable.Id == table.Id && x.TargetTable.Id == table.Id);
        RelationshipsStatus.Text = relationships.Count == 0
            ? $"{table.DisplayName} · no relationships found"
            : $"{outgoing} outgoing · {incoming} incoming" + (self == 0 ? "" : $" · {self} self references");
        StatusText.Text = $"{table.DisplayName} · {relationships.Count} foreign keys · refreshed {DateTime.Now:HH:mm:ss}";
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        _request?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _request = request;
        SetBusy(true);
        StatusText.Text = message;
        try { await action(request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (ReferenceEquals(_request, request)) ShowRequestError("Database request canceled or timed out. Refresh to try again.");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_request, request)) ShowRequestError(PostgresSchemaReader.DescribeError(ex));
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
                SetBusy(false);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshSchemaButton.IsEnabled = !busy && DatabasePicker.SelectedItem is string;
        RefreshRowsButton.IsEnabled = !busy && TableList.SelectedItem is DatabaseTable;
    }

    private void ShowRequestError(string message)
    {
        StatusText.Text = message;
        if (!_connected) ConnectionSettings.Visibility = Visibility.Visible;
        if (RowsTab.IsSelected) RowsStatus.Text = message;
        if (RelationshipsTab.IsSelected)
        {
            // A failed request is not evidence that this table has no relationships.
            RelationshipEmptyState.Visibility = Visibility.Collapsed;
            RelationshipContent.Visibility = Visibility.Collapsed;
            RelationshipsList.ItemsSource = null;
            RelationshipsStatus.Text = "Relationships could not be loaded. Refresh to try again.";
        }
    }

    private void SetSelection(Action action)
    {
        var previous = _updating;
        _updating = true;
        try { action(); }
        finally { _updating = previous; }
    }

    private void ClearTables()
    {
        _tables = [];
        _connected = false;
        DatabaseTitle.Text = "PostgreSQL";
        DatabaseTitle.ToolTip = null;
        SetSelection(() => TableList.ItemsSource = null);
        TablesHeading.Text = "TABLES / VIEWS";
        RefreshSchemaButton.IsEnabled = false;
        RefreshRowsButton.IsEnabled = false;
        ClearTableViews();
    }

    private void ClearTableViews()
    {
        ColumnGrid.ItemsSource = null;
        CopyColumnButton.IsEnabled = false;
        ColumnsHeading.Text = "Select a table to explore";
        ColumnsHeading.ToolTip = null;
        ColumnDetail.Text = "Select a column for its definition and constraints.";
        _rowColumnKeys.Clear();
        RowsGrid.ItemsSource = null;
        RowsGrid.Columns.Clear();
        RowsStatus.Text = "Choose Top 10 rows to query the selected table.";
        RowsStatus.ToolTip = null;
        CopyCellButton.IsEnabled = false;
        CopyRowButton.IsEnabled = false;
        CopyResultsButton.IsEnabled = false;
        CellDetail.Text = "";
        CellDetailHeading.Text = "Select a cell to inspect its value.";
        CellDetailExpander.Header = "Selected cell";
        ClipboardStatus.Text = CopyHint;
        RelationshipEmptyState.Visibility = Visibility.Collapsed;
        RelationshipContent.Visibility = Visibility.Collapsed;
        RelationshipsList.ItemsSource = null;
        RelationshipsStatus.Text = "Select a table to see its foreign key relationships.";
    }

    private void ApplyFilter(DatabaseTable? preferredTable = null)
    {
        var filter = TableFilter.Text.Trim();
        var visible = _tables.Where(x => x.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        SetSelection(() =>
        {
            TableList.ItemsSource = visible;
            TableList.SelectedItem = visible.FirstOrDefault(x => preferredTable is not null && x.Schema == preferredTable.Schema && x.Name == preferredTable.Name) ?? visible.FirstOrDefault();
        });
        TablesHeading.Text = $"TABLES / VIEWS ({visible.Length} / {_tables.Count})";
    }

    private async void Connection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ConnectionPicker.SelectedItem is not DatabaseConnectionSource source) return;
        ConnectionNotice.Text = "";
        await RunAsync("Connecting…", token => LoadDatabasesAsync(source, null, token));
    }

    private async void Database_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ConnectionPicker.SelectedItem is not DatabaseConnectionSource source || DatabasePicker.SelectedItem is not string database) return;
        await RunAsync("Loading schema…", token => LoadTablesAsync(source, database, token));
    }

    private async void Table_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ConnectionPicker.SelectedItem is not DatabaseConnectionSource source || DatabasePicker.SelectedItem is not string database) return;
        if (TableList.SelectedItem is DatabaseTable table)
            await RunAsync("Loading table…", token => LoadSelectedViewAsync(source, database, table, token));
    }

    private async void TableFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || TableList is null || _tables.Count == 0) return;
        var previous = TableList.SelectedItem as DatabaseTable;
        ApplyFilter(previous);
        if (TableList.SelectedItem is DatabaseTable table && table == previous) return;
        if (TableList.SelectedItem is DatabaseTable selected && ConnectionPicker.SelectedItem is DatabaseConnectionSource source && DatabasePicker.SelectedItem is string database)
            await RunAsync("Loading table…", token => LoadSelectedViewAsync(source, database, selected, token));
        else
        {
            // A filter with no match starts no replacement request; retire the old request so its cancellation cannot overwrite this state.
            _request?.Cancel();
            _request = null;
            ClearTableViews();
            SetBusy(false);
            StatusText.Text = "No tables match this filter.";
        }
    }

    private void Column_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (ColumnGrid.CurrentCell.Item is DatabaseColumn column && ColumnDetail is not null) ShowColumn(column);
    }

    private void ShowColumn(DatabaseColumn column)
    {
        ColumnDetail.Text = $"{column.Name} · {column.DataType} · Nullable: {column.Nullable}\n" +
            $"Default / expression: {(column.DefaultExpression.Length == 0 ? "None" : column.DefaultExpression)}\n" +
            (column.Generation.Length == 0 ? "" : column.Generation + "\n") +
            $"Constraints: {(column.Constraints.Length == 0 ? "None" : column.Constraints)}" +
            (column.Comment.Length == 0 ? "" : "\nComment: " + column.Comment);
        CopyColumnButton.IsEnabled = true;
    }

    private void Rows_CurrentCellChanged(object? sender, EventArgs e) => UpdateCellDetail();
    private void Rows_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e) => UpdateCellDetail();

    private bool TryGetCurrentCell(out DataRowView row, out DataGridColumn column, out string? value)
    {
        row = null!;
        column = null!;
        value = null;
        var cell = RowsGrid.CurrentCell;
        if (!cell.IsValid || cell.Item is not DataRowView currentRow || !_rowColumnKeys.TryGetValue(cell.Column, out var key)) return false;
        row = currentRow;
        column = cell.Column;
        value = row.Row.IsNull(key) ? null : (string)row[key];
        return true;
    }

    private void UpdateCellDetail()
    {
        if (CellDetail is null || CopyCellButton is null) return;
        var hasCell = TryGetCurrentCell(out var row, out var column, out var value);
        CopyCellButton.IsEnabled = hasCell;
        CopyRowButton.IsEnabled = hasCell;
        if (!hasCell)
        {
            CellDetail.Text = "";
            CellDetailHeading.Text = "Select a cell to inspect its value.";
            CellDetailExpander.Header = "Selected cell";
            return;
        }
        var kind = value is null ? "SQL NULL · copied as \\N" : value.Length == 0 ? "Empty string · 0 characters" : $"{value.Length:N0} displayed characters";
        CellDetailHeading.Text = $"{column.Header} · row {RowsGrid.Items.IndexOf(row) + 1} · {kind}";
        CellDetailExpander.Header = $"Selected cell · {column.Header}";
        CellDetail.Text = value ?? "\\N";
    }

    private string? CellValue(DataRowView row, DataGridColumn column)
    {
        var value = row[_rowColumnKeys[column]];
        return value is DBNull ? null : (string)value;
    }

    private DataGridColumn[] DisplayedColumns() => RowsGrid.Columns.Where(_rowColumnKeys.ContainsKey)
        .Where(x => x.Visibility == Visibility.Visible).OrderBy(x => x.DisplayIndex).ToArray();

    // Quoted TSV protects embedded tabs/newlines/quotes. NULL and a literal \\N are deliberately different;
    // quoted empty strings remain distinguishable from SQL NULL and from unselected gaps in a selection.
    private static string TsvField(string? value)
    {
        if (value is null) return "\\N";
        return value.Length == 0 || value == "\\N" || value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private void CopyCurrentCell()
    {
        if (!TryGetCurrentCell(out _, out _, out var value)) return;
        CopyText(value ?? "\\N", value is null ? "Copied SQL NULL as \\N." : value.Length == 0 ? "Copied an empty string." : "Copied the displayed cell value.");
    }

    private void CopySelectedCells()
    {
        var selected = RowsGrid.SelectedCells.Where(x => x.Item is DataRowView && _rowColumnKeys.ContainsKey(x.Column)).ToArray();
        if (selected.Length == 0) return;
        if (selected.Length == 1)
        {
            var value = CellValue((DataRowView)selected[0].Item, selected[0].Column);
            CopyText(value ?? "\\N", value is null ? "Copied SQL NULL as \\N." : value.Length == 0 ? "Copied an empty string." : "Copied the displayed cell value.");
            return;
        }
        var cells = selected.Select(x => ((DataRowView)x.Item, x.Column)).ToHashSet();
        var columns = DisplayedColumns().Where(column => selected.Any(x => x.Column == column)).ToArray();
        var rows = RowsGrid.Items.OfType<DataRowView>().Where(row => selected.Any(x => ReferenceEquals(x.Item, row)));
        var text = string.Join(Environment.NewLine, rows.Select(row => string.Join("\t", columns.Select(column =>
            cells.Contains((row, column)) ? TsvField(CellValue(row, column)) : ""))));
        CopyText(text, $"Copied {selected.Length} selected cells as TSV · NULL = \\N · unselected gaps are blank.");
    }

    private void CopyRows(IEnumerable<DataRowView> rows, string description)
    {
        var columns = DisplayedColumns();
        if (columns.Length == 0) return;
        var lines = new List<string> { string.Join("\t", columns.Select(column => TsvField(column.Header?.ToString() ?? ""))) };
        lines.AddRange(rows.Select(row => string.Join("\t", columns.Select(column => TsvField(CellValue(row, column))))));
        CopyText(string.Join(Environment.NewLine, lines), $"{description} with headers as TSV · NULL = \\N · displayed preview values only.");
    }

    private void CopyText(string text, string success)
    {
        try
        {
            // SetDataObject supports copying an empty string and keeps the operation local to the user's clipboard.
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            Clipboard.SetDataObject(data, true);
            ClipboardStatus.Text = success;
            StatusText.Text = success;
        }
        catch (ExternalException)
        {
            ClipboardStatus.Text = "The clipboard is busy. Try copying again.";
            StatusText.Text = ClipboardStatus.Text;
        }
    }

    private void CopyColumn_Click(object sender, RoutedEventArgs e)
    {
        if (CopyColumnButton.IsEnabled) CopyText(ColumnDetail.Text, "Copied the column definition.");
    }
    private void CopyCell_Click(object sender, RoutedEventArgs e) => CopyCurrentCell();
    private void CopySelection_Click(object sender, RoutedEventArgs e) => CopySelectedCells();
    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCurrentCell(out var row, out _, out _)) CopyRows([row], "Copied the current row");
    }
    private void CopyResults_Click(object sender, RoutedEventArgs e)
    {
        var rows = RowsGrid.Items.OfType<DataRowView>().ToArray();
        if (rows.Length > 0) CopyRows(rows, $"Copied {rows.Length} rows");
    }
    private void RowsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        CopyCellMenu.IsEnabled = CopyRowMenu.IsEnabled = TryGetCurrentCell(out _, out _, out _);
        CopySelectionMenu.IsEnabled = RowsGrid.SelectedCells.Count > 0;
        CopyResultsMenu.IsEnabled = CopyResultsButton.IsEnabled;
    }
    private void Rows_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != RowsGrid; current = current switch
        {
            Visual visual => VisualTreeHelper.GetParent(visual),
            FrameworkContentElement content => content.Parent,
            _ => LogicalTreeHelper.GetParent(current)
        })
        {
            if (current is not DataGridCell cell || cell.DataContext is not DataRowView row) continue;
            var cellInfo = new DataGridCellInfo(row, cell.Column);
            if (!RowsGrid.SelectedCells.Contains(cellInfo))
            {
                RowsGrid.SelectedCells.Clear();
                RowsGrid.SelectedCells.Add(cellInfo);
            }
            RowsGrid.CurrentCell = cellInfo;
            cell.Focus();
            break;
        }
    }

    private async void RelatedTable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DatabaseTable related } || ConnectionPicker.SelectedItem is not DatabaseConnectionSource source || DatabasePicker.SelectedItem is not string database) return;
        var table = _tables.FirstOrDefault(x => x.Id == related.Id && x.Schema == related.Schema && x.Name == related.Name);
        if (table is null)
        {
            StatusText.Text = "This related table is no longer in the navigator. Refresh the schema to update table access.";
            return;
        }
        SetSelection(() =>
        {
            // Navigation is explicit: remove a filter that would hide the chosen endpoint without firing a second request.
            if (!table.DisplayName.Contains(TableFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)) TableFilter.Clear();
            ApplyFilter(table);
            TableList.SelectedItem = table;
            RelationshipsTab.IsSelected = true;
        });
        TableList.ScrollIntoView(table);
        await RunAsync("Loading related table…", token => LoadSelectedViewAsync(source, database, table, token));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshConnectionsAsync();
    private void ChangeConnection_Click(object sender, RoutedEventArgs e)
    {
        ConnectionSettings.Visibility = ConnectionSettings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (ConnectionSettings.Visibility == Visibility.Visible) ConnectionPicker.Focus();
    }
    private void ToggleTables_Click(object sender, RoutedEventArgs e)
    {
        var hide = TablesPanel.Visibility == Visibility.Visible;
        if (hide) _tablePaneWidth = Math.Clamp(TablesColumn.ActualWidth, 150, 360);
        TablesPanel.Visibility = TablesSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        TablesColumn.MinWidth = hide ? 0 : 150;
        TablesColumn.Width = new GridLength(hide ? 0 : _tablePaneWidth);
        TablesSplitterColumn.Width = new GridLength(hide ? 0 : 10);
        ToggleTablesButton.Content = hide ? "› Show tables" : "‹ Hide tables";
        if (!hide) TableFilter.Focus();
    }
    private void HideDatabase_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private async void TableTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !ReferenceEquals(e.Source, TableTabs) || RowsGrid is null || RelationshipsList is null ||
            TableList.SelectedItem is not DatabaseTable table || ConnectionPicker.SelectedItem is not DatabaseConnectionSource source ||
            DatabasePicker.SelectedItem is not string database) return;
        await RunAsync("Loading table…", token => LoadSelectedViewAsync(source, database, table, token));
    }

    private async void RefreshRows_Click(object sender, RoutedEventArgs e)
    {
        if (TableList.SelectedItem is DatabaseTable table && ConnectionPicker.SelectedItem is DatabaseConnectionSource source && DatabasePicker.SelectedItem is string database)
            await RunAsync("Querying up to ten rows…", token => LoadSelectedViewAsync(source, database, table, token));
    }
    private async void RefreshSchema_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionPicker.SelectedItem is DatabaseConnectionSource source && DatabasePicker.SelectedItem is string database)
            await RunAsync("Refreshing schema…", token => LoadTablesAsync(source, database, token));
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelPending();
    public void CancelPending() => _request?.Cancel();

    public sealed class RelationshipView(DatabaseRelationship relationship, uint selectedTableId)
    {
        public DatabaseRelationship Relationship { get; } = relationship;
        public string Heading => (Relationship.SourceTable.Id == selectedTableId
            ? Relationship.TargetTable.Id == selectedTableId ? "SELF REFERENCE" : "OUTGOING · this table references another"
            : "INCOMING · another table references this one") + $" · {Relationship.Cardinality} · {Relationship.Name}";
        public string Multiplicity => $"{Relationship.SourceMultiplicity} → {Relationship.TargetMultiplicity}";
        public string Behavior => $"On referenced row delete: {Relationship.DeleteAction} · On referenced key update: {Relationship.UpdateAction}";
        public string ValidationNote => string.Join(" ", new[]
        {
            Relationship.IsEnforced ? "" : "Declared only: this foreign key is not enforced.",
            Relationship.IsValidated ? "" : "Existing rows have not been validated against this foreign key.",
            Relationship.IsTemporal ? "Temporal coverage can use multiple matching periods." : ""
        }.Where(x => x.Length > 0));
        public string Details => $"Constraint: {Relationship.Name}\n" +
            $"Child: {Relationship.SourceTable.DisplayName}\nParent: {Relationship.TargetTable.DisplayName}\n" +
            $"Match: {Relationship.MatchType} · {(Relationship.IsOptional ? "Nullable foreign key" : "Required foreign key")}\n" +
            (Relationship.IsDeferrable ? $"Deferrable · initially {(Relationship.IsInitiallyDeferred ? "deferred" : "immediate")}\n" : "Not deferrable\n") +
            $"Enforced: {(Relationship.IsEnforced ? "Yes" : "No")} · Validated: {(Relationship.IsValidated ? "Yes" : "No")}\n\n" + Relationship.Definition;
    }

    private sealed class GridViewportConverter : IValueConverter
    {
        internal static readonly GridViewportConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var height = value is double available && double.IsFinite(available) ? available : 180;
            return Math.Max(100, height - (parameter is double reserved ? reserved : 0));
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class PreviewValueConverter : IValueConverter
    {
        internal static readonly PreviewValueConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            null or DBNull => "∅",
            string { Length: 0 } => "\"\"",
            _ => value
        };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
