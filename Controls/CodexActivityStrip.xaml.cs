using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.CodexMonitor;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>An in-memory view of local Codex work with explicit chat replies, independent of selected projects and alert preferences.</summary>
public partial class CodexActivityStrip : UserControl, INotifyPropertyChanged
{
    private const double CardGap = 8;
    private const double MinimumCardWidth = 220;
    private const double AutomaticCardWidth = 318;
    public static readonly DependencyProperty VisibleAgentsProperty = DependencyProperty.Register(
        nameof(VisibleAgents), typeof(int), typeof(CodexActivityStrip),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, VisibleAgentsChanged),
        value => (int)value is 0 or 1 or 2 or 3 or 4 or 6);
    public static readonly DependencyProperty ThoughtBubblesEnabledProperty = DependencyProperty.Register(
        nameof(ThoughtBubblesEnabled), typeof(bool), typeof(CodexActivityStrip),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, BubblePreferenceChanged));
    public static readonly DependencyProperty ThoughtBubbleSecondsProperty = DependencyProperty.Register(
        nameof(ThoughtBubbleSeconds), typeof(int), typeof(CodexActivityStrip),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, BubblePreferenceChanged),
        value => (int)value is >= 3 and <= 120);
    public static readonly DependencyProperty AccountUsageProperty = DependencyProperty.Register(
        nameof(AccountUsage), typeof(CodexAccountUsageSnapshot), typeof(CodexActivityStrip), new PropertyMetadata(null));

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _bubbleTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private CodexActivityFeedService? _feed;
    private CodexActivityFeedSnapshot? _snapshot;
    private CancellationTokenSource? _lifetime;
    private Window? _hostWindow;
    private bool _refreshing;
    private bool _clearing;
    private bool _resetting;
    private string? _resetError;
    private bool _initializing = true;
    private bool _readFailed;
    private double _cardWidth = AutomaticCardWidth;
    private int _effectiveVisibleAgents = 1;
    private bool _restoreCardPositionPending;
    private int _firstVisibleCard;
    private bool _hasAppliedSnapshot;
    private readonly HashSet<Popup> _thoughtPopups = [];
    private readonly Dictionary<Popup, ThoughtPopupPlacement> _thoughtPopupPlacements = [];
    private readonly Dictionary<Popup, Point> _thoughtAnchorPositions = [];
    private bool _updatingThoughtPopups;
    private Popup? _hoverThoughtPopup;
    private object? _hoverThoughtContext;
    private DateTimeOffset? _hoverThoughtLeaveAt;

    public int VisibleAgents { get => (int)GetValue(VisibleAgentsProperty); set => SetValue(VisibleAgentsProperty, value); }
    public bool ThoughtBubblesEnabled { get => (bool)GetValue(ThoughtBubblesEnabledProperty); set => SetValue(ThoughtBubblesEnabledProperty, value); }
    public int ThoughtBubbleSeconds { get => (int)GetValue(ThoughtBubbleSecondsProperty); set => SetValue(ThoughtBubbleSecondsProperty, value); }
    public CodexAccountUsageSnapshot? AccountUsage { get => (CodexAccountUsageSnapshot?)GetValue(AccountUsageProperty); set => SetValue(AccountUsageProperty, value); }
    public double CardWidth => _cardWidth;
    public string ViewDescription => VisibleAgents == 0
        ? "Auto fits chat cards to the panel. Scroll to see more chats. Subagents stay inside their chat's card."
        : _effectiveVisibleAgents < VisibleAgents
            ? $"{VisibleAgents} chats selected; this panel currently fits {_effectiveVisibleAgents}. Widen the panel to show more at once. All chats and subagents remain tracked."
            : $"Show up to {VisibleAgents} chat{(VisibleAgents == 1 ? "" : "s")} at once. Scroll to see more. Subagents stay inside their chat's card.";

    public ObservableCollection<ActivityCard> Agents { get; } = [];
    public bool HasAgents => Agents.Count != 0;
    public bool IsEmpty => !HasAgents;
    public bool CanClear => !_clearing && !_resetting && Agents.Any(agent => agent.CanClear);
    public bool CanDismissCards => !_clearing && !_resetting && !_readFailed && _snapshot?.IsAvailable == true &&
        _feed is not null && _lifetime is not null;
    public bool GoodbyeWaveEnabled => SystemParameters.ClientAreaAnimation;
    public bool CanForceClear => !_initializing && !_clearing && !_resetting && _feed is not null;
    public string? ActivityError => _resetError ?? (!_initializing && (_readFailed || _snapshot?.IsAvailable == false)
        ? "Local Codex activity could not be read. The launcher will retry automatically." : null);
    public bool HasActivityError => !string.IsNullOrWhiteSpace(ActivityError);
    public string ActivityDetail => (_resetError ?? (_readFailed ? "Local Codex activity could not be read. The launcher will retry automatically."
        : _snapshot?.StatusText ?? "Reading local Codex activity without changing tasks."));
    public string EmptyTitle => _initializing ? "Waking up the crew…"
        : _readFailed || _snapshot?.IsAvailable != true ? "Activity unavailable"
        : _snapshot.UnknownCount > 0 ? "No confirmed active agents" : "Quiet keyboards";
    public string EmptyMessage => _initializing ? "Finding active chats and their agents."
        : _readFailed || _snapshot?.IsAvailable != true ? "Keep Codex open. Local activity will reconnect automatically."
        : _snapshot.UnknownCount > 0 ? "Older chats have unknown status. Newly observed work appears here."
        : _snapshot.ResetAt is not null ? "Tracking reset. New chats and new turns will appear here."
        : "Your next Codex task will appear here.";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event RoutedEventHandler? WatcherRequested;

    public CodexActivityStrip()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
        _bubbleTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var agent in Agents) agent.ExpireBubbles(now);
            ExpireThoughtHover(now);
            UpdateBubbleTimer();
        };
        Loaded += Strip_Loaded;
        Unloaded += Strip_Unloaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CloseMenus();
                HideThoughtPopups();
            }
            UpdateAnimations();
        };
        AgentScroller.ScrollChanged += (_, _) => { UpdateCardSize(); UpdateAnimations(); };
        AgentScroller.SizeChanged += (_, _) => { UpdateCardSize(); UpdateAnimations(); };
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                CloseMenus();
                HideThoughtPopups();
            }
            UpdateCardSize();
        };
        // Popups live in separate windows; keep their anchors current without contributing to card measurement.
        LayoutUpdated += (_, _) =>
        {
            if (_updatingThoughtPopups) return;
            // Popup measurement can trigger layout itself. Reflow only when a dashboard anchor actually moves.
            if (_thoughtPopups.Any(popup => popup.PlacementTarget is FrameworkElement { IsLoaded: true, IsVisible: true } anchor
                && IsAncestorOf(anchor) && (!_thoughtAnchorPositions.TryGetValue(popup, out var previous)
                    || previous != anchor.PointToScreen(new Point())))) UpdateThoughtPopups();
        };
    }

    private void Watcher_Click(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        WatcherRequested?.Invoke(this, e);
    }

    private async void Strip_Loaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null) return;
        _lifetime = new CancellationTokenSource();
        SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
        PropertyChanged?.Invoke(this, new(nameof(GoodbyeWaveEnabled)));
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged += HostActivityChanged;
            _hostWindow.Activated += HostActivityChanged;
            _hostWindow.Deactivated += HostActivityChanged;
            _hostWindow.LocationChanged += HostLocationChanged;
        }
        UpdateCardSize();
        _timer.Start();
        await RefreshAsync();
    }

    private void Strip_Unloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _bubbleTimer.Stop();
        HideThoughtPopups();
        CloseMenus();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= HostActivityChanged;
            _hostWindow.Activated -= HostActivityChanged;
            _hostWindow.Deactivated -= HostActivityChanged;
            _hostWindow.LocationChanged -= HostLocationChanged;
            _hostWindow = null;
        }
        UpdateAnimations();
    }

    private void HostActivityChanged(object? sender, EventArgs e)
    {
        if (_hostWindow is not { IsActive: true, WindowState: not WindowState.Minimized })
        {
            CloseMenus();
            HideThoughtPopups();
        }
        UpdateAnimations();
    }

    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(SystemParameters.ClientAreaAnimation))) return;
        void RefreshPreference()
        {
            if (_lifetime is not null) PropertyChanged?.Invoke(this, new(nameof(GoodbyeWaveEnabled)));
        }
        if (Dispatcher.CheckAccess()) RefreshPreference();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshPreference));
    }
    private void HostLocationChanged(object? sender, EventArgs e)
    {
        CloseMenus();
        HideThoughtPopups();
    }
    private async void Timer_Tick(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing || _clearing || _resetting || _lifetime is null) return;
        var token = _lifetime.Token;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome))
                    codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                _feed ??= new CodexActivityFeedService(codexHome,
                    new SettingsStore().SettingsPath + ".codex-crew-reset.json");
                return _feed.ReadSnapshot(token);
            }, token);
            if (token.IsCancellationRequested) return;
            _readFailed = false;
            Apply(snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (token.IsCancellationRequested) return;
            // A failed read must stop the typing animation and never imply successful completion.
            _readFailed = true;
            _initializing = false;
            foreach (var agent in Agents) agent.MarkUnavailable();
            NotifyView();
        }
        finally { _refreshing = false; }
    }

    private void Apply(CodexActivityFeedSnapshot snapshot)
    {
        // Keep new messages queued while the dashboard is inactive; initial history stays suppressed.
        var showFeedback = _hasAppliedSnapshot && snapshot.IsAvailable;
        if (snapshot.IsAvailable) _hasAppliedSnapshot = true;
        _snapshot = snapshot;
        _initializing = false;
        if (!snapshot.IsAvailable)
        {
            foreach (var agent in Agents) agent.MarkUnavailable();
            NotifyView();
            return;
        }
        var ids = snapshot.Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = Agents.Count - 1; index >= 0; index--)
            if (!ids.Contains(Agents[index].Id)) Agents.RemoveAt(index);
        for (var index = 0; index < snapshot.Agents.Count; index++)
        {
            var value = snapshot.Agents[index];
            var existing = Agents.FirstOrDefault(agent => agent.Id == value.Id);
            if (existing is null) Agents.Insert(index, new ActivityCard(value, ThoughtBubblesEnabled, ThoughtBubbleSeconds, showFeedback));
            else
            {
                existing.Update(value, showFeedback);
                var oldIndex = Agents.IndexOf(existing);
                if (oldIndex != index) Agents.Move(oldIndex, index);
            }
        }
        UpdateBubbleTimer();
        NotifyView();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e) => await ClearRecentAsync();

    private async void DismissAgent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ActivityCard { CanClear: true } card } button) return;
        e.Handled = true;
        var restoreFocus = button.IsKeyboardFocusWithin;
        await ClearRecentAsync(card.Id, card.ActivityIdentity);
        if (restoreFocus && !Agents.Contains(card) && IsLoaded) SettingsButton.Focus();
    }

    private async Task ClearRecentAsync(string? agentId = null, string? activityIdentity = null)
    {
        if (_clearing || _resetting || _feed is null || _lifetime is null) return;
        var feed = _feed;
        var lifetime = _lifetime;
        var token = lifetime.Token;
        _clearing = true;
        NotifyView();
        try
        {
            // Wait for an in-flight read before dismissal so its captured rows cannot reappear.
            while (_refreshing) await Task.Delay(40, token);
            if (token.IsCancellationRequested || !ReferenceEquals(lifetime, _lifetime)) return;
            Apply(agentId is null ? feed.ClearRecent() : feed.DismissRecent(agentId, activityIdentity));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _clearing = false;
            NotifyView();
        }
    }

    private async void ForceClear_Click(object sender, RoutedEventArgs e)
    {
        if (!CanForceClear || _feed is null || _lifetime is null) return;
        var token = _lifetime.Token;
        var feed = _feed;
        _resetting = true;
        _resetError = null;
        NotifyView();
        try
        {
            // Serialize with the old read so its captured cards cannot repopulate the reset view.
            while (_refreshing) await Task.Delay(40, token);
            var snapshot = await Task.Run(() => feed.ForceReset(token), token);
            if (token.IsCancellationRequested) return;
            _readFailed = false;
            Apply(snapshot);
            AgentScroller.ScrollToHorizontalOffset(0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException ex)
        {
            if (!token.IsCancellationRequested) _resetError = ex.Message;
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                _resetError = "Tracking could not be reset. Existing tracking is preserved. Try Force Clear again.";
        }
        finally
        {
            _resetting = false;
            NotifyView();
        }
    }

    private void NotifyView()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        UpdateAnimations();
    }

    private static void VisibleAgentsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var strip = (CodexActivityStrip)sender;
        if (strip.AgentScroller is null) return;
        strip.UpdateCardSize();
        strip.UpdateViewMenu();
        strip.PropertyChanged?.Invoke(strip, new(nameof(ViewDescription)));
    }

    private void UpdateCardSize()
    {
        var viewport = AgentScroller.ViewportWidth;
        // The card scroller is collapsed while empty, but its saved choice still describes this panel.
        if (!AgentScroller.IsVisible || viewport <= CardGap) viewport = Math.Max(0, ActualWidth - 30);
        if (!double.IsFinite(viewport) || viewport <= CardGap) return;
        var fits = Math.Max(1, (int)Math.Floor(viewport / (MinimumCardWidth + CardGap)));
        var visible = VisibleAgents == 0 ? Math.Max(1, (int)Math.Floor(viewport / (AutomaticCardWidth + CardGap)))
            : Math.Min(VisibleAgents, fits);
        var width = VisibleAgents == 0 ? Math.Min(AutomaticCardWidth, viewport - CardGap)
            : Math.Floor(viewport / visible) - CardGap;
        if (_effectiveVisibleAgents != visible)
        {
            _effectiveVisibleAgents = visible;
            PropertyChanged?.Invoke(this, new(nameof(ViewDescription)));
        }
        if (Math.Abs(width - _cardWidth) < 0.5) return;
        if (!_restoreCardPositionPending)
            _firstVisibleCard = (int)Math.Floor(AgentScroller.HorizontalOffset / (_cardWidth + CardGap));
        _cardWidth = width;
        PropertyChanged?.Invoke(this, new(nameof(CardWidth)));
        if (_restoreCardPositionPending) return;
        _restoreCardPositionPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreCardPositionPending = false;
            if (!IsLoaded) return;
            AgentScroller.ScrollToHorizontalOffset(_firstVisibleCard * (_cardWidth + CardGap));
            UpdateAnimations();
        }));
    }

    private void UpdateViewMenu()
    {
        foreach (var item in ViewOptionsItem.Items.OfType<MenuItem>())
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var count) && count == VisibleAgents;
    }

    private void ViewCount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !int.TryParse(item.Tag?.ToString(), out var count)) return;
        SetCurrentValue(VisibleAgentsProperty, count);
        UpdateViewMenu();
        CloseMenus();
        SettingsButton.Focus();
    }

    private static void BubblePreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var strip = (CodexActivityStrip)sender;
        foreach (var agent in strip.Agents)
            agent.SetBubblePreferences(strip.ThoughtBubblesEnabled, strip.ThoughtBubbleSeconds);
        if (strip.SettingsMenu is null) return;
        strip.UpdateSettingsMenu();
        strip.UpdateBubbleTimer();
    }

    private void UpdateBubbleTimer()
    {
        if (IsLoaded && IsVisible && _hostWindow is { IsActive: true, WindowState: not WindowState.Minimized }
            && (_hoverThoughtPopup is not null || Agents.Any(agent => agent.HasVisibleBubbles))) _bubbleTimer.Start();
        else _bubbleTimer.Stop();
        UpdateThoughtPopups();
    }

    private void ThoughtPopup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Popup popup) return;
        _thoughtPopups.Add(popup);
        popup.CustomPopupPlacementCallback = (_, _, _) => _thoughtPopupPlacements.TryGetValue(popup, out var placement)
            ? [new CustomPopupPlacement(placement.Offset, PopupPrimaryAxis.None)] : [];
        UpdateThoughtPopups();
    }

    private void ThoughtPopup_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Popup popup) return;
        popup.IsOpen = false;
        if (popup.DataContext is ActivityCard card) card.SetAutomaticBubbleVisible(false, DateTimeOffset.UtcNow);
        _thoughtPopups.Remove(popup);
        _thoughtPopupPlacements.Remove(popup);
        _thoughtAnchorPositions.Remove(popup);
        popup.CustomPopupPlacementCallback = null;
        if (ReferenceEquals(_hoverThoughtPopup, popup)) ClearThoughtHover();
    }

    private void RobotHover_Enter(object sender, MouseEventArgs e)
    {
        var popup = _thoughtPopups.FirstOrDefault(value => ReferenceEquals(value.PlacementTarget, sender));
        if (popup is not null) BeginThoughtHover(popup);
    }

    private void RobotHover_Leave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_hoverThoughtPopup?.PlacementTarget, sender)) DelayThoughtHoverClose();
    }

    private void ThoughtHover_Enter(object sender, MouseEventArgs e)
    {
        var popup = _thoughtPopups.FirstOrDefault(value => ReferenceEquals(value.Child, sender));
        if (popup is not null) BeginThoughtHover(popup);
    }

    private void ThoughtHover_Leave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_hoverThoughtPopup?.Child, sender)) DelayThoughtHoverClose();
    }

    private void PreviousThought_Click(object sender, RoutedEventArgs e) => NavigateThought(sender, -1);
    private void NextThought_Click(object sender, RoutedEventArgs e) => NavigateThought(sender, 1);

    private void ThoughtScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        var popup = _thoughtPopups.FirstOrDefault(value => value.IsOpen && ReferenceEquals(value.DataContext, viewer.DataContext));
        if (popup is not null) BeginThoughtHover(popup);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta * 0.35);
        // Keep wheel input inside the message, including at either end of its scroll range.
        e.Handled = true;
    }

    private void NavigateThought(object sender, int direction)
    {
        if (sender is not FrameworkElement { DataContext: ActivityCard card }) return;
        var popup = _thoughtPopups.FirstOrDefault(value => value.IsOpen && ReferenceEquals(value.DataContext, card));
        if (popup is null) return;
        BeginThoughtHover(popup);
        card.NavigateThought(direction);
        card.MarkThoughtBubblePresented();
        ResetThoughtScroll(popup.Child);
    }

    private void ThoughtMessage_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not TextBlock text || Equals(text.Tag, text.Text)) return;
        text.Tag = text.Text;
        // Passive refreshes preserve scrolling while actual message changes start at the beginning.
        for (var current = sender as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer viewer) { viewer.ScrollToTop(); break; }
    }

    private static void ResetThoughtScroll(DependencyObject? current)
    {
        if (current is null) return;
        if (current is ScrollViewer viewer) { viewer.ScrollToTop(); return; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            ResetThoughtScroll(VisualTreeHelper.GetChild(current, index));
    }

    private void BeginThoughtHover(Popup popup)
    {
        if (popup.DataContext is not ActivityCard card || string.IsNullOrWhiteSpace(card.TaskSummary)) return;
        if (!ReferenceEquals(_hoverThoughtPopup, popup) || !ReferenceEquals(_hoverThoughtContext, card))
        {
            ClearThoughtHover();
            card.ResetThoughtHistory();
        }
        _hoverThoughtPopup = popup;
        _hoverThoughtContext = card;
        _hoverThoughtLeaveAt = null;
        UpdateBubbleTimer();
    }

    private void DelayThoughtHoverClose()
    {
        // Allow the pointer to cross the tail/gap into the scrollable popup.
        _hoverThoughtLeaveAt = DateTimeOffset.UtcNow.AddMilliseconds(350);
        UpdateBubbleTimer();
    }

    private void ExpireThoughtHover(DateTimeOffset now)
    {
        if (_hoverThoughtPopup is not { } popup) return;
        if (!popup.IsLoaded || !ReferenceEquals(popup.DataContext, _hoverThoughtContext)
            || popup.PlacementTarget is not FrameworkElement anchor || !anchor.IsVisible || !IsThoughtAnchorVisible(anchor)
            || (_hoverThoughtLeaveAt is { } deadline && now >= deadline
                && popup.PlacementTarget?.IsMouseOver != true && !IsThoughtPointerActive(popup)))
        {
            ClearThoughtHover();
            return;
        }
        // A layout change can move the pointer out without a matching leave event.
        if (_hoverThoughtLeaveAt is null && anchor.IsMouseOver != true && !IsThoughtPointerActive(popup))
            _hoverThoughtLeaveAt = now.AddMilliseconds(350);
    }

    private static bool IsThoughtPointerActive(Popup popup) =>
        popup.Child is { } child && (child.IsMouseOver || child.IsMouseCaptureWithin);

    private void ClearThoughtHover()
    {
        if (_hoverThoughtContext is ActivityCard card)
        {
            card.ResetThoughtHistory();
            card.DismissAutomaticBubble();
        }
        _hoverThoughtPopup = null;
        _hoverThoughtContext = null;
        _hoverThoughtLeaveAt = null;
    }

    private void HideThoughtPopups()
    {
        ClearThoughtHover();
        var now = DateTimeOffset.UtcNow;
        foreach (var card in EnumerateActivityCards(Agents)) card.SetAutomaticBubbleVisible(false, now);
        foreach (var popup in _thoughtPopups) CloseThoughtPopup(popup);
        _thoughtPopupPlacements.Clear();
        _thoughtAnchorPositions.Clear();
        _bubbleTimer.Stop();
    }

    private void UpdateThoughtPopups()
    {
        if (_updatingThoughtPopups) return;
        _updatingThoughtPopups = true;
        try
        {
            var show = IsLoaded && IsVisible &&
                _hostWindow is { IsActive: true, WindowState: not WindowState.Minimized };
            var candidates = new List<ThoughtPopupCandidate>();
            foreach (var popup in _thoughtPopups.ToArray())
            {
                var anchor = popup.PlacementTarget as FrameworkElement;
                if (anchor is { IsLoaded: true, IsVisible: true } && IsAncestorOf(anchor))
                    _thoughtAnchorPositions[popup] = anchor.PointToScreen(new Point());
                else _thoughtAnchorPositions.Remove(popup);
                var hovered = ReferenceEquals(_hoverThoughtPopup, popup) && ReferenceEquals(popup.DataContext, _hoverThoughtContext);
                var visible = show && popup.IsLoaded && popup.DataContext is ActivityCard { Animate: true } card &&
                    !string.IsNullOrWhiteSpace(card.TaskSummary) &&
                    (hovered || (ThoughtBubblesEnabled && card.HasCurrentStatus && card.HasThoughtBubble)) &&
                    anchor is { IsLoaded: true, IsVisible: true } && IsThoughtAnchorVisible(anchor);
                if (!visible || popup.Child is not FrameworkElement child)
                {
                    CloseThoughtPopup(popup);
                    continue;
                }

                var origin = anchor!.PointToScreen(new Point());
                _thoughtAnchorPositions[popup] = origin;
                var bottom = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
                var dpi = VisualTreeHelper.GetDpi(anchor);
                var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X, (int)origin.Y)).WorkingArea;
                var edge = 8 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
                var workArea = new Rect(area.Left + edge, area.Top + edge,
                    Math.Max(0, area.Width - edge * 2), Math.Max(0, area.Height - edge * 2));
                candidates.Add(new(popup, (ActivityCard)popup.DataContext, child, new Rect(origin, bottom), workArea, dpi, hovered));
            }

            // Hover owns the single bubble. Otherwise finish the visible update, then serve queued updates in order.
            ThoughtPopupCandidate? selected = null;
            ThoughtPopupPlacement? placement = null;
            foreach (var candidate in candidates.OrderByDescending(value => value.Hovered)
                         .ThenByDescending(value => value.Popup.IsOpen)
                         .ThenBy(value => value.Card.ThoughtBubbleRequestedAt)
                         .ThenBy(value => value.Card.Id, StringComparer.Ordinal))
            {
                placement = PlaceThoughtPopup(candidate);
                if (placement is null) continue;
                selected = candidate;
                break;
            }
            // Close the previous window before opening the next, including switches between parents and children.
            foreach (var popup in _thoughtPopups)
                if (!ReferenceEquals(popup, selected?.Popup)) CloseThoughtPopup(popup);
            if (selected is not null && placement is not null)
            {
                var changed = !_thoughtPopupPlacements.TryGetValue(selected.Popup, out var previous) || previous != placement;
                _thoughtPopupPlacements[selected.Popup] = placement;
                selected.Card.SetThoughtBubbleTail(
                    Math.Clamp((selected.AnchorBounds.X + selected.AnchorBounds.Width / 2 - placement.ScreenBounds.X) / selected.Dpi.DpiScaleX - 10,
                        10, Math.Max(10, selected.Child.Width - 47)), placement.TailAbove);
                if (!selected.Popup.IsOpen) selected.Popup.IsOpen = true;
                else if (changed)
                {
                    // Re-run custom placement after scrolling or resizing the dashboard.
                    selected.Popup.HorizontalOffset = 0.01;
                    selected.Popup.HorizontalOffset = 0;
                }
                if (selected.Popup.IsOpen) selected.Card.MarkThoughtBubblePresented();
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var card in EnumerateActivityCards(Agents))
                card.SetAutomaticBubbleVisible(ReferenceEquals(card, selected?.Card) && selected?.Hovered != true, now);
        }
        finally { _updatingThoughtPopups = false; }
    }

    private void CloseThoughtPopup(Popup popup)
    {
        if (popup.IsOpen)
        {
            if (popup.DataContext is ActivityCard card) card.ResetThoughtHistory();
            ResetThoughtScroll(popup.Child);
        }
        popup.IsOpen = false;
        _thoughtPopupPlacements.Remove(popup);
    }

    private ThoughtPopupPlacement? PlaceThoughtPopup(ThoughtPopupCandidate candidate)
    {
        if (!candidate.WorkArea.IntersectsWith(candidate.AnchorBounds)) return null;
        var maximumWidth = candidate.WorkArea.Width / candidate.Dpi.DpiScaleX;
        var gap = 8 * Math.Max(candidate.Dpi.DpiScaleX, candidate.Dpi.DpiScaleY);
        var above = Math.Max(0, candidate.AnchorBounds.Top - candidate.WorkArea.Top - gap);
        var below = Math.Max(0, candidate.WorkArea.Bottom - candidate.AnchorBounds.Bottom - gap);
        // Reserve room for the title, tail and history footer before sizing the scrollable message.
        var maximumBody = Math.Min(102, Math.Max(above, below) / candidate.Dpi.DpiScaleY - 110);
        if (maximumWidth < 80 || maximumBody < 17) return null;
        candidate.Child.Width = Math.Min(300, maximumWidth);
        candidate.Card.SetThoughtBubbleMaxHeight(maximumBody);
        candidate.Child.Measure(new Size(candidate.Child.Width, double.PositiveInfinity));
        var size = new Size(Math.Ceiling(candidate.Child.DesiredSize.Width * candidate.Dpi.DpiScaleX),
            Math.Ceiling(candidate.Child.DesiredSize.Height * candidate.Dpi.DpiScaleY));
        if (size.Width > candidate.WorkArea.Width || size.Height > candidate.WorkArea.Height || size.Height > Math.Max(above, below)) return null;
        var tailAbove = size.Height > above;
        var x = Math.Clamp(candidate.AnchorBounds.X + candidate.AnchorBounds.Width / 2 - 34 * candidate.Dpi.DpiScaleX,
            candidate.WorkArea.Left, candidate.WorkArea.Right - size.Width);
        var y = tailAbove ? candidate.AnchorBounds.Bottom + gap : candidate.AnchorBounds.Top - size.Height - gap;
        y = Math.Clamp(y, candidate.WorkArea.Top, candidate.WorkArea.Bottom - size.Height);
        var bounds = new Rect(new Point(x, y), size);
        return new(new Point((x - candidate.AnchorBounds.X) / candidate.Dpi.DpiScaleX,
            (y - candidate.AnchorBounds.Y) / candidate.Dpi.DpiScaleY), bounds, tailAbove, candidate.AnchorBounds.TopLeft);
    }
    private static IEnumerable<ActivityCard> EnumerateActivityCards(IEnumerable<ActivityCard> cards)
    {
        foreach (var card in cards)
        {
            yield return card;
            foreach (var child in EnumerateActivityCards(card.Subagents)) yield return child;
        }
    }

    private sealed record ThoughtPopupPlacement(Point Offset, Rect ScreenBounds, bool TailAbove, Point AnchorOrigin);
    private sealed record ThoughtPopupCandidate(Popup Popup, ActivityCard Card, FrameworkElement Child, Rect AnchorBounds, Rect WorkArea, DpiScale Dpi, bool Hovered);
    private bool IsThoughtAnchorVisible(FrameworkElement anchor)
    {
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0 || !IsAncestorOf(anchor)) return false;
        for (DependencyObject? current = anchor; current is not null && current != _hostWindow; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ScrollContentPresenter viewport) continue;
            var bounds = anchor.TransformToAncestor(viewport).TransformBounds(new Rect(anchor.RenderSize));
            // Check each viewport, including the nested subagent scroller, before showing its floating bubble.
            if (!new Rect(viewport.RenderSize).IntersectsWith(bounds)) return false;
        }
        return true;
    }

    private void CloseMenus()
    {
        ViewOptionsItem.IsSubmenuOpen = false;
        BubbleDurationItem.IsSubmenuOpen = false;
        SettingsMenu.IsOpen = false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsMenu();
    private void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.F4)) return;
        OpenSettingsMenu();
        e.Handled = true;
    }

    private void OpenSettingsMenu()
    {
        UpdateSettingsMenu();
        SettingsMenu.PlacementTarget = SettingsButton;
        SettingsMenu.IsOpen = true;
    }

    private void UpdateSettingsMenu()
    {
        UpdateViewMenu();
        BubbleEnabledItem.IsChecked = ThoughtBubblesEnabled;
        BubbleDurationItem.IsEnabled = ThoughtBubblesEnabled;
        foreach (var item in BubbleDurationItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var seconds) && seconds == ThoughtBubbleSeconds;
            item.IsEnabled = ThoughtBubblesEnabled;
        }
    }

    private void SettingsMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateSettingsMenu();
        WatcherMenuItem.Focus();
    }

    private void SettingsMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (IsVisible && _hostWindow is { IsActive: true }) SettingsButton.Focus();
    }

    private void SettingsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ViewOptionsItem.IsSubmenuOpen || BubbleDurationItem.IsSubmenuOpen) return;
        CloseMenus();
        SettingsButton.Focus();
        e.Handled = true;
    }

    private void BubbleEnabled_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(ThoughtBubblesEnabledProperty, !ThoughtBubblesEnabled);
        UpdateSettingsMenu();
    }

    private void BubbleDuration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !int.TryParse(item.Tag?.ToString(), out var seconds)) return;
        SetCurrentValue(ThoughtBubbleSecondsProperty, seconds);
        UpdateSettingsMenu();
        CloseMenus();
    }

    private void UpdateAnimations()
    {
        var animate = IsLoaded && IsVisible && _lifetime is not null &&
            _hostWindow is { IsActive: true, WindowState: not WindowState.Minimized };
        var left = AgentScroller.HorizontalOffset;
        var right = left + AgentScroller.ViewportWidth;
        var stride = _cardWidth + CardGap;
        for (var index = 0; index < Agents.Count; index++)
            Agents[index].Animate = animate && index * stride < right && (index + 1) * stride > left;
        UpdateBubbleTimer();
    }

    private void AgentScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A large family scrolls inside its card instead of expanding the dashboard indefinitely.
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != AgentScroller;
             current = current is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(current))
        {
            if (current is not ScrollViewer subagentScroller || subagentScroller.ScrollableHeight <= 0) continue;
            subagentScroller.ScrollToVerticalOffset(subagentScroller.VerticalOffset - e.Delta * 0.35);
            e.Handled = true;
            return;
        }
        if (AgentScroller.ScrollableWidth <= 0) return;
        AgentScroller.ScrollToHorizontalOffset(AgentScroller.HorizontalOffset - e.Delta * 1.5);
        e.Handled = true;
    }

    public sealed class ActivityCard : INotifyPropertyChanged
    {
        private CodexActivityAgent _value;
        private bool _unavailable;
        private bool _animate;
        private bool _bubbleEnabled;
        private int _bubbleSeconds;
        private DateTimeOffset? _bubbleShownAt;
        private DateTimeOffset? _bubbleRequestedAt;
        private bool _bubblePresented;
        private TimeSpan _bubbleElapsed;
        private string? _lastFeedbackIdentity;
        private bool _suppressStartupFeedback;
        private double _thoughtBubbleMaxHeight = 102;
        private double _thoughtBubbleTailLeft = 24;
        private bool _thoughtBubbleTailAbove;
        private IReadOnlyList<CodexActivityMessage> _thoughtMessages = [];
        private string? _selectedThoughtIdentity;
        private string? _bubbleSourceText;
        private string _bubbleDisplayText = "";
        private string _displaySummary = "";
        public ActivityCard(CodexActivityAgent value, bool bubbleEnabled, int bubbleSeconds, bool showFeedback)
        {
            _value = value;
            _lastFeedbackIdentity = value.FeedbackIdentity;
            _suppressStartupFeedback = !showFeedback && IsFeedbackPlaceholder(value.FeedbackIdentity);
            _bubbleEnabled = bubbleEnabled;
            _bubbleSeconds = bubbleSeconds;
            UpdateThoughtHistory();
            if (showFeedback) ShowBubble(value.IsSubagent && value.IsRunning);
            UpdateSubagents(showFeedback);
        }
        public bool HasThoughtBubble => _bubbleRequestedAt is not null;
        public bool IsWaitingToSpeak => !_unavailable && HasThoughtBubble && !_bubblePresented;
        public bool HasCurrentStatus => !_unavailable;
        public DateTimeOffset? ThoughtBubbleRequestedAt => _bubbleRequestedAt;
        public bool HasVisibleBubbles => HasThoughtBubble || Subagents.Any(agent => agent.HasVisibleBubbles);
        public bool HasSubagentThoughtBubbles => Subagents.Any(agent => agent.HasVisibleBubbles);
        private int ThoughtMessageIndex
        {
            get
            {
                if (_selectedThoughtIdentity is null) return _thoughtMessages.Count - 1;
                for (var index = 0; index < _thoughtMessages.Count; index++)
                    if (_thoughtMessages[index].Identity == _selectedThoughtIdentity) return index;
                return _thoughtMessages.Count - 1;
            }
        }
        public string ThoughtBubbleText
        {
            get
            {
                var text = _thoughtMessages.Count == 0 ? _value.TaskSummary : _thoughtMessages[ThoughtMessageIndex].Text;
                if (!string.Equals(_bubbleSourceText, text, StringComparison.Ordinal))
                {
                    _bubbleDisplayText = CodexBubbleTextFormatter.Format(text);
                    _bubbleSourceText = text;
                }
                return _bubbleDisplayText;
            }
        }
        public bool CanShowPreviousThought => ThoughtMessageIndex > 0;
        public bool CanShowNextThought => _thoughtMessages.Count > 0 && ThoughtMessageIndex < _thoughtMessages.Count - 1;
        public string ThoughtMessagePosition => $"{Math.Max(1, ThoughtMessageIndex + 1)} / {Math.Max(1, _thoughtMessages.Count)}";
        public double ThoughtBubbleMaxHeight => _thoughtBubbleMaxHeight;
        public Thickness ThoughtBubbleTailMargin => new(_thoughtBubbleTailLeft, 0, 0, 0);
        public Dock ThoughtBubbleTailDock => _thoughtBubbleTailAbove ? Dock.Top : Dock.Bottom;
        public double ThoughtBubbleTailScaleY => _thoughtBubbleTailAbove ? -1 : 1;
        public ObservableCollection<ActivityCard> Subagents { get; } = [];
        public bool HasSubagents => Subagents.Count > 0;
        public string SubagentSummary => $"{Subagents.Count} subagent{(Subagents.Count == 1 ? "" : "s")}. Robots wave for new messages and rest after speaking. Hover each robot for its latest saved message.";
        public string Id => _value.Id;
        public string? ActivityIdentity => _value.ActivityIdentity;
        public string Title => _value.Title;
        public string TaskSummary => _displaySummary;
        public string Detail => _unavailable ? "Status could not be refreshed. Completion is unconfirmed." : _value.Detail;
        private AgentRunState EffectiveState => _unavailable ? AgentRunState.Unknown : _value.State;
        public string State => EffectiveState.ToString();
        public string StateText => _unavailable ? "Status unavailable" : _value.StateText;
        public bool IsRunning => EffectiveState == AgentRunState.Running;
        public bool CanClear => !_unavailable && _value.CanDismiss;
        public string StateColor => EffectiveState switch
        {
            AgentRunState.Running => "#72E6CB",
            AgentRunState.Completed => "#A6B7EB",
            AgentRunState.Waiting => "#E9C681",
            AgentRunState.NeedsInput => "#E9C681",
            AgentRunState.Failed => "#F5A1A5",
            _ => "#A5B4C7"
        };
        public string CardBorder => EffectiveState switch
        {
            AgentRunState.Running => "#315A57",
            AgentRunState.Completed => "#39465C",
            AgentRunState.Waiting => "#544B38",
            AgentRunState.NeedsInput => "#544B38",
            AgentRunState.Failed => "#66404A",
            _ => "#2D3A4C"
        };
        public string AccessibleDescription => $"{Title}. {StateText}. {TaskSummary}." +
            (IsWaitingToSpeak ? " New message waiting to speak." : "") +
            (HasSubagents ? $" {Subagents.Count} subagents tracked within this chat." : "");
        public bool Animate
        {
            get => _animate;
            set
            {
                if (_animate == value) return;
                _animate = value;
                foreach (var subagent in Subagents) subagent.Animate = value;
                PropertyChanged?.Invoke(this, new(nameof(Animate)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(CodexActivityAgent value, bool showFeedback)
        {
            if (_value == value && !_unavailable) return;
            var newActivity = (value.ActivityIdentity is not null && _value.ActivityIdentity is not null
                    && value.ActivityIdentity != _value.ActivityIdentity)
                || (value.TurnId is not null && _value.TurnId is not null && value.TurnId != _value.TurnId);
            if (newActivity)
            {
                _suppressStartupFeedback = false;
                _lastFeedbackIdentity = null;
            }
            var suppressCurrentFeedback = _suppressStartupFeedback;
            var newFeedback = value.FeedbackIdentity is not null && value.FeedbackIdentity != _lastFeedbackIdentity;
            var consumeStartupFeedback = _suppressStartupFeedback && newFeedback && !IsFeedbackPlaceholder(value.FeedbackIdentity);
            if (consumeStartupFeedback) _suppressStartupFeedback = false;
            if (value.FeedbackIdentity is not null) _lastFeedbackIdentity = value.FeedbackIdentity;
            _value = value;
            UpdateThoughtHistory();
            _unavailable = false;
            var newChildStart = newActivity && value.IsSubagent && value.IsRunning;
            if (!showFeedback || (value.FeedbackIdentity is null && !newChildStart)) ClearOwnBubble();
            else if ((newFeedback && !suppressCurrentFeedback && !consumeStartupFeedback) || newChildStart) ShowBubble(newChildStart);
            UpdateSubagents(showFeedback);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        public void MarkUnavailable()
        {
            _unavailable = true;
            SetAutomaticBubbleVisible(false, DateTimeOffset.UtcNow);
            foreach (var subagent in Subagents) subagent.MarkUnavailable();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private static bool IsFeedbackPlaceholder(string? identity) => identity is null || identity.StartsWith("subagent-start:", StringComparison.Ordinal);

        private void UpdateThoughtHistory()
        {
            _thoughtMessages = _value.MessageHistory;
            _displaySummary = CodexBubbleTextFormatter.Format(_value.TaskSummary);
            if (_selectedThoughtIdentity is not null && !_thoughtMessages.Any(message => message.Identity == _selectedThoughtIdentity))
                _selectedThoughtIdentity = _thoughtMessages.FirstOrDefault()?.Identity;
        }

        public void NavigateThought(int direction)
        {
            if (_thoughtMessages.Count == 0) return;
            var index = Math.Clamp(ThoughtMessageIndex + direction, 0, _thoughtMessages.Count - 1);
            _selectedThoughtIdentity = index == _thoughtMessages.Count - 1 ? null : _thoughtMessages[index].Identity;
            NotifyThoughtHistory();
        }

        public void ResetThoughtHistory()
        {
            if (_selectedThoughtIdentity is null) return;
            _selectedThoughtIdentity = null;
            NotifyThoughtHistory();
        }

        private void NotifyThoughtHistory()
        {
            PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleText)));
            PropertyChanged?.Invoke(this, new(nameof(CanShowPreviousThought)));
            PropertyChanged?.Invoke(this, new(nameof(CanShowNextThought)));
            PropertyChanged?.Invoke(this, new(nameof(ThoughtMessagePosition)));
        }

        public void DismissAutomaticBubble()
        {
            // Leaving older history must not consume a newer message that has never appeared.
            if (!_bubblePresented || !ClearOwnBubble()) return;
            PropertyChanged?.Invoke(this, new(nameof(HasThoughtBubble)));
        }

        public void MarkThoughtBubblePresented()
        {
            if (!HasThoughtBubble || _bubblePresented || _selectedThoughtIdentity is not null) return;
            _bubblePresented = true;
            PropertyChanged?.Invoke(this, new(nameof(IsWaitingToSpeak)));
            PropertyChanged?.Invoke(this, new(nameof(AccessibleDescription)));
        }

        private void ShowBubble(bool childStart = false)
        {
            if (!_bubbleEnabled || (_value.FeedbackIdentity is null && !childStart)
                || string.IsNullOrWhiteSpace(_value.TaskSummary) || _value.TaskSummary == "Reading saved chat…") return;
            _bubbleRequestedAt = DateTimeOffset.UtcNow;
            _bubblePresented = false;
            _bubbleShownAt = null;
            _bubbleElapsed = TimeSpan.Zero;
        }

        public void SetAutomaticBubbleVisible(bool visible, DateTimeOffset now)
        {
            if (!HasThoughtBubble) return;
            if (visible) _bubbleShownAt ??= now;
            else if (_bubbleShownAt is { } shownAt)
            {
                _bubbleElapsed += now - shownAt;
                _bubbleShownAt = null;
            }
        }

        public void SetThoughtBubbleMaxHeight(double height)
        {
            if (Math.Abs(_thoughtBubbleMaxHeight - height) < 0.1) return;
            _thoughtBubbleMaxHeight = height;
            PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleMaxHeight)));
        }

        public void SetThoughtBubbleTail(double left, bool above)
        {
            if (Math.Abs(_thoughtBubbleTailLeft - left) >= 0.1)
            {
                _thoughtBubbleTailLeft = left;
                PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleTailMargin)));
            }
            if (_thoughtBubbleTailAbove == above) return;
            _thoughtBubbleTailAbove = above;
            PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleTailDock)));
            PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleTailScaleY)));
        }

        private bool ClearOwnBubble()
        {
            var hadBubble = HasThoughtBubble;
            _bubbleRequestedAt = null;
            _bubblePresented = false;
            _bubbleShownAt = null;
            _bubbleElapsed = TimeSpan.Zero;
            if (hadBubble)
            {
                PropertyChanged?.Invoke(this, new(nameof(IsWaitingToSpeak)));
                PropertyChanged?.Invoke(this, new(nameof(AccessibleDescription)));
            }
            return hadBubble;
        }

        public void SetBubblePreferences(bool enabled, int seconds)
        {
            _bubbleEnabled = enabled;
            _bubbleSeconds = seconds;
            foreach (var subagent in Subagents) subagent.SetBubblePreferences(enabled, seconds);
            if (!enabled) HideBubbles();
            else ExpireBubbles(DateTimeOffset.UtcNow);
            PropertyChanged?.Invoke(this, new(nameof(HasSubagentThoughtBubbles)));
        }

        public void ExpireBubbles(DateTimeOffset now)
        {
            var hadSubagentBubbles = HasSubagentThoughtBubbles;
            if (HasThoughtBubble && _bubbleElapsed + (_bubbleShownAt is { } shownAt ? now - shownAt : TimeSpan.Zero)
                    >= TimeSpan.FromSeconds(_bubbleSeconds))
            {
                ClearOwnBubble();
                PropertyChanged?.Invoke(this, new(nameof(HasThoughtBubble)));
                PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleText)));
            }
            foreach (var subagent in Subagents) subagent.ExpireBubbles(now);
            if (hadSubagentBubbles != HasSubagentThoughtBubbles)
                PropertyChanged?.Invoke(this, new(nameof(HasSubagentThoughtBubbles)));
        }

        public void HideBubbles()
        {
            var hadSubagentBubbles = HasSubagentThoughtBubbles;
            if (ClearOwnBubble())
            {
                PropertyChanged?.Invoke(this, new(nameof(HasThoughtBubble)));
                PropertyChanged?.Invoke(this, new(nameof(ThoughtBubbleText)));
            }
            foreach (var subagent in Subagents) subagent.HideBubbles();
            if (hadSubagentBubbles != HasSubagentThoughtBubbles)
                PropertyChanged?.Invoke(this, new(nameof(HasSubagentThoughtBubbles)));
        }

        private void UpdateSubagents(bool showFeedback)
        {
            var ids = _value.Subagents.Select(subagent => subagent.Id).ToHashSet(StringComparer.Ordinal);
            for (var index = Subagents.Count - 1; index >= 0; index--)
                if (!ids.Contains(Subagents[index].Id)) Subagents.RemoveAt(index);
            for (var index = 0; index < _value.Subagents.Count; index++)
            {
                var value = _value.Subagents[index];
                var existing = Subagents.FirstOrDefault(subagent => subagent.Id == value.Id);
                if (existing is null)
                {
                    existing = new ActivityCard(value, _bubbleEnabled, _bubbleSeconds, showFeedback) { Animate = _animate };
                    Subagents.Insert(index, existing);
                }
                else
                {
                    existing.Update(value, showFeedback);
                    var oldIndex = Subagents.IndexOf(existing);
                    if (oldIndex != index) Subagents.Move(oldIndex, index);
                    existing.Animate = _animate;
                }
            }
        }
    }
}

