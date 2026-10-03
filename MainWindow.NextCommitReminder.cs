using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private CommitMessageReminder? _nextCommitReminder;
    private readonly ReminderSound _nextCommitReminderSound = new();
    private CommitMessageReminderScope? _nextCommitReminderScope;
    private bool _nextCommitPrReminderEnabled;

    public bool NextCommitPrReminderEnabled
    {
        get => _nextCommitPrReminderEnabled;
        set
        {
            if (_nextCommitPrReminderEnabled == value) return;
            _nextCommitPrReminderEnabled = value;
            Changed(nameof(NextCommitPrReminderEnabled));
            PresentNextCommit();
        }
    }

    private void InitializeNextCommitReminder()
    {
        _nextCommitReminder = new(new DispatcherCommitMessageReminderTimer(Dispatcher),
            PlayNextCommitReminder, refreshState: PresentNextCommit);
        Loaded += NextCommitReminder_Loaded;
        Unloaded += NextCommitReminder_Unloaded;
        IsVisibleChanged += NextCommitReminder_IsVisibleChanged;
        StateChanged += NextCommitReminder_StateChanged;
        NextCommitPanel.IsVisibleChanged += NextCommitReminder_IsVisibleChanged;
        Closed += NextCommitReminder_Closed;
    }

    private void UpdateNextCommitReminder()
    {
        if (_nextCommitReminder is null || _updatingNextCommitRepositories) return;
        var selected = SelectedNextCommitRepository;
        CommitMessageReminderScope? scope = SelectedProject is { } project
            && selected is { Branch: not null, ConnectionId: not null }
            ? new(project.Id, selected.RepositoryRoot.ToUpperInvariant(), selected.Branch,
                selected.ConnectionId, selected.RemoteName) : null;
        var active = IsLoaded && IsVisible && WindowState != WindowState.Minimized
            && NextCommitPanel.IsVisible && !_closeRequested && !_closing && !_closed;
        if (_nextCommitReminder.IsArmed && (scope != _nextCommitReminderScope || !active
            || !NextCommitPrReminderEnabled || NextCommitMessage.Length <= CommitMessageReminder.CharacterThreshold))
            _nextCommitReminderSound.Stop();
        _nextCommitReminderScope = scope;
        _nextCommitReminder.Update(scope, NextCommitMessage, NextCommitPrReminderEnabled, active);
    }

    private void SuspendNextCommitReminder()
    {
        _nextCommitReminder?.Update(null, NextCommitMessage, NextCommitPrReminderEnabled, active: false);
        _nextCommitReminderScope = null;
        _nextCommitReminderSound.Stop();
    }

    private void PlayNextCommitReminder()
    {
        Notice = _nextCommitReminderSound.Play()
            ? "PR reminder: the pending commit message exceeds 3,000 characters. Consider making a PR."
            : "PR reminder: the pending commit message exceeds 3,000 characters, but its sound could not play.";
    }

    private void NextCommitReminder_Loaded(object sender, RoutedEventArgs e) => PresentNextCommit();
    private void NextCommitReminder_Unloaded(object sender, RoutedEventArgs e) => SuspendNextCommitReminder();
    private void NextCommitReminder_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateNextCommitReminder();
    private void NextCommitReminder_StateChanged(object? sender, EventArgs e) => PresentNextCommit();

    private void NextCommitReminder_Closed(object? sender, EventArgs e)
    {
        Loaded -= NextCommitReminder_Loaded;
        Unloaded -= NextCommitReminder_Unloaded;
        IsVisibleChanged -= NextCommitReminder_IsVisibleChanged;
        StateChanged -= NextCommitReminder_StateChanged;
        NextCommitPanel.IsVisibleChanged -= NextCommitReminder_IsVisibleChanged;
        Closed -= NextCommitReminder_Closed;
        _nextCommitReminder?.Dispose();
        _nextCommitReminderSound.Dispose();
    }
}
