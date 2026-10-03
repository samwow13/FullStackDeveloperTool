using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;

namespace FullStackLauncher.Controls;

public sealed record CommitMessageUnreadRange(int Start, int Length);

public sealed record CommitMessagePresentation(string Text, IReadOnlyList<CommitMessageUnreadRange> UnreadRanges,
    AgentGitSummaryBatch? DisplayedUnviewedBatch);

public sealed class CommitMessageReadEventArgs(CommitMessagePresentation presentation) : EventArgs
{
    public CommitMessagePresentation Presentation { get; } = presentation;
}

/// <summary>A single selectable message with unread styling and explicit acknowledgment of displayed updates.</summary>
public partial class CommitMessagePreview : UserControl
{
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(
        nameof(Presentation), typeof(CommitMessagePresentation), typeof(CommitMessagePreview),
        new PropertyMetadata(null, PresentationChanged));

    private static readonly Brush MessageBrush = FrozenBrush("#EDF3FC");
    private static readonly Brush UnreadBrush = FrozenBrush("#B6F5DF");
    private static readonly CommitMessagePresentation EmptyPresentation = new("", [], null);
    private DispatcherOperation? _renderOperation;
    private RenderWork? _renderWork;
    private CommitMessagePresentation? _renderedPresentation;
    private int _documentGeneration;
    private bool _mouseSelectionInProgress;

    public CommitMessagePreview()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) QueueRender();
            else CancelPendingRender();
        };
    }

    public CommitMessagePresentation? Presentation
    {
        get => (CommitMessagePresentation?)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public event EventHandler<CommitMessageReadEventArgs>? ReadRequested;

    private static void PresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CommitMessagePreview)d).QueueRender();

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueRender();
    private void OnUnloaded(object sender, RoutedEventArgs e) => CancelPendingRender();

    private void CancelPendingRender()
    {
        _renderOperation?.Abort();
        _renderOperation = null;
        _renderWork = null;
        _mouseSelectionInProgress = false;
    }

    private void QueueRender()
    {
        if (_renderOperation is not null || !IsLoaded || !IsVisible) return;
        _renderOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RenderBatch));
    }

    private void RenderBatch()
    {
        _renderOperation = null;
        if (!IsLoaded || !IsVisible) { _renderWork = null; return; }
        // A click can start a native selection drag and a durable acknowledgment.
        // Wait for release/capture loss before replacing that drag's document.
        if (_mouseSelectionInProgress && Mouse.LeftButton == MouseButtonState.Pressed) return;
        var snapshot = CapturePresentation(Presentation ?? EmptyPresentation);

        // A fresh ledger read can contain the same text and styles but newer identities.
        // Keep the captured click target current without touching selection or layout.
        if (SameDisplay(_renderedPresentation, snapshot))
        {
            _renderedPresentation = snapshot;
            _renderWork = null;
            return;
        }
        if (_renderWork is null || !SameDisplay(_renderWork.Presentation, snapshot))
            _renderWork = new RenderWork(snapshot);
        else _renderWork.Presentation = snapshot;

        var work = _renderWork;
        var started = Stopwatch.GetTimestamp();
        var changes = 0;
        while (work.Offset < snapshot.Text.Length)
        {
            work.AppendRun();
            if (++changes >= 32 || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
        }
        if (work.Offset < snapshot.Text.Length) { QueueRender(); return; }

        var sameText = _renderedPresentation?.Text == snapshot.Text;
        var selectionStart = sameText ? TextIndex(MessageText.Document, MessageText.Selection.Start) : 0;
        var selectionEnd = sameText ? TextIndex(MessageText.Document, MessageText.Selection.End) : 0;
        var caretAtStart = sameText && MessageText.CaretPosition.CompareTo(MessageText.Selection.Start) <= 0;
        var verticalOffset = MessageText.VerticalOffset;
        var horizontalOffset = MessageText.HorizontalOffset;
        MessageText.Document = work.Document;
        _renderedPresentation = work.Presentation;
        _renderWork = null;
        var generation = ++_documentGeneration;
        if (sameText)
        {
            var start = TextPosition(work.Document, selectionStart);
            var end = TextPosition(work.Document, selectionEnd);
            MessageText.Selection.Select(caretAtStart ? end : start, caretAtStart ? start : end);
            MessageText.ScrollToVerticalOffset(verticalOffset);
            MessageText.ScrollToHorizontalOffset(horizontalOffset);
            // Restore again after layout, which may otherwise clamp the old offset
            // against the new document's not-yet-measured extent.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation != _documentGeneration || !IsLoaded || !IsVisible) return;
                MessageText.ScrollToVerticalOffset(verticalOffset);
                MessageText.ScrollToHorizontalOffset(horizontalOffset);
            }));
        }
    }

    private void MessageMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Scrollbar use is navigation. Leave selection, focus, and drag behavior native.
        if (IsScrollbarSource(e.OriginalSource as DependencyObject)) return;
        _mouseSelectionInProgress = true;
        RequestRead();
    }

    private void MessageSelectionEnded(object sender, RoutedEventArgs e)
    {
        _mouseSelectionInProgress = false;
        QueueRender();
    }

    private void MessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        RequestRead();
        e.Handled = true;
    }

    private void RequestRead()
    {
        if (!IsLoaded || !IsVisible || !IsEnabled || _renderedPresentation is not { } displayed
            || displayed.DisplayedUnviewedBatch is not { Entries.Count: > 0 }) return;
        // The dependency property may already be ahead of the coalesced document.
        // A click acknowledges only the complete snapshot currently on screen.
        ReadRequested?.Invoke(this, new CommitMessageReadEventArgs(displayed));
    }

    private static bool IsScrollbarSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static CommitMessagePresentation CapturePresentation(CommitMessagePresentation source)
    {
        var text = source.Text ?? "";
        var ranges = NormalizeRanges(source.UnreadRanges, text.Length);
        var batch = source.DisplayedUnviewedBatch;
        return new CommitMessagePresentation(text, ranges, batch is null ? null : batch with
        {
            Entries = batch.Entries.Select(entry => entry with { Bullets = entry.Bullets.ToArray() }).ToArray()
        });
    }

    private static CommitMessageUnreadRange[] NormalizeRanges(IReadOnlyList<CommitMessageUnreadRange>? source, int textLength)
    {
        if (source is null || textLength == 0) return [];
        var ranges = source.Where(range => range.Start >= 0 && range.Start < textLength && range.Length > 0)
            .Select(range => new CommitMessageUnreadRange(range.Start, Math.Min(range.Length, textLength - range.Start)))
            .OrderBy(range => range.Start).ToArray();
        var merged = new List<CommitMessageUnreadRange>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].Start + merged[^1].Length) merged.Add(range);
            else
            {
                var previous = merged[^1];
                merged[^1] = previous with { Length = Math.Max(previous.Length, range.Start + range.Length - previous.Start) };
            }
        }
        return merged.ToArray();
    }

    private static bool SameDisplay(CommitMessagePresentation? left, CommitMessagePresentation right) =>
        left is not null && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.UnreadRanges.SequenceEqual(right.UnreadRanges);

    private static int TextIndex(FlowDocument document, TextPointer position)
    {
        var index = 0;
        var current = document.ContentStart;
        while (current.CompareTo(position) < 0)
        {
            if (current.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = current.GetTextRunLength(LogicalDirection.Forward);
                var end = current.GetPositionAtOffset(length, LogicalDirection.Forward)!;
                if (end.CompareTo(position) >= 0) return index + current.GetOffsetToPosition(position);
                index += length;
                current = end;
            }
            else
            {
                var next = current.GetNextContextPosition(LogicalDirection.Forward);
                if (next is null) break;
                current = next;
            }
        }
        return index;
    }

    private static TextPointer TextPosition(FlowDocument document, int index)
    {
        var current = document.ContentStart;
        while (true)
        {
            if (current.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = current.GetTextRunLength(LogicalDirection.Forward);
                if (index <= length) return current.GetPositionAtOffset(index, LogicalDirection.Forward)!;
                index -= length;
                current = current.GetPositionAtOffset(length, LogicalDirection.Forward)!;
            }
            else
            {
                var next = current.GetNextContextPosition(LogicalDirection.Forward);
                if (next is null) return document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                current = next;
            }
        }
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromInvariantString(color)!;
        brush.Freeze();
        return brush;
    }

    private sealed class RenderWork
    {
        private readonly Paragraph _paragraph = new() { Margin = new Thickness(0) };
        private int _rangeIndex;

        public RenderWork(CommitMessagePresentation presentation)
        {
            Presentation = presentation;
            Document = new FlowDocument(_paragraph)
            {
                PagePadding = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 13,
                LineHeight = 21, Foreground = MessageBrush
            };
        }

        public CommitMessagePresentation Presentation { get; set; }
        public FlowDocument Document { get; }
        public int Offset { get; private set; }

        public void AppendRun()
        {
            var text = Presentation.Text;
            var ranges = Presentation.UnreadRanges;
            while (_rangeIndex < ranges.Count && ranges[_rangeIndex].Start + ranges[_rangeIndex].Length <= Offset) _rangeIndex++;
            var range = _rangeIndex < ranges.Count ? ranges[_rangeIndex] : null;
            var unread = range is not null && Offset >= range.Start;
            var boundary = range is null ? text.Length : unread ? range.Start + range.Length : range.Start;
            var end = Math.Min(boundary, Offset + Math.Min(2048, text.Length - Offset));
            // Do not split a surrogate pair or a CRLF when chunking a long run.
            if (end < boundary && end < text.Length && end > Offset
                && (char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])
                    || text[end] == '\n' && text[end - 1] == '\r')) end--;
            if (end == Offset) end = Math.Min(boundary, Offset + 2);
            _paragraph.Inlines.Add(new Run(text[Offset..end])
            {
                Foreground = unread ? UnreadBrush : MessageBrush,
                TextDecorations = unread ? TextDecorations.Underline : null
            });
            Offset = end;
        }
    }
}
