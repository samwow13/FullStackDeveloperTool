using System.IO;
using FullStackLauncher.CodexMonitor;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Read-only local activity for the dashboard, independent of saved alert watches.
/// Call ReadSnapshot on a background worker. ClearRecent only changes this feed.
/// </summary>
public sealed partial class CodexActivityFeedService
{
    private const int MaximumRecentChats = 12;
    private static readonly TimeSpan CompletionConfirmation = TimeSpan.FromSeconds(10);
    private readonly LocalCodexReader _reader;
    private readonly CodexActivityFeedSummaryReader _summaries;
    private readonly CodexActivityResetStore _resetStore;
    private readonly CodexActivityProjectStore _projectStore;
    private readonly CodexActivityProjectReceiptReader _projectReceiptReader;
    private readonly CodexActivityWorkingFolderReader _workingFolderReader;
    private readonly string _settingsPath;
    private CodexActivityProjectResolver _projectResolver = new([]);
    private IReadOnlyDictionary<string, string> _explicitProjectIds = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _workingFolders = new Dictionary<string, IReadOnlyList<string>>();
    private Dictionary<string, CodexActivityProjectScope> _projectOwners = new(StringComparer.Ordinal);
    private string[] _unlistedUnknownFamilies = [];
    private readonly object _readLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, TrackedAgent> _tracked = new(StringComparer.Ordinal);
    private Dictionary<string, AgentSnapshot> _familyAnchors = new(StringComparer.Ordinal);
    private CodexActivityFeedSnapshot _current = CodexActivityFeedSnapshot.Initial;
    private CodexDesktopPresence _desktopPresence;
    private int _unlistedUnknownCount;
    private HashSet<string>? _previousAgentIds;
    private CodexActivityResetBaseline _resetBaseline;
    private Dictionary<string, HashSet<string?>> _suppressed;

    public CodexActivityFeedService(string codexHome, string? resetStorePath = null, string? settingsPath = null)
    {
        _reader = new LocalCodexReader(codexHome);
        _summaries = new CodexActivityFeedSummaryReader(codexHome);
        _resetStore = new CodexActivityResetStore(codexHome, resetStorePath);
        _settingsPath = settingsPath ?? new SettingsStore().SettingsPath;
        _projectStore = new CodexActivityProjectStore(_settingsPath);
        _projectReceiptReader = new CodexActivityProjectReceiptReader(_settingsPath);
        _workingFolderReader = new CodexActivityWorkingFolderReader(codexHome);
        _resetBaseline = _resetStore.Load();
        _suppressed = IndexReset(_resetBaseline);
        _current = _current with { ResetAt = _resetBaseline.ResetAt };
    }

    public CodexActivityFeedSnapshot CurrentSnapshot
    {
        get { lock (_stateLock) return _current; }
    }

    internal CodexChatHistoryExport ReadChatHistory(string agentId, CancellationToken token) =>
        _summaries.ReadChatHistory(agentId, token);

    public CodexActivityFeedSnapshot ReadSnapshot(CancellationToken token = default) => ReadSnapshot([], token);

    public CodexActivityFeedSnapshot ReadSnapshot(IReadOnlyList<CodexActivityProjectScope> projects, CancellationToken token = default)
    {
        lock (_readLock)
        {
            token.ThrowIfCancellationRequested();
            var desktopPresence = CodexDesktopPresenceReader.Read();
            if (desktopPresence != CodexDesktopPresence.Running)
                return SuspendRead(desktopPresence);
            string[] retained;
            lock (_stateLock) retained = _tracked.Keys.ToArray();
            IReadOnlyList<AgentSnapshot> agents;
            CodexActivityResetBaseline resetBaseline;
            try
            {
                resetBaseline = _resetStore.Load(token);
                agents = _reader.ReadAllSnapshot(retained, includeGuardians: false);
                ReadProjectEvidence(projects, agents, token);
                token.ThrowIfCancellationRequested();
                desktopPresence = CodexDesktopPresenceReader.Read();
                if (desktopPresence != CodexDesktopPresence.Running)
                    return SuspendRead(desktopPresence);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Database exceptions and source payloads never become dashboard diagnostics.
                return SuspendRead(CodexDesktopPresenceReader.Read());
            }

            var now = DateTimeOffset.UtcNow;
            string[] summaryIds;
            lock (_stateLock)
            {
                _desktopPresence = desktopPresence;
                // A removed/replaced receipt must not resurrect an already suppressed turn in this process.
                resetBaseline = MergeReset(_resetBaseline, resetBaseline);
                _resetBaseline = resetBaseline;
                _suppressed = IndexReset(resetBaseline);
                // An ancestor hidden by a tracking reset can still provide the
                // real chat title for newly observed child work. It contributes
                // metadata only; suppressed siblings never re-enter tracking.
                _familyAnchors = agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
                foreach (var removed in _tracked.Values.Where(item => IsSuppressed(item.Agent)).Select(item => item.Agent.Id).ToArray())
                    _tracked.Remove(removed);
                agents = agents.Where(agent => !IsSuppressed(agent)).ToArray();
                var currentIds = agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var tracked in _tracked.Values)
                    if (!currentIds.Contains(tracked.Agent.Id) && !IsSettled(tracked))
                    {
                        tracked.Missing = true;
                        tracked.CompletionSince = null;
                        tracked.CompletionScans = 0;
                    }

                foreach (var agent in agents)
                {
                    if (!_tracked.TryGetValue(agent.Id, out var tracked))
                    {
                        // Start with current work, never a wall of old completed conversations.
                        var newlyUnknown = agent.State == AgentRunState.Unknown &&
                            _previousAgentIds is not null && !_previousAgentIds.Contains(agent.Id);
                        if (agent.State is not (AgentRunState.Running or AgentRunState.Waiting or AgentRunState.NeedsInput) && !newlyUnknown)
                            continue;
                        _tracked[agent.Id] = tracked = new TrackedAgent(agent, now);
                        CaptureParentActivity(tracked, agents);
                    }
                    var newActivity = IsNewActivity(tracked.Agent, agent);
                    if (newActivity)
                    {
                        tracked.Summary = null;
                        tracked.ObservedAt = now;
                        tracked.TerminalObservedAt = null;
                        tracked.CompletionSince = null;
                        tracked.CompletionScans = 0;
                        CaptureParentActivity(tracked, agents);
                        tracked.ObservedChildFailure = false;
                    }
                    if (!IsTerminal(tracked.Agent.State) && IsTerminal(agent.State))
                        tracked.TerminalObservedAt = now;
                    // A projection changing back to idle is not proof that observed work completed.
                    var observed = agent.State == AgentRunState.Idle
                        ? agent with { State = AgentRunState.Unknown }
                        : agent;
                    // Keep the last known selection identity across an incomplete
                    // status read, so a later new turn cannot inherit old badges.
                    var selectionIdentity = ActivityIdentity(observed);
                    var preserveSelection = !newActivity && (selectionIdentity is null ||
                        selectionIdentity == tracked.SelectionActivityIdentity);
                    tracked.Agent = !preserveSelection ? observed : observed with
                    {
                        Model = observed.Model ?? tracked.Agent.Model,
                        ReasoningEffort = observed.ReasoningEffort ?? tracked.Agent.ReasoningEffort
                    };
                    if (selectionIdentity is not null) tracked.SelectionActivityIdentity = selectionIdentity;
                    tracked.Missing = false;
                    if (agent.State == AgentRunState.Completed)
                    {
                        tracked.CompletionSince ??= now;
                        tracked.CompletionLastSeen = now;
                        tracked.CompletionScans = Math.Min(2, tracked.CompletionScans + 1);
                    }
                    else
                    {
                        tracked.CompletionSince = null;
                        tracked.CompletionScans = 0;
                    }
                }

                // Retain the root of an active child even when the root itself has finished.
                foreach (var child in _tracked.Values.ToArray())
                {
                    if (IsSettled(child)) continue;
                    var parentId = child.Agent.ParentId;
                    var visited = new HashSet<string>(StringComparer.Ordinal) { child.Agent.Id };
                    while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
                    {
                        var parent = agents.FirstOrDefault(agent => agent.Id == parentId);
                        if (parent is null) break;
                        if (!_tracked.ContainsKey(parent.Id))
                            _tracked[parent.Id] = new TrackedAgent(parent, now)
                            {
                                CompletionSince = parent.State == AgentRunState.Completed ? now : null,
                                CompletionLastSeen = now,
                                CompletionScans = parent.State == AgentRunState.Completed ? 1 : 0,
                                ParentActivityIdentity = agents.FirstOrDefault(ancestor => ancestor.Id == parent.ParentId) is { } ancestor
                                    ? ActivityIdentity(ancestor) : null,
                                ParentActivityStartedAt = agents.FirstOrDefault(ancestor => ancestor.Id == parent.ParentId)?.ActivityStartedAt
                            };
                        parentId = parent.ParentId;
                    }
                }
                RememberChildFailures();
                UpdateProjectOwnership();
                PruneRecent();
                _previousAgentIds = currentIds;
                var listedFamilies = _tracked.Values.Select(item => ResolveFamilyRoot(item.Agent).Id).ToHashSet(StringComparer.Ordinal);
                _unlistedUnknownFamilies = agents.Where(agent => agent.State == AgentRunState.Unknown && !_tracked.ContainsKey(agent.Id))
                    .Select(agent => ResolveFamilyRoot(agent).Id).Where(id => !listedFamilies.Contains(id))
                    .Distinct(StringComparer.Ordinal).ToArray();
                _unlistedUnknownCount = _unlistedUnknownFamilies.Length;
                foreach (var tracked in _tracked.Values.Where(item => string.IsNullOrWhiteSpace(item.Agent.LatestTurnId)))
                    tracked.Summary = new(null, "", "Chat title · current turn's saved chat unavailable");
                summaryIds = _tracked.Values.Where(item => !string.IsNullOrWhiteSpace(item.Agent.LatestTurnId))
                    .OrderBy(item => IsTerminal(item.Agent.State))
                    .ThenByDescending(item => item.ObservedAt).Select(item => item.Agent.Id).ToArray();
            }

            var summaries = _summaries.Read(agents, summaryIds, token);
            token.ThrowIfCancellationRequested();
            desktopPresence = CodexDesktopPresenceReader.Read();
            if (desktopPresence != CodexDesktopPresence.Running)
                return SuspendRead(desktopPresence);
            lock (_stateLock)
            {
                _desktopPresence = desktopPresence;
                foreach (var (id, summary) in summaries)
                    if (_tracked.TryGetValue(id, out var tracked) && tracked.Agent.LatestTurnId == summary.TurnId)
                    {
                        tracked.Summary = summary;
                        tracked.MessageHistory = summary.MessageHistory;
                    }
                return _current = CreateSnapshot(true, _unlistedUnknownCount > 0
                    ? $"All local projects · {_unlistedUnknownCount} other chats have unknown status"
                    : "All local projects", now);
            }
        }
    }

    private CodexActivityFeedSnapshot SuspendRead(CodexDesktopPresence desktopPresence)
    {
        lock (_stateLock)
        {
            _desktopPresence = desktopPresence;
            // Keep observed history and reset markers. Loss of the desktop never
            // establishes completion, and a reopened desktop resumes normal reads.
            foreach (var tracked in _tracked.Values.Where(item => !IsSettled(item)))
            {
                tracked.Missing = true;
                tracked.CompletionSince = null;
                tracked.CompletionScans = 0;
            }
            var status = desktopPresence switch
            {
                CodexDesktopPresence.NotRunning => "Codex isn't Running",
                CodexDesktopPresence.Unknown => "Codex desktop status unavailable",
                _ => "Local Codex status unavailable"
            };
            return _current = CreateSnapshot(false, status, DateTimeOffset.UtcNow);
        }
    }

    public CodexActivityFeedSnapshot ClearRecent(string? projectId = null)
    {
        lock (_stateLock)
        {
            // Clear the complete settled family. Hidden children must not
            // recreate an orphan card after their visible chat is dismissed.
            var removable = _current.Agents.Where(agent => (projectId is null || agent.ProjectId == projectId) && agent.CanDismiss &&
                TryGetFamilyRoot(agent.Id, out var root) && ActivityIdentity(root) == agent.ActivityIdentity &&
                FamilyMembers(agent.Id).All(item => !item.Missing && IsSettled(item)))
                .Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _tracked.Values.Where(item => removable.Contains(ResolveFamilyRoot(item.Agent).Id))
                         .Select(item => item.Agent.Id).ToArray())
                _tracked.Remove(id);
            return _current = CreateSnapshot(_current.IsAvailable, _current.StatusText, _current.CheckedAt);
        }
    }

    /// <summary>Removes one settled chat family from this dashboard's transient feed.</summary>
    public CodexActivityFeedSnapshot DismissRecent(string agentId, string? activityIdentity)
    {
        lock (_stateLock)
        {
            var row = _current.Agents.FirstOrDefault(agent => agent.Id == agentId);
            if (!_current.IsAvailable || row is not { CanDismiss: true } ||
                row.ActivityIdentity != activityIdentity ||
                !TryGetFamilyRoot(agentId, out var root) || ActivityIdentity(root) != activityIdentity ||
                !FamilyMembers(agentId).All(item => !item.Missing && IsSettled(item)))
                return _current;

            // Remove hidden children too, so they cannot recreate the dismissed chat.
            // A later observed active turn is tracked normally.
            foreach (var id in FamilyMembers(agentId).Select(item => item.Agent.Id).ToArray())
                _tracked.Remove(id);
            return _current = CreateSnapshot(_current.IsAvailable, _current.StatusText, _current.CheckedAt);
        }
    }

    /// <summary>
    /// Starts a new dashboard tracking baseline. This never interrupts Codex work,
    /// changes Codex history, or clears the launcher task queue or alert preferences.
    /// </summary>
    public CodexActivityFeedSnapshot ForceReset(CancellationToken token = default)
    {
        try { return ForceResetCore(token); }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Crew reset was canceled. Tracking was not reset.");
        }
    }

    private CodexActivityFeedSnapshot ForceResetCore(CancellationToken token)
    {
        lock (_readLock)
        {
            token.ThrowIfCancellationRequested();
            var resetAt = DateTimeOffset.UtcNow;
            string[] retained;
            CodexActivityResetMarker[] trackedMarkers;
            lock (_stateLock)
            {
                retained = _tracked.Keys.Concat(_suppressed.Keys).Distinct(StringComparer.Ordinal).ToArray();
                trackedMarkers = _tracked.Values.Select(item => new CodexActivityResetMarker(item.Agent.Id,
                    ActivityIdentity(item.Agent))).Concat(_resetBaseline.Markers).ToArray();
            }

            IReadOnlyList<AgentSnapshot> agents;
            try
            {
                if (CodexDesktopPresenceReader.Read() != CodexDesktopPresence.Running)
                    throw new InvalidOperationException();
                agents = _reader.ReadAllSnapshot(retained, includeGuardians: false);
                token.ThrowIfCancellationRequested();
                if (CodexDesktopPresenceReader.Read() != CodexDesktopPresence.Running)
                    throw new InvalidOperationException();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                throw new InvalidOperationException("A fresh local Codex baseline could not be read. Tracking was not reset; keep Codex open and try again.");
            }

            var markers = agents.Select(agent => new CodexActivityResetMarker(agent.Id, ActivityIdentity(agent)))
                .Concat(trackedMarkers).ToArray();
            // The save is the only persistent side effect. Commit visible state only after it succeeds.
            var saved = _resetStore.SaveMerged(markers, resetAt, token);
            lock (_stateLock)
            {
                _resetBaseline = saved;
                _suppressed = IndexReset(saved);
                _tracked.Clear();
                _familyAnchors.Clear();
                _previousAgentIds = [];
                _unlistedUnknownCount = 0;
                _unlistedUnknownFamilies = [];
                var desktopPresence = CodexDesktopPresenceReader.Read();
                if (desktopPresence != CodexDesktopPresence.Running)
                    return SuspendRead(desktopPresence);
                _desktopPresence = desktopPresence;
                return _current = CreateSnapshot(true, "Tracking reset · existing work hidden", saved.ResetAt);
            }
        }
    }

    private bool IsSuppressed(AgentSnapshot agent)
    {
        if (!_suppressed.TryGetValue(agent.Id, out var identities)) return false;
        var identity = ActivityIdentity(agent);
        // Missing identity is never evidence that a baseline agent began new work.
        if (identity is null || identities.Contains(identity)) return true;
        // A projection acquiring a real turn ID can describe the same legacy lifecycle start.
        // Only comparable, known identities prove a new turn without a newer start timestamp.
        if (!identities.Contains(null) && identities.Any(prior => prior is not null &&
                IdentityKind(prior) == IdentityKind(identity))) return false;
        return !(agent.ActivityStartedAt is { } started && _resetBaseline.ResetAt is { } resetAt && started > resetAt);
    }

    private static string IdentityKind(string identity) => identity.StartsWith("turn:", StringComparison.Ordinal)
        ? "turn" : identity.StartsWith("start:", StringComparison.Ordinal) ? "start" : identity;

    private static string? ActivityIdentity(AgentSnapshot agent) => agent.ActivityIdentity ??
        (string.IsNullOrWhiteSpace(agent.LatestTurnId) ? null : "turn:" + agent.LatestTurnId);

    private static bool IsNewActivity(AgentSnapshot previous, AgentSnapshot current)
    {
        var before = ActivityIdentity(previous);
        var after = ActivityIdentity(current);
        if (before is null || after is null || before == after) return false;
        return IdentityKind(before) == IdentityKind(after) ||
            previous.ActivityStartedAt is { } priorStart && current.ActivityStartedAt is { } currentStart && currentStart > priorStart;
    }

    private static void CaptureParentActivity(TrackedAgent tracked, IReadOnlyList<AgentSnapshot> agents)
    {
        var parent = agents.FirstOrDefault(candidate => candidate.Id == tracked.Agent.ParentId);
        tracked.ParentActivityIdentity = parent is null ? null : ActivityIdentity(parent);
        tracked.ParentActivityStartedAt = parent?.ActivityStartedAt;
    }

    private static bool ParentHasNewActivity(TrackedAgent child, TrackedAgent parent) =>
        ParentHasNewActivity(child, parent.Agent);

    private static bool ParentHasNewActivity(TrackedAgent child, AgentSnapshot parent)
    {
        var before = child.ParentActivityIdentity;
        var after = ActivityIdentity(parent);
        if (before is null || after is null || before == after) return false;
        return IdentityKind(before) == IdentityKind(after) ||
            child.ParentActivityStartedAt is { } priorStart && parent.ActivityStartedAt is { } currentStart && currentStart > priorStart;
    }

    private static Dictionary<string, HashSet<string?>> IndexReset(CodexActivityResetBaseline baseline) =>
        baseline.Markers.GroupBy(marker => marker.AgentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(marker => marker.ActivityIdentity)
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    private static CodexActivityResetBaseline MergeReset(CodexActivityResetBaseline previous,
        CodexActivityResetBaseline current) => new(
        previous.ResetAt is { } prior && (current.ResetAt is null || prior > current.ResetAt) ? prior : current.ResetAt,
        previous.Markers.Concat(current.Markers).Distinct().ToArray());

    private void PruneRecent()
    {
        var removeFamilies = _tracked.Values.GroupBy(item => ResolveFamilyRoot(item.Agent).Id, StringComparer.Ordinal)
            .Where(family => TryGetFamilyRoot(family.Key, out var root) && IsTerminal(root.State) &&
                family.All(item => !item.Missing && IsSettled(item)))
            .GroupBy(family => _projectOwners.GetValueOrDefault(family.Key)?.Id ?? "", StringComparer.Ordinal)
            .SelectMany(project => project.OrderByDescending(family => family.Max(item => item.TerminalObservedAt ?? item.ObservedAt))
                .Skip(MaximumRecentChats)).Select(family => family.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _tracked.Values.Where(item => removeFamilies.Contains(ResolveFamilyRoot(item.Agent).Id))
                     .Select(item => item.Agent.Id).ToArray())
            _tracked.Remove(id);
    }

    private CodexActivityFeedSnapshot CreateSnapshot(bool available, string status, DateTimeOffset? checkedAt)
    {
        var rows = new List<CodexActivityAgent>(_tracked.Count);
        foreach (var tracked in _tracked.Values)
        {
            var agent = tracked.Agent;
            var waitingForChildren = IsTerminal(agent.State) && HasUnresolvedDescendant(agent.Id);
            var failedChildren = agent.State == AgentRunState.Completed && HasFailedDescendant(agent.Id);
            var confirming = agent.State == AgentRunState.Completed && !IsSettled(tracked);
            var unknownChild = waitingForChildren && HasUnknownDescendant(agent.Id);
            var inputBlockedChildren = waitingForChildren && HasInputBlockedDescendant(agent.Id);
            var state = tracked.Missing || unknownChild ? AgentRunState.Unknown : inputBlockedChildren ? AgentRunState.NeedsInput : waitingForChildren ? AgentRunState.Waiting :
                failedChildren ? AgentRunState.Failed : confirming ? AgentRunState.Waiting : agent.State;
            var title = CodexActivityFeedSummaryReader.Compact(agent.Title, 110);
            if (string.IsNullOrWhiteSpace(title)) title = "Codex agent";
            var summary = tracked.Summary;
            var text = summary?.Text ?? "Reading saved chat…";
            var source = summary?.Source ?? "Chat title";
            if (summary?.Text.Length == 0) text = title;
            var feedbackIdentity = summary?.FeedbackIdentity;
            var stateDetail = tracked.Missing
                ? "This agent is missing from readable local status. Completion is unconfirmed."
                : unknownChild
                    ? "This chat's turn finished, but an observed child agent has unknown or unavailable status. Completion is unconfirmed."
                : inputBlockedChildren
                    ? "This chat's turn finished; an observed child agent needs an answer before its work can finish."
                : waitingForChildren
                    ? "This chat's turn finished; an observed child agent is still working or has unconfirmed status."
                    : failedChildren
                        ? "This chat's turn finished, but an observed child agent failed or was interrupted."
                    : confirming
                        ? "The local lifecycle records completion; confirming it remains stable for 10 seconds."
                    : state switch
                    {
                        AgentRunState.Completed => "The local lifecycle records this turn as completed.",
                        AgentRunState.Failed => "The local lifecycle records this turn as failed or interrupted.",
                        AgentRunState.Running => "The local lifecycle records work in progress; no timeout is treated as completion.",
                        AgentRunState.Waiting => "Codex has queued work for this chat. It may be waiting for its next turn.",
                        AgentRunState.NeedsInput => "An explicit Codex question is awaiting an answer. Work is not complete.",
                        _ => "Local status is unknown. Completion is unconfirmed."
                    };
            var detail = title + "\n" + agent.ProjectPath + "\n" + stateDetail;
            if (summary is { Text.Length: > 0 }) detail += "\n" + summary.Source + ": " + summary.Text;
            rows.Add(new CodexActivityAgent(agent.Id, title, text, ProjectLabel(agent.ProjectPath), state,
                agent.LatestTurnId, agent.ParentId, tracked.ObservedAt,
                agent.CompletedAt ?? tracked.TerminalObservedAt, detail, waitingForChildren && !unknownChild)
                { SummarySource = source, ConfirmingCompletion = confirming && state == AgentRunState.Waiting,
                    HasFailedChildren = failedChildren && state == AgentRunState.Failed,
                    HasInputBlockedChildren = inputBlockedChildren && state == AgentRunState.NeedsInput,
                    CanDismiss = IsSettled(tracked) && !HasUnresolvedDescendant(agent.Id),
                    ActivityIdentity = ActivityIdentity(agent),
                    FeedbackIdentity = feedbackIdentity,
                    Model = agent.Model,
                    ReasoningEffort = agent.ReasoningEffort,
                    MessageHistory = tracked.MessageHistory });
        }
        var chats = GroupChatRows(rows);
        return new(chats.OrderBy(row => row.NeedsInput ? 0 : row.IsRunning ? 1 : row.IsWaiting ? 2 : row.IsUnknown ? 3 : 4)
            .ThenByDescending(row => row.IsRecent ? row.CompletedAt ?? row.ObservedAt : row.ObservedAt)
            .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray(), available, status, checkedAt)
            { DesktopPresence = _desktopPresence, UnlistedUnknownCount = _unlistedUnknownCount, ResetAt = _resetBaseline.ResetAt,
                ProjectUnknownCounts = _unlistedUnknownFamilies.Select(id => _projectOwners.GetValueOrDefault(id)?.Id)
                    .Where(id => id is not null).GroupBy(id => id!, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal) };
    }

    private IReadOnlyList<CodexActivityAgent> GroupChatRows(IReadOnlyList<CodexActivityAgent> rows)
    {
        var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var chats = new List<CodexActivityAgent>();
        foreach (var family in rows.GroupBy(row => ResolveFamilyRoot(_tracked[row.Id].Agent).Id, StringComparer.Ordinal))
        {
            var members = family.Select(row => _tracked[row.Id]).ToArray();
            var knownRoot = TryGetFamilyRoot(family.Key, out var root);
            var rootRow = knownRoot && byId.TryGetValue(root.Id, out var trackedRow) ? trackedRow :
                knownRoot ? CreateRootAnchor(root, members) : new CodexActivityAgent(family.Key,
                    "Chat unavailable", "Parent chat status unavailable", family.First().ProjectLabel,
                    AgentRunState.Unknown, null, null, members.Min(item => item.ObservedAt), null,
                    "An observed subagent's parent chat is unavailable. Chat completion is unconfirmed.");
            // Keep observed child robots with their chat after they finish.
            // Their visible presence is separate from unfinished family work.
            var displayedChildren = family.Where(row => row.Id != rootRow.Id)
                .OrderBy(row => row.NeedsInput ? 0 : row.IsRunning ? 1 : row.IsWaiting ? 2 : 3)
                .ThenBy(row => row.ObservedAt).ThenBy(row => row.Id, StringComparer.Ordinal)
                .Select(row => row with { CanDismiss = false, Subagents = [] }).ToArray();
            var unfinishedChildren = displayedChildren.Where(row =>
                    _tracked[row.Id].Missing || !IsSettled(_tracked[row.Id]) || HasUnresolvedDescendant(row.Id))
                .ToArray();
            var childNeedsInput = members.Any(item => item.Agent.Id != rootRow.Id &&
                !item.Missing && item.Agent.State == AgentRunState.NeedsInput);
            var childRunning = members.Any(item => item.Agent.Id != rootRow.Id &&
                !item.Missing && item.Agent.State == AgentRunState.Running);
            var unknownChildren = knownRoot && IsTerminal(root.State) && members.Any(item => item.Agent.Id != rootRow.Id &&
                (item.Missing || item.Agent.State is AgentRunState.Unknown or AgentRunState.Idle));
            var failedChildren = rootRow.HasFailedChildren || knownRoot && root.State == AgentRunState.Completed &&
                HasFamilyFailure(members, root.Id);
            var waitingForChildren = knownRoot && IsTerminal(root.State) && unfinishedChildren.Length > 0;
            var state = rootRow.IsUnknown || unknownChildren ? AgentRunState.Unknown :
                rootRow.NeedsInput || childNeedsInput ? AgentRunState.NeedsInput :
                rootRow.IsFailed || failedChildren && unfinishedChildren.Length == 0 ? AgentRunState.Failed :
                childRunning ? AgentRunState.Running : rootRow.State;
            var settled = knownRoot && IsTerminal(root.State) && members.All(item => !item.Missing && IsSettled(item));
            if (!settled && state == AgentRunState.Completed) state = AgentRunState.Waiting;
            var detail = rootRow.Detail;
            if (waitingForChildren && childRunning)
                detail += "\nAn observed subagent is still working. This chat is not complete.";
            chats.Add(rootRow with
            {
                ProjectId = _projectOwners.GetValueOrDefault(family.Key)?.Id,
                ProjectLabel = _projectOwners.GetValueOrDefault(family.Key)?.Name ?? rootRow.ProjectLabel,
                ParentId = null,
                State = state,
                WaitingForChildren = waitingForChildren && state is AgentRunState.Running or AgentRunState.Waiting,
                HasInputBlockedChildren = childNeedsInput && state == AgentRunState.NeedsInput,
                HasFailedChildren = failedChildren && state == AgentRunState.Failed,
                ConfirmingCompletion = !settled && state == AgentRunState.Waiting && !waitingForChildren,
                CanDismiss = settled && state is AgentRunState.Completed or AgentRunState.Failed,
                Subagents = displayedChildren,
                Detail = detail
            });
        }
        return chats;
    }

    private CodexActivityAgent CreateRootAnchor(AgentSnapshot root, IReadOnlyList<TrackedAgent> members)
    {
        var title = CodexActivityFeedSummaryReader.Compact(root.Title, 110);
        if (string.IsNullOrWhiteSpace(title)) title = "Codex chat";
        return new(root.Id, title, title, ProjectLabel(root.ProjectPath), root.State, root.LatestTurnId, null,
            members.Min(item => item.ObservedAt), root.CompletedAt,
            title + "\n" + root.ProjectPath + "\nParent chat metadata anchors newly observed subagent work.")
        {
            SummarySource = "Chat title",
            ActivityIdentity = ActivityIdentity(root),
            Model = root.Model,
            ReasoningEffort = root.ReasoningEffort
        };
    }

    private IEnumerable<TrackedAgent> FamilyMembers(string rootId) =>
        _tracked.Values.Where(item => ResolveFamilyRoot(item.Agent).Id == rootId);

    private bool HasFamilyFailure(IEnumerable<TrackedAgent> members, string rootId)
    {
        foreach (var failed in members.Where(item => item.Agent.Id != rootId &&
                     (item.Agent.State == AgentRunState.Failed || item.ObservedChildFailure)))
        {
            var current = failed.Agent;
            TrackedAgent? observed = failed;
            var visited = new HashSet<string>(StringComparer.Ordinal) { current.Id };
            while (current.ParentId is { Length: > 0 } parentId && visited.Add(parentId))
            {
                var parent = _tracked.TryGetValue(parentId, out var tracked) ? tracked.Agent :
                    _familyAnchors.GetValueOrDefault(parentId);
                if (parent is null) break;
                if (observed is not null && ParentHasNewActivity(observed, parent)) break;
                if (parentId == rootId) return true;
                current = parent;
                observed = tracked;
            }
        }
        return false;
    }

    private bool TryGetFamilyRoot(string id, out AgentSnapshot root)
    {
        root = _tracked.TryGetValue(id, out var tracked) ? tracked.Agent : _familyAnchors.GetValueOrDefault(id)!;
        return root is not null && string.IsNullOrWhiteSpace(root.ParentId);
    }

    private FamilyRoot ResolveFamilyRoot(AgentSnapshot agent)
    {
        var chain = new List<string>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        while (true)
        {
            if (indexes.TryGetValue(agent.Id, out var cycleStart))
                return new("unavailable:" + chain.Skip(cycleStart).Order(StringComparer.Ordinal).First());
            indexes.Add(agent.Id, chain.Count);
            chain.Add(agent.Id);
            if (string.IsNullOrWhiteSpace(agent.ParentId)) return new(agent.Id);
            if (_tracked.TryGetValue(agent.ParentId, out var parent)) agent = parent.Agent;
            else if (_familyAnchors.TryGetValue(agent.ParentId, out var anchor)) agent = anchor;
            else return new(agent.ParentId);
        }
    }

    private bool HasUnresolvedDescendant(string parentId)
    {
        foreach (var tracked in _tracked.Values)
        {
            if (IsSettled(tracked) && !tracked.Missing) continue;
            var parent = tracked.Agent.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { tracked.Agent.Id };
            while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
            {
                if (parent == parentId) return true;
                parent = _tracked.TryGetValue(parent, out var ancestor) ? ancestor.Agent.ParentId : null;
            }
        }
        return false;
    }

    private bool HasFailedDescendant(string parentId)
    {
        if (_tracked.TryGetValue(parentId, out var root) && root.ObservedChildFailure) return true;
        foreach (var tracked in _tracked.Values.Where(item => item.Agent.State == AgentRunState.Failed))
        {
            var child = tracked;
            var visited = new HashSet<string>(StringComparer.Ordinal) { child.Agent.Id };
            while (child.Agent.ParentId is { Length: > 0 } parent && visited.Add(parent) &&
                   _tracked.TryGetValue(parent, out var ancestor))
            {
                // A child from an older completed parent turn must not poison a new run.
                if (ParentHasNewActivity(child, ancestor)) break;
                if (parent == parentId) return true;
                child = ancestor;
            }
        }
        return false;
    }

    private void RememberChildFailures()
    {
        foreach (var tracked in _tracked.Values.Where(item => item.Agent.State == AgentRunState.Failed || item.ObservedChildFailure))
        {
            var child = tracked;
            var visited = new HashSet<string>(StringComparer.Ordinal) { child.Agent.Id };
            while (child.Agent.ParentId is { Length: > 0 } parent && visited.Add(parent) &&
                   _tracked.TryGetValue(parent, out var ancestor))
            {
                if (ParentHasNewActivity(child, ancestor)) break;
                ancestor.ObservedChildFailure = true;
                child = ancestor;
            }
        }
    }

    private bool HasUnknownDescendant(string parentId)
    {
        foreach (var tracked in _tracked.Values.Where(item => item.Missing || item.Agent.State == AgentRunState.Unknown))
        {
            var parent = tracked.Agent.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { tracked.Agent.Id };
            while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
            {
                if (parent == parentId) return true;
                parent = _tracked.TryGetValue(parent, out var ancestor) ? ancestor.Agent.ParentId : null;
            }
        }
        return false;
    }

    private bool HasInputBlockedDescendant(string parentId)
    {
        foreach (var tracked in _tracked.Values.Where(item => !item.Missing && item.Agent.State == AgentRunState.NeedsInput))
        {
            var parent = tracked.Agent.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { tracked.Agent.Id };
            while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
            {
                if (parent == parentId) return true;
                parent = _tracked.TryGetValue(parent, out var ancestor) ? ancestor.Agent.ParentId : null;
            }
        }
        return false;
    }

    private static bool IsSettled(TrackedAgent item) => item.Agent.State == AgentRunState.Failed ||
        item.Agent.State == AgentRunState.Completed && item.CompletionScans >= 2 &&
        item.CompletionSince is { } since && item.CompletionLastSeen - since >= CompletionConfirmation;

    private static bool IsTerminal(AgentRunState state) => state is AgentRunState.Completed or AgentRunState.Failed;
    private static string ProjectLabel(string path)
    {
        try { return Path.GetFileName(Path.TrimEndingDirectorySeparator(path)); }
        catch (ArgumentException) { return "Local project"; }
    }

    private sealed class TrackedAgent(AgentSnapshot agent, DateTimeOffset observedAt)
    {
        public AgentSnapshot Agent { get; set; } = agent;
        public string? SelectionActivityIdentity { get; set; } = ActivityIdentity(agent);
        public DateTimeOffset ObservedAt { get; set; } = observedAt;
        public DateTimeOffset? TerminalObservedAt { get; set; }
        public bool Missing { get; set; }
        public string? ParentActivityIdentity { get; set; }
        public DateTimeOffset? ParentActivityStartedAt { get; set; }
        public bool ObservedChildFailure { get; set; }
        public DateTimeOffset? CompletionSince { get; set; }
        public DateTimeOffset CompletionLastSeen { get; set; }
        public int CompletionScans { get; set; }
        public CodexActivityFeedSummary? Summary { get; set; }
        public IReadOnlyList<CodexActivityMessage> MessageHistory { get; set; } = [];
    }
    private sealed record FamilyRoot(string Id);
}
