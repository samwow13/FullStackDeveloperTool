namespace FullStackLauncher.CodexMonitor;

public enum AgentRunState
{
    Running,
    Waiting,
    Completed,
    Failed,
    Unknown,
    Idle
}

public sealed record AgentSnapshot(
    string Id,
    string Title,
    string ProjectPath,
    AgentRunState State,
    string? ParentId = null,
    string? LatestTurnId = null,
    DateTimeOffset? CompletedAt = null);

public sealed record ReminderUpdate(
    string Status,
    bool ShouldPlaySound,
    bool IsReminding,
    int RunningCount,
    int WaitingCount,
    int TotalCount);

/// <summary>
/// Observes complete, reliable snapshots for one project. Call ConnectionLost when a
/// snapshot cannot be obtained, and Reset when changing the project being monitored.
/// All calls must come from the same thread.
/// </summary>
public sealed class ReminderState
{
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SoundInterval = TimeSpan.FromMinutes(1);
    private readonly HashSet<string> _batchIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lastActiveIds = new(StringComparer.Ordinal);
    private DateTimeOffset? _completionObservedAt;
    private DateTimeOffset? _lastSoundAt;
    private int _completionScans;
    private int _lastActiveCount;
    private bool _isReminding;
    private bool _paused;
    private bool _dismissed;
    private bool _dismissUntilIdle;
    private bool _batchSettled;

    public ReminderUpdate Observe(IReadOnlyList<AgentSnapshot> agents, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var running = agents.Count(agent => agent.State == AgentRunState.Running);
        var waiting = agents.Count(agent => agent.State == AgentRunState.Waiting);
        _lastActiveCount = running + waiting;
        _lastActiveIds.Clear();
        foreach (var agent in agents.Where(agent => agent.State is AgentRunState.Running or AgentRunState.Waiting))
            _lastActiveIds.Add(agent.Id);

        ReminderUpdate Result(string status, bool sound = false) =>
            new(status, sound, _isReminding, running, waiting, agents.Count);

        if (_paused)
            return Result("Codex reminders paused.");

        var current = new Dictionary<string, AgentSnapshot>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id) || !current.TryAdd(agent.Id, agent))
            {
                StopReminding();
                _batchSettled = false;
                return Result("Waiting for a consistent Codex status snapshot.");
            }
        }

        // Dismissing while work is active must not immediately rearm that same work.
        if (_dismissUntilIdle)
        {
            if (_lastActiveCount == 0)
                _dismissUntilIdle = false;
            return Result("Dismissed; waiting for new Codex work.");
        }

        if (_lastActiveCount > 0)
        {
            // A completed or failed batch must not block later work indefinitely.
            // Unknown or missing tasks do not count as settled.
            if (_batchSettled)
                _batchIds.Clear();

            StopReminding();
            _batchSettled = false;
            _dismissed = false;
            foreach (var agent in agents)
            {
                if (agent.State is AgentRunState.Running or AgentRunState.Waiting or AgentRunState.Unknown)
                    _batchIds.Add(agent.Id);
            }

            return Result(waiting > 0
                ? $"{running} running; {waiting} queued or waiting."
                : $"Watching {running} running Codex task(s).");
        }

        // An ownership transfer can retain outstanding IDs in a dismissed
        // scope. Retain their safeguards, but require new active work to rearm.
        if (_dismissed)
            return Result("Dismissed; waiting for new Codex work.");

        // Old completed history never starts an alert when the monitor first opens.
        if (_batchIds.Count == 0)
            return Result("Armed; waiting to observe Codex work.");

        // A newly created task can appear before its first turn does. Keep watching
        // it even if it disappears, until a later reliable snapshot confirms it.
        foreach (var agent in agents)
        {
            if (agent.State == AgentRunState.Unknown)
                _batchIds.Add(agent.Id);
        }

        _batchSettled = false;
        var hasFailure = false;
        foreach (var id in _batchIds)
        {
            if (!current.TryGetValue(id, out var agent))
            {
                StopReminding();
                return Result("A watched task is missing; completion is unconfirmed.");
            }

            if (agent.State == AgentRunState.Failed)
            {
                hasFailure = true;
            }
            else if (agent.State != AgentRunState.Completed)
            {
                StopReminding();
                return Result("A watched task has unknown status; completion is unconfirmed.");
            }
        }

        _batchSettled = true;
        if (hasFailure)
        {
            StopReminding();
            return Result("A watched task failed or was interrupted; completion is unconfirmed.");
        }

        if (_isReminding)
        {
            var shouldPlay = _lastSoundAt is null || now - _lastSoundAt.Value >= SoundInterval;
            if (shouldPlay)
                _lastSoundAt = now;
            return Result("All watched Codex tasks finished. Reminding every minute until dismissed.", shouldPlay);
        }

        _completionObservedAt ??= now;
        _completionScans = Math.Min(2, _completionScans + 1);
        if (_completionScans < 2 || now - _completionObservedAt.Value < CompletionGrace)
            return Result("All watched tasks appear finished; confirming completion.");

        _isReminding = true;
        _lastSoundAt = now;
        return Result("All watched Codex tasks finished. Reminding every minute until dismissed.", true);
    }

    public void Dismiss()
    {
        _dismissUntilIdle = _lastActiveCount > 0;
        _dismissed = true;
        _batchIds.Clear();
        _batchSettled = false;
        StopReminding();
    }

    // Move only previously observed outstanding work. Resetting an entire scope
    // here could discard unrelated missing/unknown IDs and falsely report success.
    public void TransferTo(ReminderState destination, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(ids);
        if (ReferenceEquals(this, destination) || ids.Count == 0)
            return;

        var movingIds = ids.ToHashSet(StringComparer.Ordinal);
        var moved = _batchIds.Where(movingIds.Contains).ToArray();
        var movedActive = _lastActiveIds.Where(movingIds.Contains).ToArray();
        var moveActiveDismissal = _dismissUntilIdle && movedActive.Length > 0;
        _lastActiveIds.ExceptWith(movingIds);
        _lastActiveCount = _lastActiveIds.Count;
        destination._lastActiveIds.UnionWith(movedActive);
        destination._lastActiveCount = destination._lastActiveIds.Count;
        if (moved.Length > 0)
        {
            _batchIds.ExceptWith(moved);
            // Subtracting members from a settled batch leaves its remaining
            // members settled; a subsequent active run can still replace it.
            _batchSettled = _batchSettled && _batchIds.Count > 0;
            StopReminding();
            // Replace confirmed old work before introducing outstanding IDs.
            // Unsettled missing/unknown members are always retained.
            if (destination._batchSettled)
                destination._batchIds.Clear();
            destination._batchIds.UnionWith(moved);
            destination._dismissed |= _dismissed;
        }

        if (moveActiveDismissal)
        {
            destination._batchIds.ExceptWith(movingIds);
            destination._dismissed = true;
            destination._dismissUntilIdle = true;
        }
        if (moved.Length > 0 || moveActiveDismissal)
        {
            destination._batchSettled = false;
            destination.StopReminding();
        }
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        Reset();
    }

    public void Reset()
    {
        _batchIds.Clear();
        _lastActiveIds.Clear();
        _lastActiveCount = 0;
        _dismissed = false;
        _dismissUntilIdle = false;
        _batchSettled = false;
        StopReminding();
    }

    // A reconnection must observe active work again before it can report completion.
    public void ConnectionLost() => Reset();

    private void StopReminding()
    {
        _isReminding = false;
        _completionObservedAt = null;
        _completionScans = 0;
        _lastSoundAt = null;
    }
}
