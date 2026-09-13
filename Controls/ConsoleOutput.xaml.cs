using System.Collections;
using System.Collections.Specialized;
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

/// <summary>Selectable terminal output with incremental document updates and user-controlled following.</summary>
public partial class ConsoleOutput : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ConsoleOutput), new PropertyMetadata(null, ItemsSourceChanged));
    public static readonly DependencyProperty ShowSourceProperty = DependencyProperty.Register(
        nameof(ShowSource), typeof(bool), typeof(ConsoleOutput), new PropertyMetadata(true, PresentationChanged));

    private static readonly Brush TimestampBrush = FrozenBrush("#829F95");
    private static readonly Brush SourceBrush = FrozenBrush("#9CBAD2");
    private static readonly Brush CommandBackground = FrozenBrush("#0E2429");
    private static readonly Brush ErrorBackground = FrozenBrush("#291820");
    private readonly List<(ConsoleLine Line, Paragraph Paragraph)> _displayed = [];
    private INotifyCollectionChanged? _subscribed;
    private bool _updating;
    private bool _scrollPending;
    private bool _rebuildPending;

    public ConsoleOutput() => InitializeComponent();

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public bool ShowSource { get => (bool)GetValue(ShowSourceProperty); set => SetValue(ShowSourceProperty, value); }
    public event EventHandler? ClearRequested;

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
        if (_subscribed is not null) return;
        _subscribed = ItemsSource as INotifyCollectionChanged;
        if (_subscribed is not null) _subscribed.CollectionChanged += CollectionChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.CollectionChanged -= CollectionChanged;
        _subscribed = null;
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // UI-bound collections should normally be updated on the dispatcher. Coalesce a refresh if not.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueRebuild));
            return;
        }
        if (!ReferenceEquals(sender, _subscribed)) return;
        _updating = true;
        try
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && e.NewStartingIndex >= 0)
            {
                var index = e.NewStartingIndex;
                foreach (var item in e.NewItems)
                    if (item is ConsoleLine line) Insert(index++, line);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null && e.OldStartingIndex >= 0)
            {
                for (var count = 0; count < e.OldItems.Count && e.OldStartingIndex < _displayed.Count; count++)
                {
                    OutputText.Document.Blocks.Remove(_displayed[e.OldStartingIndex].Paragraph);
                    _displayed.RemoveAt(e.OldStartingIndex);
                }
            }
            else
            {
                Rebuild();
                return;
            }
        }
        finally { _updating = false; }
        UpdateStatus();
        FollowLatest();
    }

    private void QueueRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _rebuildPending = false; Rebuild(); }));
    }

    private void Rebuild()
    {
        if (OutputText is null) return;
        _updating = true;
        try
        {
            OutputText.Document.Blocks.Clear();
            _displayed.Clear();
            if (ItemsSource is not null)
                foreach (var item in ItemsSource)
                    if (item is ConsoleLine line) Insert(_displayed.Count, line);
        }
        finally { _updating = false; }
        UpdateStatus();
        FollowLatest();
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
        if (_updating || FollowToggle is null || FollowToggle.IsChecked != true) return;
        // Content growth changes the extent. Only a viewport scroll away from the tail pauses following.
        if (e.ExtentHeightChange == 0 && e.VerticalChange < 0 &&
            e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 2)
            FollowToggle.IsChecked = false;
    }

    private void OutputMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && OutputText.ExtentHeight > OutputText.ViewportHeight) FollowToggle.IsChecked = false;
    }

    private void OutputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp || e.Key == Key.Home && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            FollowToggle.IsChecked = false;
    }

    private void FollowLatest()
    {
        if (FollowToggle?.IsChecked != true || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { if (FollowToggle.IsChecked == true) OutputText.ScrollToEnd(); }
            finally { _scrollPending = false; }
        }));
    }

    private void UpdateStatus(string? notice = null)
    {
        if (StatusText is null) return;
        EmptyState.Visibility = _displayed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = _displayed.Count > 0;
        StatusText.Text = notice ?? $"{_displayed.Count:N0} {(_displayed.Count == 1 ? "line" : "lines")} · " +
            (FollowToggle.IsChecked == true ? "Following output" : "Scroll paused · Enable Follow output to jump to latest");
    }

    private void CopyAllClick(object sender, RoutedEventArgs e)
    {
        if (_displayed.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _displayed.Select(item => item.Line.PlainText)));
            UpdateStatus($"Copied {_displayed.Count:N0} lines.");
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
