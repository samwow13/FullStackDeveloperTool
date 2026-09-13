namespace FullStackLauncher.CodexMonitor;

public sealed record ProgressEstimate(string Text, string Detail)
{
    internal int? Percent { get; init; }
}

/// <summary>
/// A deliberately rough elapsed-time heuristic, independent of completion detection.
/// Only IDs, turn markers, states and observed durations are retained, in memory.
/// Call on the UI thread with reliable snapshots for a single project.
/// </summary>
public sealed class AgentProgressEstimator
{
    private const int SampleLimit = 20;
    private const int MinimumSamples = 3;
    private static readonly TimeSpan InitialDuration = TimeSpan.FromMinutes(10);
    private readonly Queue<TimeSpan> _completedDurations = new();
    private Dictionary<string, ObservedAgent> _agents = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastObservation;

    public void Observe(IReadOnlyList<AgentSnapshot> agents, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agents);
        // Machine sleep or a long blocked poll is unobserved time as well.
        if (_lastObservation.HasValue && (now < _lastObservation.Value || now - _lastObservation.Value > TimeSpan.FromSeconds(30)))
            ConnectionLost();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id) || !ids.Add(agent.Id))
            {
                ConnectionLost();
                return;
            }
        }

        var current = new Dictionary<string, ObservedAgent>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            _agents.TryGetValue(agent.Id, out var previous);
            var changedTurn = previous is not null &&
                !string.Equals(previous.TurnId, agent.LatestTurnId, StringComparison.Ordinal);
            var starting = previous is null || changedTurn ||
                (agent.State is AgentRunState.Running or AgentRunState.Waiting &&
                 previous.State is not (AgentRunState.Running or AgentRunState.Waiting));
            var observed = starting
                ? new ObservedAgent(agent.LatestTurnId)
                : previous!;

            // Waiting intervals are excluded. After a gap we start a fresh
            // observation, so disconnected time cannot advance an estimate.
            if (!starting && previous!.State == AgentRunState.Running && _lastObservation.HasValue)
                observed.RunningDuration += now - _lastObservation.Value;
            if (agent.State == AgentRunState.Running)
            {
                if (!observed.SawRunning)
                {
                    // Freeze each run's baseline so another agent completing
                    // cannot move an already-running estimate backwards.
                    observed.BaselineSamples = _completedDurations.Count >= MinimumSamples ? _completedDurations.Count : 0;
                    observed.ExpectedDuration = observed.BaselineSamples > 0 ? MedianDuration() : InitialDuration;
                }
                observed.SawRunning = true;
            }

            if (agent.State == AgentRunState.Completed && !observed.SampleRecorded && observed.SawRunning &&
                observed.RunningDuration > TimeSpan.Zero && observed.RunningDuration <= TimeSpan.FromDays(1))
            {
                _completedDurations.Enqueue(observed.RunningDuration);
                while (_completedDurations.Count > SampleLimit)
                    _completedDurations.Dequeue();
                observed.SampleRecorded = true;
            }

            observed.State = agent.State;
            // Uncertain or unsuccessful work cannot later teach a completion
            // duration unless a fresh running observation starts another run.
            if (agent.State is AgentRunState.Unknown or AgentRunState.Failed or AgentRunState.Idle)
                observed.SawRunning = false;
            current.Add(agent.Id, observed);
        }

        _agents = current;
        _lastObservation = now;
    }

    public void ConnectionLost()
    {
        _agents.Clear();
        _lastObservation = null;
    }

    public ProgressEstimate GetEstimate(string id)
    {
        if (!_agents.TryGetValue(id, out var agent))
            return Unavailable("A reliable current status is needed before estimating progress.");

        if (agent.State == AgentRunState.Completed)
            return Completed();
        if (agent.State == AgentRunState.Waiting)
            return Unavailable("Queued or waiting; remaining time cannot be estimated yet.");
        if (agent.State == AgentRunState.Failed)
            return Unavailable("Failed or interrupted; completion is unconfirmed.");
        if (agent.State != AgentRunState.Running)
            return Unavailable("Progress cannot be estimated while status is unavailable or inactive.");

        var learned = agent.BaselineSamples > 0;
        var expected = agent.ExpectedDuration;
        var percent = (int)Math.Clamp(Math.Floor(agent.RunningDuration.TotalSeconds / expected.TotalSeconds * 100), 0, 95);
        var basis = learned
            ? $"the median {FormatDuration(expected)} observed running duration of {agent.BaselineSamples} completed runs in this project during this monitor session"
            : "an uncalibrated 10-minute baseline (fewer than 3 completed runs observed when this run was first seen)";
        var limit = percent == 95
            ? " The 95% ceiling has been reached; remaining time is unknown."
            : " Running estimates are capped at 95%.";
        return new ProgressEstimate($"~{percent}% est.",
            $"Low-confidence elapsed-time estimate, not measured work completed. {FormatDuration(agent.RunningDuration)} of running time since first observed; waiting time excluded. Based on {basis}. Actual completion may be much earlier or later.{limit}")
        {
            Percent = percent
        };
    }

    internal ProgressEstimate GetChatEstimate(IEnumerable<string> memberIds, AgentRunState state)
    {
        if (state == AgentRunState.Completed)
            return Completed();
        if (state == AgentRunState.Waiting)
            return Unavailable("Waiting or confirming completion; no remaining-time estimate is available.");
        if (state != AgentRunState.Running)
            return Unavailable("A reliable running or completed status is needed for every watched agent.");

        var estimates = memberIds.Select(GetEstimate).ToArray();
        if (estimates.Length == 0 || estimates.Any(estimate => !estimate.Percent.HasValue))
            return Unavailable("At least one watched agent is waiting, unavailable or failed; a chat estimate is not available.");
        var slowest = estimates.MinBy(estimate => estimate.Percent!.Value)!;
        if (slowest.Percent == 100)
            return Unavailable("Waiting for the chat's completion to be confirmed.");
        return slowest with
        {
            Detail = "Uses the lowest estimate among this chat's observed agents; new work can lower it. " + slowest.Detail
        };
    }

    internal static ProgressEstimate Completed() => new("100%", "Observed completion; this is a status, not a time estimate.")
    {
        Percent = 100
    };

    private static ProgressEstimate Unavailable(string detail) => new("—", detail);

    private TimeSpan MedianDuration()
    {
        var seconds = _completedDurations.Select(duration => duration.TotalSeconds).Order().ToArray();
        var middle = seconds.Length / 2;
        var median = seconds.Length % 2 == 0 ? (seconds[middle - 1] + seconds[middle]) / 2 : seconds[middle];
        return TimeSpan.FromSeconds(Math.Max(30, median));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return "less than 1 minute";
        if (duration.TotalHours < 1)
            return $"about {Math.Round(duration.TotalMinutes)} minute(s)";
        return $"about {Math.Round(duration.TotalHours, 1):0.#} hour(s)";
    }

    private sealed class ObservedAgent(string? turnId)
    {
        public string? TurnId { get; } = turnId;
        public AgentRunState State { get; set; } = AgentRunState.Unknown;
        public TimeSpan RunningDuration { get; set; }
        public TimeSpan ExpectedDuration { get; set; } = InitialDuration;
        public int BaselineSamples { get; set; }
        public bool SawRunning { get; set; }
        public bool SampleRecorded { get; set; }
    }
}
