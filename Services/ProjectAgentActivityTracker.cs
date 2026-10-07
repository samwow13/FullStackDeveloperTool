using FullStackLauncher.CodexMonitor;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Transient project activity shared by both sidebar presentations.</summary>
public readonly record struct ProjectAgentActivity(int RunningCount, bool HasUnreadCompletion)
{
    public bool HasSnapshot { get; init; }
    public bool IsAvailable { get; init; }
}

/// <summary>
/// Projects the global feed without changing its tracking, agents, or saved settings.
/// Completion remains unread until an explicit project acknowledgment.
/// </summary>
public sealed class ProjectAgentActivityTracker
{
    private readonly Dictionary<string, ObservedAgent> _observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _runningCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadProjects = new(StringComparer.Ordinal);
    private bool _hasSnapshot;
    private bool _isAvailable;
    private DateTimeOffset? _lastCheckedAt;
    private DateTimeOffset? _resetAt;

    public ProjectAgentActivity GetActivity(string projectId) =>
        new(_runningCounts.GetValueOrDefault(projectId), _unreadProjects.Contains(projectId))
        {
            HasSnapshot = _hasSnapshot,
            IsAvailable = _isAvailable
        };

    public void Acknowledge(string projectId) => _unreadProjects.Remove(projectId);

    public void Update(CodexActivityFeedSnapshot snapshot, string? selectedProjectId)
    {
        if (!snapshot.IsAvailable)
        {
            // Losing Codex or its readable status does not establish zero work or completion.
            _isAvailable = false;
            return;
        }
        if (_lastCheckedAt is { } lastChecked && snapshot.CheckedAt is { } checkedAt && checkedAt < lastChecked ||
            _resetAt is { } resetAt && (snapshot.ResetAt is null || snapshot.ResetAt < resetAt))
        {
            _isAvailable = false;
            return;
        }

        var baseline = !_hasSnapshot || snapshot.ResetAt != _resetAt;
        var members = ReadMembers(snapshot.Agents);
        _runningCounts.Clear();
        foreach (var member in members.Values)
        {
            if (member.ProjectId is { } projectId && member.OwnState == AgentRunState.Running)
                _runningCounts[projectId] = _runningCounts.GetValueOrDefault(projectId) + 1;

            if (!_observed.TryGetValue(member.Id, out var observed))
                _observed[member.Id] = observed = new();

            var newActivity = IsNewActivity(observed, member);
            if (baseline || newActivity)
            {
                observed.Armed = false;
                observed.CompletionHandled = false;
            }
            if (newActivity) observed.ActivityIdentity = member.ActivityIdentity;
            if (member.ActivityIdentity is { } identity)
                observed.ActivityIdentity = identity;
            if (member.ActivityStartedAt is { } startedAt)
                observed.ActivityStartedAt = startedAt;

            if (IsActive(member.OwnState))
            {
                // A changed representation (start -> turn) can describe the same activity.
                // Regressing to running must not rearm an already completed known turn.
                if (!observed.CompletionHandled || observed.ActivityIdentity is null)
                    observed.Armed = !observed.ConsumedCompletions.Contains(ActivityKey(observed.ActivityIdentity));
                if (observed.ActivityIdentity is null) observed.CompletionHandled = false;
                continue;
            }
            if (!member.IsConfirmedCompleted) continue;

            var completionKey = ActivityKey(observed.ActivityIdentity) ??
                (member.CompletedAt is { } completedAt ? "completed:" + completedAt.UtcTicks : null);
            var alreadyConsumed = completionKey is not null && observed.ConsumedCompletions.Contains(completionKey);
            if (!baseline && observed.Armed && !observed.CompletionHandled && !alreadyConsumed &&
                completionKey is not null && member.ProjectId is { } completedProjectId &&
                completedProjectId != selectedProjectId)
                _unreadProjects.Add(completedProjectId);

            // Consume selected-project completion too, so leaving the project cannot replay it.
            if (completionKey is not null) observed.ConsumedCompletions.Add(completionKey);
            observed.Armed = false;
            observed.CompletionHandled = true;
        }
        if (baseline)
        {
            // A reset changes the reader's baseline. Absent observations must not remain armed.
            foreach (var pair in _observed)
                if (!members.ContainsKey(pair.Key)) pair.Value.Armed = false;
        }
        _hasSnapshot = true;
        _isAvailable = true;
        _lastCheckedAt = snapshot.CheckedAt ?? _lastCheckedAt;
        _resetAt = snapshot.ResetAt;
    }

    private static bool IsActive(AgentRunState state) =>
        state is AgentRunState.Running or AgentRunState.Waiting or AgentRunState.NeedsInput;

    private static string? ActivityKey(string? identity) => identity is null ? null : "activity:" + identity;

    private static bool IsNewActivity(ObservedAgent previous, MemberObservation current)
    {
        if (previous.ActivityIdentity == current.ActivityIdentity) return false;
        return previous.ActivityIdentity is { } priorIdentity && current.ActivityIdentity is { } identity &&
            IdentityKind(priorIdentity) == IdentityKind(identity) ||
            previous.ActivityStartedAt is { } priorStart && current.ActivityStartedAt is { } currentStart &&
            currentStart > priorStart;
    }

    private static string IdentityKind(string identity) => identity.StartsWith("turn:", StringComparison.Ordinal)
        ? "turn" : identity.StartsWith("start:", StringComparison.Ordinal) ? "start" : "activity";

    private static Dictionary<string, MemberObservation> ReadMembers(IReadOnlyList<CodexActivityAgent> roots)
    {
        var result = new Dictionary<string, MemberObservation>(StringComparer.Ordinal);
        var pending = new Stack<(CodexActivityAgent Agent, string? ProjectId)>();
        foreach (var root in roots) pending.Push((root, null));
        var visited = new Dictionary<CodexActivityAgent, HashSet<string?>>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var entry))
        {
            var agent = entry.Agent;
            // The feed assigns complete families to one authoritative configured project ID.
            var projectId = entry.ProjectId ?? (string.IsNullOrWhiteSpace(agent.ProjectId) ? null : agent.ProjectId);
            if (!visited.TryGetValue(agent, out var projectContexts))
                visited.Add(agent, projectContexts = new(StringComparer.Ordinal));
            if (!projectContexts.Add(projectId)) continue;
            foreach (var child in agent.Subagents) pending.Push((child, projectId));
            if (string.IsNullOrWhiteSpace(agent.Id)) continue;
            var identity = !string.IsNullOrWhiteSpace(agent.ActivityIdentity) ? agent.ActivityIdentity :
                string.IsNullOrWhiteSpace(agent.TurnId) ? null : "turn:" + agent.TurnId;
            var ownState = agent.OwnState ?? agent.State;
            var member = new MemberObservation(agent.Id, projectId, ownState, identity, agent.ActivityStartedAt,
                agent.CompletedAt, ownState == AgentRunState.Completed && (agent.OwnCompletionConfirmed ??
                (agent.State == AgentRunState.Completed && !agent.ConfirmingCompletion &&
                !agent.WaitingForChildren && !agent.HasFailedChildren && !agent.HasInputBlockedChildren)));
            if (!result.TryGetValue(agent.Id, out var previous)) result.Add(agent.Id, member);
            else
            {
                // Conflicting duplicate observations cannot establish successful completion.
                var identityConflict = previous.ActivityIdentity is { } prior && identity is not null && prior != identity;
                result[agent.Id] = previous with
                {
                    ProjectId = previous.ProjectId == projectId ? projectId : null,
                    OwnState = previous.OwnState == member.OwnState ? member.OwnState : AgentRunState.Unknown,
                    ActivityIdentity = identityConflict ? null : previous.ActivityIdentity ?? identity,
                    ActivityStartedAt = previous.ActivityStartedAt ?? member.ActivityStartedAt,
                    CompletedAt = previous.CompletedAt ?? member.CompletedAt,
                    IsConfirmedCompleted = previous.IsConfirmedCompleted && member.IsConfirmedCompleted && !identityConflict
                };
            }
        }
        return result;
    }

    private sealed class ObservedAgent
    {
        public string? ActivityIdentity { get; set; }
        public DateTimeOffset? ActivityStartedAt { get; set; }
        public bool Armed { get; set; }
        public bool CompletionHandled { get; set; }
        public HashSet<string?> ConsumedCompletions { get; } = new(StringComparer.Ordinal);
    }

    private sealed record MemberObservation(string Id, string? ProjectId, AgentRunState OwnState,
        string? ActivityIdentity, DateTimeOffset? ActivityStartedAt, DateTimeOffset? CompletedAt,
        bool IsConfirmedCompleted);
}
