using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher.Controls;

public enum ConsoleFollowMode { End, Start, None }

/// <summary>Selectable terminal output with incremental document updates and user-controlled following.</summary>
public partial class ConsoleOutput : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ConsoleOutput), new PropertyMetadata(null, ItemsSourceChanged));
    public static readonly DependencyProperty ShowSourceProperty = DependencyProperty.Register(
        nameof(ShowSource), typeof(bool), typeof(ConsoleOutput), new PropertyMetadata(true, PresentationChanged));
    public static readonly DependencyProperty CopyItemsSourceProperty = DependencyProperty.Register(
        nameof(CopyItemsSource), typeof(IEnumerable), typeof(ConsoleOutput), new PropertyMetadata(null, CopyItemsSourceChanged));
    public static readonly DependencyProperty FollowModeProperty = DependencyProperty.Register(
        nameof(FollowMode), typeof(ConsoleFollowMode), typeof(ConsoleOutput), new PropertyMetadata(ConsoleFollowMode.End, FollowModeChanged));
    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(
        nameof(EmptyMessage), typeof(string), typeof(ConsoleOutput),
        new PropertyMetadata("Waiting for output. Commands and their results appear here.", EmptyMessageChanged));

    private static readonly Brush TimestampBrush = FrozenBrush("#829F95");
    private static readonly Brush SourceBrush = FrozenBrush("#9CBAD2");
    private static readonly Brush CommandBackground = FrozenBrush("#0E2429");
    private static readonly Brush ErrorBackground = FrozenBrush("#291820");
    private readonly List<(ConsoleLine Line, Paragraph Paragraph)> _displayed = [];
    private INotifyCollectionChanged? _subscribed;
    private INotifyCollectionChanged? _copySubscribed;
    private bool _updating;
    private bool _scrollPending;
    private bool _rebuildPending;
    private bool _resetDocument;

    public ConsoleOutput()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueRebuild(); };
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public bool ShowSource { get => (bool)GetValue(ShowSourceProperty); set => SetValue(ShowSourceProperty, value); }
    /// <summary>Optional complete buffer for Copy all and Clear when the displayed source is filtered.</summary>
    public IEnumerable? CopyItemsSource { get => (IEnumerable?)GetValue(CopyItemsSourceProperty); set => SetValue(CopyItemsSourceProperty, value); }
    public ConsoleFollowMode FollowMode { get => (ConsoleFollowMode)GetValue(FollowModeProperty); set => SetValue(FollowModeProperty, value); }
    public string EmptyMessage { get => (string)GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }
    public event EventHandler? ClearRequested;

    private static void CopyItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ConsoleOutput)d;
        control.Unsubscribe();
        if (control.IsLoaded) control.Subscribe();
        control.QueueRebuild();
    }

    private static void FollowModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ConsoleOutput)d;
        if (control.FollowToggle is null) return;
        control.FollowToggle.IsEnabled = control.FollowMode != ConsoleFollowMode.None;
        control.FollowToggle.ToolTip = control.FollowMode == ConsoleFollowMode.None
            ? "Follow output is available with Oldest first or Newest first sorting."
            : "Scroll with new output. Scroll away from the latest line to pause; re-enable to jump to it.";
        control.UpdateStatus();
        control.FollowLatest();
    }

    private static void EmptyMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ConsoleOutput)d;
        if (control.EmptyState is not null) control.EmptyState.Text = control.EmptyMessage;
    }

    private static void ItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ConsoleOutput)d;
        control.Unsubscribe();
        if (control.IsLoaded) control.Subscribe();
        control.Rebuild();
    }

    private static void PresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ConsoleOutput)d).Rebuild();

    private void OnLoaded(object sender, RoutedEventArgs e) { Subscribe(); Rebuild(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed is not null || _copySubscribed is not null) return;
        _subscribed = ItemsSource as INotifyCollectionChanged;
        if (_subscribed is not null) _subscribed.CollectionChanged += CollectionChanged;
        _copySubscribed = CopyItemsSource as INotifyCollectionChanged;
        if (_copySubscribed is not null && !ReferenceEquals(_copySubscribed, _subscribed))
            _copySubscribed.CollectionChanged += CopyCollectionChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.CollectionChanged -= CollectionChanged;
        if (_copySubscribed is not null && !ReferenceEquals(_copySubscribed, _subscribed))
            _copySubscribed.CollectionChanged -= CopyCollectionChanged;
        _subscribed = null;
        _copySubscribed = null;
    }

    private void CopyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // UI-bound collections should normally be updated on the dispatcher. Coalesce a refresh if not.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueRebuild));
            return;
        }
        if (!ReferenceEquals(sender, _subscribed)) return;
        // A timer drain can add/trim hundreds of lines across console views. Coalesce them
        // into one document transaction, and do no rich-text work for hidden panels.
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueRebuild));
            return;
        }
        if (_rebuildPending || !IsLoaded || !IsVisible) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _rebuildPending = false;
            if (IsLoaded && IsVisible) SynchronizeDocument();
        }));
    }

    private void Rebuild()
    {
        _resetDocument = true;
        QueueRebuild();
    }

    private ConsoleLine[] RetainedLines() => ItemsSource?.OfType<ConsoleLine>().ToArray() ?? [];
    private ConsoleLine[] CopyLines() => (CopyItemsSource ?? ItemsSource)?.OfType<ConsoleLine>().ToArray() ?? [];

    private void SynchronizeDocument()
    {
        var lines = RetainedLines();
        var started = Stopwatch.GetTimestamp();
        var complete = false;
        var changes = 0;
        _updating = true;
        OutputText.BeginChange();
        try
        {
            // Retain paragraph objects and selections for unchanged entries, including
            // insertion within a filtered or severity-sorted popout. Bound each batch.
            if (_resetDocument)
            {
                OutputText.Document.Blocks.Clear();
                _displayed.Clear();
            }
            _resetDocument = false;
            var expected = lines.ToHashSet();
            var removedMissing = true;
            for (var index = _displayed.Count - 1; index >= 0; index--)
            {
                if (expected.Contains(_displayed[index].Line)) continue;
                if (BatchFull())
                {
                    removedMissing = false;
                    break;
                }
                OutputText.Document.Blocks.Remove(_displayed[index].Paragraph);
                _displayed.RemoveAt(index);
                changes++;
            }
            if (removedMissing)
            {
                complete = true;
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index < _displayed.Count && ReferenceEquals(_displayed[index].Line, lines[index])) continue;
                    if (BatchFull()) { complete = false; break; }
                    var existing = _displayed.FindIndex(index, item => ReferenceEquals(item.Line, lines[index]));
                    if (existing >= 0)
                    {
                        var entry = _displayed[existing];
                        OutputText.Document.Blocks.Remove(entry.Paragraph);
                        _displayed.RemoveAt(existing);
                        if (index == _displayed.Count) OutputText.Document.Blocks.Add(entry.Paragraph);
                        else OutputText.Document.Blocks.InsertBefore(_displayed[index].Paragraph, entry.Paragraph);
                        _displayed.Insert(index, entry);
                    }
                    else Insert(index, lines[index]);
                    changes++;
                }
            }
        }
        finally
        {
            OutputText.EndChange();
            _updating = false;
        }
        UpdateStatus();
        FollowLatest();
        if (!complete) QueueRebuild();

        bool BatchFull() => changes >= 40 || changes > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8;
    }

    private void Insert(int index, ConsoleLine line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(5, 1, 5, 1) };
        if (line.Kind == ServiceLogKind.Command) paragraph.Background = CommandBackground;
        else if (line.Kind == ServiceLogKind.Error) paragraph.Background = ErrorBackground;
        paragraph.Inlines.Add(new Run(line.TimestampLabel + "  ") { Foreground = TimestampBrush });
        paragraph.Inlines.Add(new Run($"[{line.Label}] ") { Foreground = line.Foreground, FontWeight = FontWeights.SemiBold });
        if (ShowSource && line.Source.Length > 0)
            paragraph.Inlines.Add(new Run($"[{line.Source}] ") { Foreground = SourceBrush });
        paragraph.Inlines.Add(new Run(line.Message) { Foreground = line.Foreground });
        index = Math.Clamp(index, 0, _displayed.Count);
        if (index == _displayed.Count) OutputText.Document.Blocks.Add(paragraph);
        else OutputText.Document.Blocks.InsertBefore(_displayed[index].Paragraph, paragraph);
        _displayed.Insert(index, (line, paragraph));
    }

    private void FollowChanged(object sender, RoutedEventArgs e)
    {
        if (OutputText is null || StatusText is null) return;
        UpdateStatus();
        FollowLatest();
    }

    private void OutputScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_updating || FollowMode == ConsoleFollowMode.None || FollowToggle is null || FollowToggle.IsChecked != true) return;
        // Content growth changes the extent. Only a viewport scroll away from the tail pauses following.
        if (e.ExtentHeightChange == 0 &&
            (FollowMode == ConsoleFollowMode.End && e.VerticalChange < 0 && e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 2 ||
             FollowMode == ConsoleFollowMode.Start && e.VerticalChange > 0 && e.VerticalOffset > 2))
            FollowToggle.IsChecked = false;
    }

    private void OutputMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (OutputText.ExtentHeight > OutputText.ViewportHeight &&
            (FollowMode == ConsoleFollowMode.End && e.Delta > 0 || FollowMode == ConsoleFollowMode.Start && e.Delta < 0))
            FollowToggle.IsChecked = false;
    }

    private void OutputKeyDown(object sender, KeyEventArgs e)
    {
        if (FollowMode == ConsoleFollowMode.End &&
                (e.Key is Key.Up or Key.PageUp || e.Key == Key.Home && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ||
            FollowMode == ConsoleFollowMode.Start &&
                (e.Key is Key.Down or Key.PageDown || e.Key == Key.End && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            FollowToggle.IsChecked = false;
    }

    private void FollowLatest()
    {
        if (!IsVisible || FollowMode == ConsoleFollowMode.None || FollowToggle?.IsChecked != true || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (!IsLoaded || !IsVisible || FollowToggle.IsChecked != true) return;
                if (FollowMode == ConsoleFollowMode.Start) OutputText.ScrollToHome();
                else if (FollowMode == ConsoleFollowMode.End) OutputText.ScrollToEnd();
            }
            finally { _scrollPending = false; }
        }));
    }

    private void UpdateStatus(string? notice = null)
    {
        if (StatusText is null) return;
        EmptyState.Visibility = _displayed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var retainedCount = CopyLines().Length;
        CopyButton.IsEnabled = retainedCount > 0;
        ClearButton.IsEnabled = retainedCount > 0;
        CopyButton.ToolTip = CopyItemsSource is null
            ? "Copy the complete retained output with timestamps and labels"
            : "Copy every retained line, including lines hidden by the filter, in capture order";
        ClearButton.ToolTip = CopyItemsSource is null
            ? "Clear this console's retained output"
            : "Clear all retained output for this service, including lines hidden by the filter";
        StatusText.Text = notice ?? $"{_displayed.Count:N0} {(_displayed.Count == 1 ? "line" : "lines")} · " +
            (FollowMode == ConsoleFollowMode.None ? "Follow output available with chronological sorting" :
                FollowToggle.IsChecked == true ? "Following output" : "Scroll paused · Enable Follow output to jump to latest");
    }

    private void CopyAllClick(object sender, RoutedEventArgs e)
    {
        var lines = CopyLines();
        if (lines.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(line => line.PlainText)));
            UpdateStatus($"Copied {lines.Length:N0} lines.");
        }
        catch (ExternalException) { UpdateStatus("Clipboard is busy. Select text and try Ctrl+C, or try Copy all again."); }
    }

    private void ClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
