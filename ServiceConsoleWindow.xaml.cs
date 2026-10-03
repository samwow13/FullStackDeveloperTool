using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FullStackLauncher.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

/// <summary>A live view of one service's retained, already-redacted output.</summary>
public partial class ServiceConsoleWindow : Window
{
    private readonly ObservableCollection<ConsoleLine> _source;
    private readonly ObservableCollection<ConsoleLine> _visible = [];
    private bool _refreshPending;
    private bool _closed;
    private bool _initialized;

    public ServiceConsoleWindow(string projectName, string serviceName, ObservableCollection<ConsoleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _source = lines;
        InitializeComponent();
        UpdateContext(projectName, serviceName);
        Output.ItemsSource = _visible;
        Output.CopyItemsSource = _source;
        _source.CollectionChanged += SourceChanged;
        _initialized = true;
        IsVisibleChanged += VisibilityChanged;
        RefreshView();
    }

    public event EventHandler? ClearRequested;

    internal void UpdateContext(string projectName, string serviceName)
    {
        Title = $"{serviceName} — Console";
        ServiceHeading.Text = serviceName;
        ProjectHeading.Text = projectName;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueRefresh();

    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueRefresh));
            return;
        }
        if (_closed || _refreshPending || !IsVisible) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshPending = false;
            if (!_closed && IsVisible) RefreshView();
        }));
    }

    private void ViewOptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) RefreshView();
    }

    private void RefreshView()
    {
        var filter = (SeverityFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var sort = (SortOrder.SelectedItem as ComboBoxItem)?.Tag as string ?? "oldest";
        var retained = _source.ToArray();
        var entries = retained.Select((line, index) => (Line: line, Index: index))
            .Where(entry => Matches(entry.Line.Kind, filter));
        // The capture index breaks equal-timestamp ties without losing any entries.
        var ordered = sort switch
        {
            "newest" => entries.OrderByDescending(entry => entry.Line.Timestamp).ThenByDescending(entry => entry.Index),
            "severity" => entries.OrderBy(entry => SeverityRank(entry.Line.Kind))
                .ThenByDescending(entry => entry.Line.Timestamp).ThenByDescending(entry => entry.Index),
            _ => entries.OrderBy(entry => entry.Line.Timestamp).ThenBy(entry => entry.Index)
        };
        var displayed = ordered.Select(entry => entry.Line).ToArray();
        var keep = displayed.ToHashSet();
        for (var index = _visible.Count - 1; index >= 0; index--)
            if (!keep.Contains(_visible[index])) _visible.RemoveAt(index);
        for (var index = 0; index < displayed.Length; index++)
        {
            if (index < _visible.Count && ReferenceEquals(_visible[index], displayed[index])) continue;
            var existing = _visible.IndexOf(displayed[index]);
            if (existing >= 0) _visible.Move(existing, index);
            else _visible.Insert(index, displayed[index]);
        }
        Output.FollowMode = sort switch
        {
            "newest" => ConsoleFollowMode.Start,
            "severity" => ConsoleFollowMode.None,
            _ => ConsoleFollowMode.End
        };
        Output.EmptyMessage = retained.Length == 0
            ? "Waiting for output. Commands and their results appear here."
            : "No retained output matches this filter. Choose All output to see other entries.";
        var errors = retained.Count(line => line.Kind == ServiceLogKind.Error);
        var warnings = retained.Count(line => line.Kind == ServiceLogKind.Warning);
        CountsText.Text = $"{displayed.Length:N0} shown · {retained.Length:N0} retained · " +
            $"{errors:N0} {(errors == 1 ? "error" : "errors")} · {warnings:N0} {(warnings == 1 ? "warning" : "warnings")}";
    }

    private static bool Matches(ServiceLogKind kind, string filter) => filter switch
    {
        "problems" => kind is ServiceLogKind.Error or ServiceLogKind.Warning,
        "error" => kind == ServiceLogKind.Error,
        "warning" => kind == ServiceLogKind.Warning,
        "command" => kind == ServiceLogKind.Command,
        "output" => kind == ServiceLogKind.Output,
        "success" => kind == ServiceLogKind.Success,
        "information" => kind == ServiceLogKind.Information,
        _ => true
    };

    private static int SeverityRank(ServiceLogKind kind) => kind switch
    {
        ServiceLogKind.Error => 0,
        ServiceLogKind.Warning => 1,
        _ => 2
    };

    private void ClearOutputRequested(object? sender, EventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _source.CollectionChanged -= SourceChanged;
        IsVisibleChanged -= VisibilityChanged;
        Output.ItemsSource = null;
        Output.CopyItemsSource = null;
        _visible.Clear();
        ClearRequested = null;
    }
}
