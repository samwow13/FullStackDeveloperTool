using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Models;

public enum CodexDesktopPresence
{
    Unknown,
    Running,
    NotRunning
}

/// <summary>A bounded, redacted public chat excerpt retained only in dashboard memory.</summary>
public sealed record CodexActivityMessage(string Identity, string Text);

/// <summary>Transient dashboard content. Never serialize this into launcher or monitor settings.</summary>
public sealed record CodexActivityAgent(
    string Id,
    string Title,
    string TaskSummary,
    string ProjectLabel,
    AgentRunState State,
    string? TurnId,
    string? ParentId,
    DateTimeOffset ObservedAt,
    DateTimeOffset? CompletedAt,
    string Detail,
    bool WaitingForChildren = false)
{
    public bool IsRunning => State == AgentRunState.Running;
    public bool IsWaiting => State == AgentRunState.Waiting;
    public bool NeedsInput => State == AgentRunState.NeedsInput;
    public bool IsSleeping => State == AgentRunState.Completed;
    public bool IsUnknown => State is AgentRunState.Unknown or AgentRunState.Idle;
    public bool IsFailed => State == AgentRunState.Failed;
    public bool IsRecent => IsSleeping || IsFailed;
    public bool IsSubagent => !string.IsNullOrWhiteSpace(ParentId);
    public bool ConfirmingCompletion { get; init; }
    // Family state can be Running solely because a child is working. Retain the
    // readable member state for counts without changing the chat-card projection.
    public AgentRunState? OwnState { get; init; }
    public bool? OwnCompletionConfirmed { get; init; }
    public bool HasFailedChildren { get; init; }
    public bool HasInputBlockedChildren { get; init; }
    public bool CanDismiss { get; init; }
    public string? ActivityIdentity { get; init; }
    public DateTimeOffset? ActivityStartedAt { get; init; }
    public string? FeedbackIdentity { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ProjectId { get; init; }
    public IReadOnlyList<CodexActivityMessage> MessageHistory { get; init; } = [];
    public IReadOnlyList<CodexActivityAgent> Subagents { get; init; } = [];
    public bool HasSubagents => Subagents.Count > 0;
    public string StateText => HasFailedChildren ? "Team needs attention" : HasInputBlockedChildren ? "Team needs answer" : WaitingForChildren ? "Team working" :
        ConfirmingCompletion ? "Finishing…" : State switch
    {
        AgentRunState.Running => "Working",
        AgentRunState.Waiting => "Queued / waiting",
        AgentRunState.NeedsInput => "Needs answer",
        AgentRunState.Completed => "Complete · zzz",
        AgentRunState.Failed => "Stopped / failed",
        _ => "Status unknown"
    };
    public string SummarySource { get; init; } = "Chat title";
}

public sealed record CodexActivityFeedSnapshot(
    IReadOnlyList<CodexActivityAgent> Agents,
    bool IsAvailable,
    string StatusText,
    DateTimeOffset? CheckedAt)
{
    public CodexDesktopPresence DesktopPresence { get; init; }
    public bool IsCodexNotRunning => DesktopPresence == CodexDesktopPresence.NotRunning;
    public int RunningCount => Agents.Count(agent => agent.IsRunning);
    public int WaitingCount => Agents.Count(agent => agent.IsWaiting);
    public int NeedsInputCount => Agents.Count(agent => agent.NeedsInput);
    public int RecentCount => Agents.Count(agent => agent.IsRecent);
    public int UnlistedUnknownCount { get; init; }
    public IReadOnlyDictionary<string, int> ProjectUnknownCounts { get; init; } = new Dictionary<string, int>();
    public int UnknownCount => Agents.Count(agent => agent.IsUnknown) + UnlistedUnknownCount;
    public bool CanClear => Agents.Any(agent => agent.CanDismiss);
    public bool HasAgents => Agents.Count > 0;
    public DateTimeOffset? ResetAt { get; init; }
    public const string ScopeDescription = "One card per local Codex chat belonging to the selected project, with its observed subagents retained after completion. All projects remain tracked. Codex must remain open; remote and cloud work is not covered. Summaries are bounded excerpts from saved chat, kept only in memory.";
    public static CodexActivityFeedSnapshot Initial { get; } =
        new([], false, "Checking local Codex activity…", null);

    public CodexActivityFeedSnapshot ForProject(string? projectId, string? projectName) => this with
    {
        Agents = string.IsNullOrWhiteSpace(projectId) ? [] : Agents.Where(agent => agent.ProjectId == projectId).ToArray(),
        UnlistedUnknownCount = projectId is null ? 0 : ProjectUnknownCounts.GetValueOrDefault(projectId),
        StatusText = !IsAvailable ? StatusText :
            string.IsNullOrWhiteSpace(projectId) ? "Choose a project" : projectName ?? "Selected project"
    };
}
