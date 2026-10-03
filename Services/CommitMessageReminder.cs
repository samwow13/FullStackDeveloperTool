namespace FullStackLauncher.Services;

public interface ICommitMessageReminderTimer : IDisposable
{
    event EventHandler? Tick;
    void Start(TimeSpan interval);
    void Stop();
}

public readonly record struct CommitMessageReminderScope(string ProjectId, string RepositoryRoot,
    string Branch, string ConnectionId, string? RemoteName);

/// <summary>Session-only reminder for the full message text, independent of Git diff size.</summary>
/// <remarks>All calls and timer ticks run on the owning UI thread.</remarks>
public sealed class CommitMessageReminder : IDisposable
{
    public const int CharacterThreshold = 3_000;
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    private readonly ICommitMessageReminderTimer _timer;
    private readonly Action _notify;
    private readonly Action? _refreshState;
    private readonly TimeProvider _timeProvider;
    private CommitMessageReminderScope? _scope;
    private long _lastReminderTimestamp;
    private bool _disposed;

    public bool IsArmed { get; private set; }

    public CommitMessageReminder(ICommitMessageReminderTimer timer, Action notify,
        TimeProvider? timeProvider = null, Action? refreshState = null)
    {
        _timer = timer;
        _notify = notify;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshState = refreshState;
        _timer.Tick += OnTick;
    }

    public void Update(CommitMessageReminderScope? scope, string message, bool enabled, bool active)
    {
        if (_disposed) return;
        if (!enabled || !active || scope is null || message.Length <= CharacterThreshold)
        {
            Disarm();
            return;
        }
        // Ordinary polling and ongoing edits above the threshold retain the countdown.
        if (IsArmed && _scope == scope) return;
        Disarm();
        _scope = scope;
        _lastReminderTimestamp = _timeProvider.GetTimestamp();
        IsArmed = true;
        _timer.Start(ReminderInterval);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsArmed) return;
        // Re-read the current draft/lifecycle immediately before considering sound.
        _refreshState?.Invoke();
        if (_disposed || !IsArmed) return;
        var now = _timeProvider.GetTimestamp();
        // Reject queued ticks from a prior scope/countdown, and never catch up in bursts.
        if (_timeProvider.GetElapsedTime(_lastReminderTimestamp, now) < ReminderInterval) return;
        _lastReminderTimestamp = now;
        _notify();
    }

    private void Disarm()
    {
        if (IsArmed) _timer.Stop();
        IsArmed = false;
        _scope = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disarm();
        _timer.Tick -= OnTick;
        _timer.Dispose();
    }
}
