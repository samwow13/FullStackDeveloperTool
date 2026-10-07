using System.Diagnostics;
using System.Globalization;
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

public sealed record CommitMessageHistory(int RecentLength, IReadOnlyList<CommitMessageUnreadRange> RecentUnreadRanges,
    AgentGitSummaryBatch? RecentUnviewedBatch, string ScopeKey);

public sealed record CommitMessagePresentation(string Text, IReadOnlyList<CommitMessageUnreadRange> UnreadRanges,
    AgentGitSummaryBatch? DisplayedUnviewedBatch, CommitMessageHistory? History = null, bool HistoryExpanded = false);

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
    private bool _historyExpanded;
    private string? _historyScope;
    private bool _focusHistoryToggle;
    private Window? _animationWindow;
    private (AgentGitSummaryBatch Scope, string EntryId, long RequestedAt)? _pendingAnimation;

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

    private static void PresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var preview = (CommitMessagePreview)d;
        if (e.NewValue is not CommitMessagePresentation { Text.Length: > 0 } presentation
            || preview._pendingAnimation is { } pending && presentation.DisplayedUnviewedBatch is { } batch
                && !SameScope(pending.Scope, batch))
            preview.StopNewEntryAnimation();
        var scope = (e.NewValue as CommitMessagePresentation)?.History?.ScopeKey;
        if (preview._historyScope != scope)
        {
            preview._historyScope = scope;
            preview._historyExpanded = false;
            preview._focusHistoryToggle = false;
        }
        preview.QueueRender();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _animationWindow = Window.GetWindow(this);
        if (_animationWindow is not null) _animationWindow.StateChanged += AnimationWindowStateChanged;
        QueueRender();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_animationWindow is not null) _animationWindow.StateChanged -= AnimationWindowStateChanged;
        _animationWindow = null;
        CancelPendingRender();
    }

    public void AnimateNewEntry(AgentGitSummaryBatch scope, string entryId)
    {
        if (!IsLoaded || !IsVisible || _animationWindow?.WindowState == WindowState.Minimized) return;
        _pendingAnimation = (scope, entryId, Stopwatch.GetTimestamp());
        _renderWork = null;
        QueueRender();
    }

    private void AnimationWindowStateChanged(object? sender, EventArgs e)
    {
        if (_animationWindow?.WindowState == WindowState.Minimized) StopNewEntryAnimation();
    }

    public void StopNewEntryAnimation()
    {
        _pendingAnimation = null;
        HammerRobot.Stop();
    }

    private void AnimateRenderedEntry(IReadOnlyList<Brush>? textRevealBrushes = null)
    {
        if (_pendingAnimation is not { } pending) return;
        if (Stopwatch.GetElapsedTime(pending.RequestedAt).TotalSeconds > 10)
        {
            _pendingAnimation = null;
            return;
        }
        if (_renderedPresentation?.DisplayedUnviewedBatch is not { } displayed
            || !SameScope(pending.Scope, displayed)
            || !displayed.Entries.Any(entry => entry.Id == pending.EntryId)) return;
        _pendingAnimation = null;
        HammerRobot.Play(textRevealBrushes);
    }

    private CommitMessageUnreadRange[] NewEntryRevealRanges(CommitMessagePresentation presentation)
    {
        if (_pendingAnimation is not { } pending
            || Stopwatch.GetElapsedTime(pending.RequestedAt).TotalSeconds > 10
            || presentation.DisplayedUnviewedBatch is not { } displayed
            || !SameScope(pending.Scope, displayed)
            || displayed.Entries.FirstOrDefault(entry => entry.Id == pending.EntryId) is not { } entry) return [];
        var bullets = entry.Bullets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<CommitMessageUnreadRange>();
        var offset = 0;
        foreach (var line in presentation.Text.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)
                || text.StartsWith("• ", StringComparison.Ordinal)) text = text[2..];
            if (bullets.Contains(text)) ranges.Add(new(offset, line.TrimEnd('\r').Length));
            offset += line.Length + 1;
        }
        return ranges.ToArray();
    }

    private static bool SameScope(AgentGitSummaryBatch left, AgentGitSummaryBatch right) =>
        string.Equals(left.RepositoryRoot, right.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
        && left.Branch == right.Branch && left.RemoteName == right.RemoteName && left.ConnectionId == right.ConnectionId;

    private void CancelPendingRender()
    {
        _renderOperation?.Abort();
        _renderOperation = null;
        _renderWork = null;
        _mouseSelectionInProgress = false;
        _focusHistoryToggle = false;
        StopNewEntryAnimation();
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
        var snapshot = CapturePresentation(Presentation ?? EmptyPresentation, _historyExpanded);
        var revealRanges = NewEntryRevealRanges(snapshot);

        // A fresh ledger read can contain the same text and styles but newer identities.
        // Keep the captured click target current without touching selection or layout.
        if (SameDisplay(_renderedPresentation, snapshot) && revealRanges.Length == 0)
        {
            _renderedPresentation = snapshot;
            _renderWork = null;
            AnimateRenderedEntry();
            return;
        }
        if (_renderWork is null || !SameDisplay(_renderWork.Presentation, snapshot))
            _renderWork = new RenderWork(snapshot, revealRanges);
        else _renderWork.Presentation = snapshot;

        var work = _renderWork;
        var started = Stopwatch.GetTimestamp();
        var changes = 0;
        while (work.Offset < snapshot.Text.Length)
        {
            work.AppendRun(HistoryToggle_Click);
            if (++changes >= 32 || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
        }
        if (work.Offset < snapshot.Text.Length) { QueueRender(); return; }
        work.AppendHistoryToggle(HistoryToggle_Click);

        var sameText = _renderedPresentation?.Text == snapshot.Text;
        var selectionStart = sameText ? TextIndex(MessageText.Document, MessageText.Selection.Start) : 0;
        var selectionEnd = sameText ? TextIndex(MessageText.Document, MessageText.Selection.End) : 0;
        var caretAtStart = sameText && MessageText.CaretPosition.CompareTo(MessageText.Selection.Start) <= 0;
        var verticalOffset = MessageText.VerticalOffset;
        var horizontalOffset = MessageText.HorizontalOffset;
        HammerRobot.Stop();
        MessageText.Document = work.Document;
        _renderedPresentation = work.Presentation;
        _renderWork = null;
        AnimateRenderedEntry(work.RevealBrushes);
        var generation = ++_documentGeneration;
        if (_focusHistoryToggle && work.HistoryToggle is { } toggle)
        {
            _focusHistoryToggle = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation == _documentGeneration && IsLoaded && IsVisible) toggle.Focus();
            }));
        }
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
        // Scrolling and expanding history never acknowledge hidden reports.
        if (IsNavigationSource(e.OriginalSource as DependencyObject)) return;
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
        if (!IsNavigationSource(e.OriginalSource as DependencyObject)) HammerRobot.CompleteTextReveal();
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None
            || IsNavigationSource(e.OriginalSource as DependencyObject)
            || IsNavigationSource(Keyboard.FocusedElement as DependencyObject)) return;
        RequestRead();
        e.Handled = true;
    }

    private void RequestRead()
    {
        if (!IsLoaded || !IsVisible || !IsEnabled || _renderedPresentation is not { } displayed
            || displayed.DisplayedUnviewedBatch is not { Entries.Count: > 0 }) return;
        HammerRobot.CompleteTextReveal();
        // The dependency property may already be ahead of the coalesced document.
        // A click acknowledges only the complete snapshot currently on screen.
        ReadRequested?.Invoke(this, new CommitMessageReadEventArgs(displayed));
    }

    private void HistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_renderedPresentation?.History is not { } rendered
            || Presentation?.History?.ScopeKey != rendered.ScopeKey) return;
        _focusHistoryToggle = sender is Hyperlink { IsKeyboardFocusWithin: true };
        _historyExpanded = !_historyExpanded;
        QueueRender();
        e.Handled = true;
    }

    private static bool IsNavigationSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar or Hyperlink) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static CommitMessagePresentation CapturePresentation(CommitMessagePresentation source, bool historyExpanded)
    {
        var text = source.Text ?? "";
        var history = source.History;
        if (history is not null && (history.RecentLength < 0 || history.RecentLength > text.Length)) history = null;
        if (history is not null && !historyExpanded) text = text[..history.RecentLength];
        var ranges = NormalizeRanges(history is not null && !historyExpanded ? history.RecentUnreadRanges : source.UnreadRanges, text.Length);
        var batch = history is not null && !historyExpanded ? history.RecentUnviewedBatch : source.DisplayedUnviewedBatch;
        return new CommitMessagePresentation(text, ranges, batch is null ? null : batch with
        {
            Entries = batch.Entries.Select(entry => entry with { Bullets = entry.Bullets.ToArray() }).ToArray()
        }, history, history is not null && historyExpanded);
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
        && left.UnreadRanges.SequenceEqual(right.UnreadRanges)
        && left.History?.ScopeKey == right.History?.ScopeKey
        && left.History?.RecentLength == right.History?.RecentLength
        && (left.History is null) == (right.History is null)
        && left.HistoryExpanded == right.HistoryExpanded;

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
                var isToggle = IsNavigationSource(current.Parent);
                if (end.CompareTo(position) >= 0) return isToggle ? index : index + current.GetOffsetToPosition(position);
                if (!isToggle) index += length;
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
                if (IsNavigationSource(current.Parent))
                {
                    current = current.GetPositionAtOffset(length, LogicalDirection.Forward)!;
                    continue;
                }
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
        private readonly CommitMessageUnreadRange[] _revealRanges;
        private int _rangeIndex;
        private int _revealIndex;

        public RenderWork(CommitMessagePresentation presentation, IReadOnlyList<CommitMessageUnreadRange> revealRanges)
        {
            Presentation = presentation;
            // Bound animation clocks and keep Unicode text elements intact.
            var chunkSize = Math.Max(1, (int)Math.Ceiling(revealRanges.Sum(range => range.Length) / 64.0));
            var chunks = new List<CommitMessageUnreadRange>();
            foreach (var range in revealRanges)
            {
                var elements = StringInfo.ParseCombiningCharacters(presentation.Text.Substring(range.Start, range.Length));
                for (var index = 0; index < elements.Length;)
                {
                    var start = elements[index];
                    var next = index + 1;
                    while (next < elements.Length && elements[next] - start < chunkSize) next++;
                    var end = next < elements.Length ? elements[next] : range.Length;
                    chunks.Add(new(range.Start + start, end - start));
                    index = next;
                }
            }
            _revealRanges = chunks.ToArray();
            Document = new FlowDocument(_paragraph)
            {
                PagePadding = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 13,
                LineHeight = 21, Foreground = MessageBrush
            };
        }

        public CommitMessagePresentation Presentation { get; set; }
        public FlowDocument Document { get; }
        public int Offset { get; private set; }
        public Hyperlink? HistoryToggle { get; private set; }
        public List<Brush> RevealBrushes { get; } = [];

        public void AppendHistoryToggle(RoutedEventHandler click)
        {
            if (Presentation.History is null || HistoryToggle is not null) return;
            HistoryToggle = new Hyperlink(new Run(Presentation.HistoryExpanded ? "Collapse" : "Expand"))
            {
                Foreground = FrozenBrush("#AAC8F5"), FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Focusable = true,
                ToolTip = Presentation.HistoryExpanded ? "Show newest three AI updates" : "Show older AI updates"
            };
            HistoryToggle.Click += click;
            _paragraph.Inlines.Add(new LineBreak());
            _paragraph.Inlines.Add(new LineBreak());
            _paragraph.Inlines.Add(HistoryToggle);
            if (Presentation.HistoryExpanded) _paragraph.Inlines.Add(new LineBreak());
        }

        public void AppendRun(RoutedEventHandler historyClick)
        {
            if (Presentation.History is { } history && Offset == history.RecentLength)
                AppendHistoryToggle(historyClick);
            var text = Presentation.Text;
            var ranges = Presentation.UnreadRanges;
            while (_rangeIndex < ranges.Count && ranges[_rangeIndex].Start + ranges[_rangeIndex].Length <= Offset) _rangeIndex++;
            var range = _rangeIndex < ranges.Count ? ranges[_rangeIndex] : null;
            var unread = range is not null && Offset >= range.Start;
            var boundary = range is null ? text.Length : unread ? range.Start + range.Length : range.Start;
            while (_revealIndex < _revealRanges.Length
                && _revealRanges[_revealIndex].Start + _revealRanges[_revealIndex].Length <= Offset) _revealIndex++;
            var revealRange = _revealIndex < _revealRanges.Length ? _revealRanges[_revealIndex] : null;
            var reveal = revealRange is not null && Offset >= revealRange.Start;
            if (revealRange is not null)
                boundary = Math.Min(boundary, reveal ? revealRange.Start + revealRange.Length : revealRange.Start);
            if (Presentation.History is { } pendingHistory && HistoryToggle is null && Offset < pendingHistory.RecentLength)
                boundary = Math.Min(boundary, pendingHistory.RecentLength);
            var end = Math.Min(boundary, Offset + Math.Min(2048, text.Length - Offset));
            // Do not split a surrogate pair or a CRLF when chunking a long run.
            if (end < boundary && end < text.Length && end > Offset
                && (char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])
                    || text[end] == '\n' && text[end - 1] == '\r')) end--;
            if (end == Offset) end = Math.Min(boundary, Offset + 2);
            var foreground = unread ? UnreadBrush : MessageBrush;
            if (reveal)
            {
                foreground = foreground.Clone();
                RevealBrushes.Add(foreground);
            }
            _paragraph.Inlines.Add(new Run(text[Offset..end])
            {
                Foreground = foreground,
                TextDecorations = unread ? TextDecorations.Underline : null
            });
            Offset = end;
        }
    }
}
