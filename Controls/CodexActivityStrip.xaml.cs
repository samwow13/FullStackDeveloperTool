using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.CodexMonitor;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>A passive, in-memory view of local Codex work, independent of selected projects and alert preferences.</summary>
public partial class CodexActivityStrip : UserControl, INotifyPropertyChanged
{
    private const double CardGap = 8;
    private const double MinimumCardWidth = 220;
    private const double AutomaticCardWidth = 318;
    public static readonly DependencyProperty VisibleAgentsProperty = DependencyProperty.Register(
        nameof(VisibleAgents), typeof(int), typeof(CodexActivityStrip),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, VisibleAgentsChanged),
        value => (int)value is 0 or 1 or 2 or 3 or 4 or 6);

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
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
    private bool _compactHeader;
    private bool _narrowHeader;
    private double _cardWidth = AutomaticCardWidth;
    private int _effectiveVisibleAgents = 1;
    private bool _restoreCardPositionPending;
    private int _firstVisibleCard;

    public int VisibleAgents { get => (int)GetValue(VisibleAgentsProperty); set => SetValue(VisibleAgentsProperty, value); }
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
    public bool CanForceClear => !_initializing && !_clearing && !_resetting && _feed is not null;
    public bool CompactHeader => _compactHeader;
    public bool NarrowHeader => _narrowHeader;
    public string ActivitySummary
    {
        get
        {
            if (_resetting) return "Resetting tracking…";
            if (_initializing) return "Checking activity…";
            if (_readFailed || _snapshot?.IsAvailable != true) return "Status unavailable";
            var running = Agents.Count(agent => agent.IsRunning);
            var parts = new List<string> { $"{running} chat{(running == 1 ? "" : "s")} running" };
            var waiting = Agents.Count(agent => agent.IsWaiting);
            var needsInput = Agents.Count(agent => agent.NeedsInput);
            var sleeping = Agents.Count(agent => agent.IsSleeping);
            var failed = Agents.Count(agent => agent.IsFailed);
            var unknown = Math.Max(_snapshot.UnknownCount, Agents.Count(agent => agent.IsUnknown));
            if (waiting > 0) parts.Add($"{waiting} waiting");
            if (needsInput > 0) parts.Add($"{needsInput} needs answer");
            if (sleeping > 0) parts.Add($"{sleeping} asleep");
            if (failed > 0) parts.Add($"{failed} stopped");
            if (unknown > 0) parts.Add($"{unknown} unknown");
            return string.Join(" · ", parts);
        }
    }
    public string ActivityHint => _resetting ? "Saving a fresh starting point"
        : _resetError is not null ? "Force Clear failed · tracking preserved"
        : _initializing ? "Listening for local work"
        : _readFailed || _snapshot?.IsAvailable != true ? "Live activity cannot be confirmed"
        : HasAgents ? "Local status · saved chat updates" : _snapshot?.UnknownCount > 0
            ? "Some local chats have unconfirmed status" : _snapshot?.ResetAt is not null
                ? "Watching for new chats and new turns" : "Ready when you are";
    public string ActivityDetail => _resetError ?? (_readFailed ? "Local Codex activity could not be read. The launcher will retry automatically."
        : _snapshot?.StatusText ?? "Reading local Codex activity without changing tasks.");
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
        Loaded += Strip_Loaded;
        Unloaded += Strip_Unloaded;
        IsVisibleChanged += (_, _) => UpdateAnimations();
        AgentScroller.ScrollChanged += (_, _) => { UpdateCardSize(); UpdateAnimations(); };
        AgentScroller.SizeChanged += (_, _) => { UpdateCardSize(); UpdateAnimations(); };
        SizeChanged += (_, _) =>
        {
            ViewMenu.IsOpen = false;
            UpdateCardSize();
            var compact = ActualWidth < 640;
            if (_compactHeader != compact)
            {
                _compactHeader = compact;
                PropertyChanged?.Invoke(this, new(nameof(CompactHeader)));
            }
            var narrow = ActualWidth < 540;
            if (_narrowHeader != narrow)
            {
                _narrowHeader = narrow;
                PropertyChanged?.Invoke(this, new(nameof(NarrowHeader)));
            }
        };
    }

    private void Watcher_Click(object sender, RoutedEventArgs e) => WatcherRequested?.Invoke(this, e);

    private async void Strip_Loaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null) return;
        _lifetime = new CancellationTokenSource();
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
        ViewMenu.IsOpen = false;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
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
        if (_hostWindow is not { IsActive: true }) ViewMenu.IsOpen = false;
        UpdateAnimations();
    }
    private void HostLocationChanged(object? sender, EventArgs e) => ViewMenu.IsOpen = false;
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
        _snapshot = snapshot;
        _initializing = false;
        var ids = snapshot.Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = Agents.Count - 1; index >= 0; index--)
            if (!ids.Contains(Agents[index].Id)) Agents.RemoveAt(index);
        for (var index = 0; index < snapshot.Agents.Count; index++)
        {
            var value = snapshot.Agents[index];
            var existing = Agents.FirstOrDefault(agent => agent.Id == value.Id);
            if (existing is null) Agents.Insert(index, new ActivityCard(value));
            else
            {
                existing.Update(value);
                var oldIndex = Agents.IndexOf(existing);
                if (oldIndex != index) Agents.Move(oldIndex, index);
            }
        }
        NotifyView();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_clearing || _resetting || _feed is null || _lifetime is null) return;
        _clearing = true;
        NotifyView();
        try
        {
            // Wait for an in-flight read before dismissal so its captured rows cannot reappear.
            while (_refreshing && _lifetime is not null) await Task.Delay(40);
            if (_lifetime is null) return;
            Apply(_feed.ClearRecent());
        }
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

    private void View_Click(object sender, RoutedEventArgs e) => OpenViewMenu();

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.F4)) return;
        OpenViewMenu();
        e.Handled = true;
    }

    private void OpenViewMenu()
    {
        UpdateViewMenu();
        ViewMenu.PlacementTarget = ViewButton;
        ViewMenu.IsOpen = true;
    }

    private void UpdateViewMenu()
    {
        foreach (var item in ViewMenu.Items.OfType<MenuItem>())
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var count) && count == VisibleAgents;
    }

    private void ViewMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateViewMenu();
        ViewMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsChecked)?.Focus();
    }

    private void ViewMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ViewMenu.IsOpen = false;
        ViewButton.Focus();
        e.Handled = true;
    }

    private void ViewCount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !int.TryParse(item.Tag?.ToString(), out var count)) return;
        SetCurrentValue(VisibleAgentsProperty, count);
        UpdateViewMenu();
        ViewMenu.IsOpen = false;
        ViewButton.Focus();
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
        public ActivityCard(CodexActivityAgent value)
        {
            _value = value;
            UpdateSubagents();
        }
        public ObservableCollection<ActivityCard> Subagents { get; } = [];
        public bool HasSubagents => Subagents.Count > 0;
        public string SubagentSummary => $"{Subagents.Count} subagent{(Subagents.Count == 1 ? "" : "s")}. Finished robots stay asleep. Hover each robot for its task and status.";
        public string Id => _value.Id;
        public string Title => _value.Title;
        public string TaskSummary => _value.TaskSummary;
        public string ProjectLabel => _value.ProjectLabel;
        public string Detail => _unavailable ? "Status could not be refreshed. Completion is unconfirmed." : _value.Detail;
        private AgentRunState EffectiveState => _unavailable ? AgentRunState.Unknown : _value.State;
        public string State => EffectiveState.ToString();
        public string StateText => _unavailable ? "Status unavailable" : _value.StateText;
        public bool IsRunning => EffectiveState == AgentRunState.Running;
        public bool IsWaiting => EffectiveState == AgentRunState.Waiting;
        public bool NeedsInput => EffectiveState == AgentRunState.NeedsInput;
        public bool IsSleeping => EffectiveState == AgentRunState.Completed;
        public bool IsFailed => EffectiveState == AgentRunState.Failed;
        public bool IsUnknown => EffectiveState is AgentRunState.Unknown or AgentRunState.Idle;
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
        public string AccessibleDescription => $"{Title}. {StateText}. {TaskSummary}. Workspace folder: {ProjectLabel}." +
            (HasSubagents ? $" {Subagents.Count} subagents tracked within this chat. Finished robots stay asleep." : "");
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
        public void Update(CodexActivityAgent value)
        {
            if (_value == value && !_unavailable) return;
            _value = value;
            _unavailable = false;
            UpdateSubagents();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        public void MarkUnavailable()
        {
            _unavailable = true;
            foreach (var subagent in Subagents) subagent.MarkUnavailable();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private void UpdateSubagents()
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
                    existing = new ActivityCard(value) { Animate = _animate };
                    Subagents.Insert(index, existing);
                }
                else
                {
                    existing.Update(value);
                    var oldIndex = Subagents.IndexOf(existing);
                    if (oldIndex != index) Subagents.Move(oldIndex, index);
                    existing.Animate = _animate;
                }
            }
        }
    }
}

