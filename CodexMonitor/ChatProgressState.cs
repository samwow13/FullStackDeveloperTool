using System.IO;

namespace FullStackLauncher.CodexMonitor;

public sealed record ChatProgressRow(
    string Id,
    string Title,
    AgentRunState State,
    string Status,
    string DotColor,
    bool IsCompleted,
    DateTimeOffset? CompletedAt,
    int AgentCount)
{
    public string ProjectPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string EstimateText { get; init; } = "—";
    public string EstimateDetail { get; init; } = "";
}

// Persist only completion metadata. Display names are hydrated from live snapshots.
public sealed record CompletedChatRecord(
    string ProjectPath,
    string Id,
    string? LatestTurnId,
    DateTimeOffset CompletedAt);

/// <summary>
/// Groups local parent chats and their descendants. New green badges require an
/// observed work or a new turn after a reliable baseline, followed by stable,
/// explicit completion; retained badges are historical.
/// Call on the UI thread. Hiding the overlay must not reset this state.
/// </summary>
public sealed class ChatProgressState
{
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Dictionary<string, CompletedChatRecord>> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedChat> _chats = new(StringComparer.Ordinal);
    private AgentProgressEstimator _estimator = new();
    private Dictionary<string, AgentSnapshot> _latest = new(StringComparer.Ordinal);
    private string _projectPath = "";
    private bool _hasBaseline;

    public IReadOnlyList<ChatProgressRow> Rows { get; private set; } = Array.Empty<ChatProgressRow>();

    public ProgressEstimate GetAgentEstimate(string id) => _estimator.GetEstimate(id);

    public void SetProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = NormalizeProject(projectPath);
        if (string.Equals(normalized, _projectPath, StringComparison.OrdinalIgnoreCase))
            return;
        _projectPath = normalized;
        _chats.Clear();
        _latest.Clear();
        _estimator = new AgentProgressEstimator();
        _hasBaseline = false;
        RebuildRows();
    }

    public void ImportCompleted(IEnumerable<CompletedChatRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _completed.Clear();
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.ProjectPath) ||
                string.IsNullOrWhiteSpace(record.Id) || record.CompletedAt == default)
                continue;
            string project;
            try
            {
                project = NormalizeProject(record.ProjectPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!_completed.TryGetValue(project, out var projectRecords))
                _completed[project] = projectRecords = new(StringComparer.Ordinal);
            if (!projectRecords.TryGetValue(record.Id, out var previous) || previous.CompletedAt < record.CompletedAt)
                projectRecords[record.Id] = record with { ProjectPath = project };
        }
        RebuildRows();
    }

    public IReadOnlyList<CompletedChatRecord> ExportCompleted() => _completed.Values
        .SelectMany(records => records.Values).OrderByDescending(record => record.CompletedAt).ToArray();

    public void ClearCompleted(string? id = null)
    {
        var records = ProjectCompletions();
        var ids = id is null ? records.Keys.ToArray() : [id];
        foreach (var completedId in ids)
        {
            if (!records.Remove(completedId))
                continue;
            if (_chats.TryGetValue(completedId, out var chat) && !chat.Armed)
                _chats.Remove(completedId);
        }
        RebuildRows();
    }

    // Ownership can change when a more specific project is watched. Move the
    // entire observed family, including absent members that still block success.
    // The caller must transfer reminder membership before observing either scope.
    public IReadOnlyCollection<string> TransferTo(ChatProgressState destination, IReadOnlyCollection<string> movedIds)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(movedIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination._projectPath);
        var transferred = movedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (ReferenceEquals(this, destination) || transferred.Count == 0)
            return transferred.ToArray();

        var movingChats = new Dictionary<string, TrackedChat>(StringComparer.Ordinal);
        bool expanded;
        do
        {
            expanded = false;
            foreach (var chat in _chats.Values)
            {
                if (movingChats.ContainsKey(chat.Id) ||
                    (!transferred.Contains(chat.Id) && !chat.MemberIds.Overlaps(transferred)))
                    continue;
                movingChats.Add(chat.Id, chat);
                transferred.Add(chat.Id);
                transferred.UnionWith(chat.MemberIds);
                if (destination._chats.TryGetValue(chat.Id, out var existing))
                    transferred.UnionWith(existing.MemberIds);
                expanded = true;
            }
        } while (expanded);

        var sourceCompletions = ProjectCompletions();
        var destinationCompletions = destination.ProjectCompletions();
        foreach (var chat in movingChats.Values)
        {
            _chats.Remove(chat.Id);
            if (destination._chats.TryGetValue(chat.Id, out var existing))
            {
                // A collision must not erase another observation's missing or
                // unknown members. Conflicting turns need fresh active work.
                chat.MemberIds.UnionWith(existing.MemberIds);
                transferred.UnionWith(existing.MemberIds);
                if (!string.Equals(chat.RootTurnId, existing.RootTurnId, StringComparison.Ordinal) ||
                    (!existing.Armed && existing.State is AgentRunState.Unknown or AgentRunState.Failed))
                    MarkUnavailable(chat);
            }
            chat.AgentCount = chat.MemberIds.Count;
            chat.ResetConfirmation();
            destination._chats[chat.Id] = chat;
            if (chat.State != AgentRunState.Completed && !sourceCompletions.ContainsKey(chat.Id))
                destinationCompletions.Remove(chat.Id);
        }

        foreach (var id in transferred)
        {
            if (sourceCompletions.Remove(id, out var completed) &&
                (!destinationCompletions.TryGetValue(id, out var existing) || existing.CompletedAt < completed.CompletedAt))
                destinationCompletions[id] = completed with { ProjectPath = destination._projectPath };

            if (!_latest.Remove(id, out var snapshot) || !_hasBaseline)
                continue;
            // A destination recovering from connection loss may retain stale
            // display metadata. Only the transferred reliable rows form its
            // baseline; stale rows must never prove a turn finished in the gap.
            if (!destination._hasBaseline)
            {
                destination._latest.Clear();
                destination._hasBaseline = true;
            }
            destination._latest[id] = snapshot;
        }

        RebuildRows();
        destination.RebuildRows();
        return transferred.ToArray();
    }

    public void Observe(IReadOnlyList<AgentSnapshot> agents, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (string.IsNullOrEmpty(_projectPath))
            return;

        var current = new Dictionary<string, AgentSnapshot>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id) || !current.TryAdd(agent.Id, agent))
            {
                ConnectionLost();
                return;
            }
        }

        var groups = new Dictionary<string, List<AgentSnapshot>>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var rootId = FindRoot(agent, current);
            if (rootId is null)
            {
                ConnectionLost();
                return;
            }
            if (!groups.TryGetValue(rootId, out var members))
                groups[rootId] = members = [];
            members.Add(agent);
        }

        _estimator.Observe(agents, now);
        var previous = _hasBaseline ? _latest : new Dictionary<string, AgentSnapshot>(StringComparer.Ordinal);
        _latest = current;
        _hasBaseline = true;
        var completions = ProjectCompletions();
        foreach (var (rootId, members) in groups)
        {
            current.TryGetValue(rootId, out var root);
            _chats.TryGetValue(rootId, out var chat);
            var running = members.Count(member => member.State == AgentRunState.Running);
            var waiting = members.Count(member => member.State == AgentRunState.Waiting);
            if (running + waiting > 0)
            {
                chat ??= new TrackedChat(rootId);
                _chats[rootId] = chat;
                var starting = !chat.Armed ||
                    (root?.LatestTurnId is not null && chat.RootTurnId is not null && root.LatestTurnId != chat.RootTurnId);
                if (starting)
                    chat.MemberIds.Clear();
                chat.Armed = true;
                chat.RootTurnId = root?.LatestTurnId;
                TrackMembers(chat, members, previous, starting);
                chat.Title = root?.Title ?? chat.Title;
                chat.AgentCount = chat.MemberIds.Count;
                chat.State = running > 0 ? AgentRunState.Running : AgentRunState.Waiting;
                chat.Status = running > 0
                    ? (waiting > 0 ? $"{running} running · {waiting} waiting" : "In progress")
                    : "Queued or waiting";
                chat.ResetConfirmation();
                completions.Remove(rootId);
                continue;
            }

            // A short turn can start and finish between polls. A changed, explicit
            // root turn ID proves new work after a reliable baseline; startup or
            // reconnection history alone never creates a completion badge.
            if (root?.State == AgentRunState.Completed && root.LatestTurnId is not null &&
                previous.TryGetValue(rootId, out var priorRoot) && priorRoot.LatestTurnId is not null &&
                root.LatestTurnId != priorRoot.LatestTurnId)
            {
                chat ??= new TrackedChat(rootId);
                _chats[rootId] = chat;
                chat.MemberIds.Clear();
                chat.Armed = true;
                chat.RootTurnId = root.LatestTurnId;
                chat.ResetConfirmation();
                completions.Remove(rootId);
            }

            // Opening the monitor never imports the entire completed chat history.
            if (chat is null)
                continue;
            chat.Title = root?.Title ?? chat.Title;
            if (!chat.Armed)
                continue;

            TrackMembers(chat, members, previous, starting: false);
            chat.AgentCount = chat.MemberIds.Count;
            var groupIds = members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
            var watched = members.Where(member => chat.MemberIds.Contains(member.Id)).ToArray();
            var missing = chat.MemberIds.Any(id => !groupIds.Contains(id));
            var failed = watched.Any(member => member.State == AgentRunState.Failed);
            var unknown = watched.Any(member => member.State != AgentRunState.Completed);
            if (missing || failed || unknown)
            {
                chat.State = failed ? AgentRunState.Failed : AgentRunState.Unknown;
                chat.Status = failed ? "Failed or interrupted" : "Completion unconfirmed";
                chat.ResetConfirmation();
                // A disappearance/failure must observe work again before a later
                // completed projection can create a new completion badge.
                if (missing || failed)
                    chat.Armed = false;
                continue;
            }

            // Include turn markers so a changed turn restarts the grace interval.
            var marker = string.Join("\n", watched.OrderBy(member => member.Id, StringComparer.Ordinal)
                .Select(member => $"{member.Id}:{member.LatestTurnId}:{member.CompletedAt?.UtcTicks}"));
            if (chat.CompletionMarker != marker)
            {
                chat.ResetConfirmation();
                chat.CompletionMarker = marker;
            }
            chat.CompletionObservedAt ??= now;
            chat.CompletionScans = Math.Min(2, chat.CompletionScans + 1);
            if (chat.CompletionScans < 2 || now - chat.CompletionObservedAt.Value < CompletionGrace)
            {
                chat.State = AgentRunState.Waiting;
                chat.Status = "Confirming completion";
                continue;
            }

            var completedAt = watched.Where(member => member.CompletedAt.HasValue)
                .Select(member => member.CompletedAt!.Value).DefaultIfEmpty(now).Max();
            completions[rootId] = new CompletedChatRecord(_projectPath, rootId, root?.LatestTurnId, completedAt);
            chat.State = AgentRunState.Completed;
            chat.Status = "Finished";
            chat.Armed = false;
            chat.ResetConfirmation();
        }

        foreach (var chat in _chats.Values.Where(chat => !groups.ContainsKey(chat.Id) && !completions.ContainsKey(chat.Id)))
            MarkUnavailable(chat);
        RebuildRows();
    }

    public void ConnectionLost()
    {
        _hasBaseline = false;
        _estimator.ConnectionLost();
        foreach (var chat in _chats.Values)
            MarkUnavailable(chat);
        RebuildRows();
    }

    private void RebuildRows()
    {
        var records = ProjectCompletions();
        var rows = new Dictionary<string, ChatProgressRow>(StringComparer.Ordinal);
        foreach (var chat in _chats.Values)
        {
            if (records.ContainsKey(chat.Id))
                continue;
            var estimate = _estimator.GetChatEstimate(chat.MemberIds, chat.State);
            rows[chat.Id] = new ChatProgressRow(chat.Id, chat.Title, chat.State, chat.Status,
                Color(chat.State), false, null, chat.AgentCount)
            {
                ProjectPath = _projectPath,
                ProjectName = ProjectName(),
                EstimateText = estimate.Text,
                EstimateDetail = estimate.Detail
            };
        }
        foreach (var record in records.Values)
        {
            var title = _latest.TryGetValue(record.Id, out var snapshot) ? snapshot.Title
                : _chats.TryGetValue(record.Id, out var tracked) ? tracked.Title : "Completed chat";
            var count = _chats.TryGetValue(record.Id, out var chat) ? chat.AgentCount : 1;
            var estimate = AgentProgressEstimator.Completed();
            rows[record.Id] = new ChatProgressRow(record.Id, title, AgentRunState.Completed,
                "Finished", Color(AgentRunState.Completed), true, record.CompletedAt, count)
            {
                ProjectPath = _projectPath,
                ProjectName = ProjectName(),
                EstimateText = estimate.Text,
                EstimateDetail = estimate.Detail
            };
        }
        Rows = rows.Values.OrderBy(row => row.State is AgentRunState.Running or AgentRunState.Waiting ? 0 : row.IsCompleted ? 2 : 1)
            .ThenByDescending(row => row.CompletedAt).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Dictionary<string, CompletedChatRecord> ProjectCompletions()
    {
        if (!_completed.TryGetValue(_projectPath, out var records))
            _completed[_projectPath] = records = new(StringComparer.Ordinal);
        return records;
    }

    private static string? FindRoot(AgentSnapshot agent, IReadOnlyDictionary<string, AgentSnapshot> agents)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(agent.Id))
                return null;
            if (string.IsNullOrWhiteSpace(agent.ParentId))
                return agent.Id;
            if (!agents.TryGetValue(agent.ParentId, out var parent))
                return agent.ParentId;
            agent = parent;
        }
    }

    private static void TrackMembers(TrackedChat chat, IEnumerable<AgentSnapshot> members,
        IReadOnlyDictionary<string, AgentSnapshot> previous, bool starting)
    {
        chat.MemberIds.Add(chat.Id);
        foreach (var member in members)
        {
            if (member.State == AgentRunState.Idle)
                continue;
            var wasPresent = previous.TryGetValue(member.Id, out var prior);
            var changedTurn = wasPresent && member.LatestTurnId is not null && member.LatestTurnId != prior!.LatestTurnId;
            // Old, inactive descendants belong to earlier turns. Retain children
            // that we see working, arriving, or advancing during this observed run.
            if (member.State is AgentRunState.Running or AgentRunState.Waiting || changedTurn || (!starting && !wasPresent))
                chat.MemberIds.Add(member.Id);
        }
    }

    private static void MarkUnavailable(TrackedChat chat)
    {
        chat.Armed = false;
        chat.State = AgentRunState.Unknown;
        chat.Status = "Status unavailable";
        chat.ResetConfirmation();
    }

    private static string NormalizeProject(string projectPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath.Trim().Replace('/', '\\')));

    private string ProjectName()
    {
        var name = Path.GetFileName(_projectPath);
        return string.IsNullOrWhiteSpace(name) ? _projectPath : name;
    }

    private static string Color(AgentRunState state) => state switch
    {
        AgentRunState.Running or AgentRunState.Waiting => "#F4BE4F",
        AgentRunState.Completed => "#53D7A0",
        AgentRunState.Failed => "#F07D86",
        _ => "#8E9AAE"
    };

    private sealed class TrackedChat(string id)
    {
        public string Id { get; } = id;
        public string Title { get; set; } = "Codex chat";
        public HashSet<string> MemberIds { get; } = new(StringComparer.Ordinal);
        public bool Armed { get; set; }
        public string? RootTurnId { get; set; }
        public AgentRunState State { get; set; } = AgentRunState.Unknown;
        public string Status { get; set; } = "Status unavailable";
        public int AgentCount { get; set; } = 1;
        public string? CompletionMarker { get; set; }
        public DateTimeOffset? CompletionObservedAt { get; set; }
        public int CompletionScans { get; set; }

        public void ResetConfirmation()
        {
            CompletionMarker = null;
            CompletionObservedAt = null;
            CompletionScans = 0;
        }
    }
}
