using System.Security.Cryptography;
using FullStackLauncher.Services;

namespace FullStackLauncher.AgentBridge;

// Token-free dashboard view. "using" is an agent declaration, not a measured
// network connection. Reservation and wait states apply only to scoped claims.
internal sealed record AgentServiceActivity(string SessionId, string Owner, string State,
    DateTime LastSeenUtc, DateTime? ExpiresUtc, int? QueuePosition,
    bool DeclaredUse, bool HoldsReservation, bool IsActing, bool IsStale);

internal sealed record AgentSessionIdentity(string SessionId, string Owner);

/// <summary>
/// Cooperative, in-memory reservations for local agent sessions. Dashboard controls remain
/// available to the user. All calls occur on the WPF dispatcher.
/// </summary>
internal sealed partial class AgentBridgeCoordination
{
    internal const string AgentRestartMessage = "Agent initiated restart";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleDeclarationRetention = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, AgentSession> _sessions = [];
    private readonly Dictionary<string, ProjectReservations> _projects = [];
    private readonly Queue<BridgeEvent> _events = new();
    private readonly Queue<StaleServiceDeclaration> _staleDeclarations = new();
    private long _sequence;

    internal string InstanceId { get; } = Guid.NewGuid().ToString("N");

    private sealed class AgentSession(string projectId, string owner, long startSequence, DateTime now)
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string ProjectId { get; } = projectId;
        internal string Owner { get; } = owner;
        internal long StartSequence { get; } = startSequence;
        internal long NotificationSequence { get; set; } = startSequence;
        internal DateTime LastSeenUtc { get; set; } = now;
        internal DateTime ExpiresUtc { get; set; } = now + SessionLifetime;
        internal string? ReplyChatId { get; set; }
        internal HashSet<string> DeclaredServiceIds { get; } = [];
        internal HashSet<string> ActingServiceIds { get; } = [];
    }

    private sealed class Reservation(string sessionToken, string purpose, string? ticket,
        string? serviceId, DateTime now)
    {
        internal string SessionToken { get; } = sessionToken;
        internal string Purpose { get; } = purpose;
        internal string? Ticket { get; } = ticket;
        internal string? ServiceId { get; } = serviceId;
        internal string Token { get; } = NewToken();
        internal DateTime StartedUtc { get; } = now;
        internal DateTime ExpiresUtc { get; set; } = now + SessionLifetime;
    }

    private sealed class WaitingAgent(string sessionToken, string purpose, string? serviceId)
    {
        internal string SessionToken { get; } = sessionToken;
        internal string Purpose { get; } = purpose;
        internal string? ServiceId { get; } = serviceId;
        internal string Ticket { get; } = NewToken();
    }

    private sealed record StaleServiceDeclaration(string ProjectId, string ServiceId,
        string SessionId, string Owner, DateTime LastSeenUtc, DateTime ExpiresUtc,
        DateTime RetainUntilUtc);

    private sealed class ProjectReservations
    {
        internal Reservation? Current { get; set; }
        internal LinkedList<WaitingAgent> Waiting { get; } = new();
        internal bool AgentStartsSuspended { get; set; }
        internal DateTime? SuspendedUtc { get; set; }
        internal long DashboardActionRevision { get; set; }
    }

    private sealed record BridgeEvent(long Sequence, string ProjectId, string? ServiceId,
        string Action, string Result, DateTime TimeUtc, string? Message = null, string? Severity = null);

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    internal object Register(string projectId, string owner)
    {
        Prune();
        owner = CleanText(owner, 80);
        if (owner.Length == 0) throw new ArgumentException("Give this agent session a short owner label.");
        var token = NewToken();
        var now = DateTime.UtcNow;
        _sessions.Add(token, new AgentSession(projectId, owner, _sequence, now));
        return new { instanceId = InstanceId, sessionToken = token, sessionId = _sessions[token].Id, projectId, owner,
            expiresUtc = now + SessionLifetime, eventCursor = _sequence };
    }

    internal object Heartbeat(string projectId, string sessionToken, object? replyInbox = null)
    {
        var session = RequireSession(projectId, sessionToken);
        var expires = DateTime.UtcNow + SessionLifetime;
        session.ExpiresUtc = expires;
        if (_projects.TryGetValue(projectId, out var project) && project.Current?.SessionToken == sessionToken)
            project.Current.ExpiresUtc = expires;
        var notifications = _events.Where(item => item.ProjectId == projectId &&
            item.Message is not null && item.Sequence > session.NotificationSequence).ToArray();
        session.NotificationSequence = _sequence;
        return new { instanceId = InstanceId, projectId, expiresUtc = expires, notifications, replyInbox };
    }

    internal AgentSessionIdentity SessionIdentity(string projectId, string sessionToken)
    {
        var session = RequireSession(projectId, sessionToken);
        return new(session.Id, session.Owner);
    }

    internal object Unregister(string projectId, string sessionToken)
    {
        RequireSession(projectId, sessionToken);
        _sessions.Remove(sessionToken);
        Prune();
        return new { instanceId = InstanceId, projectId, status = "unregistered" };
    }

    internal object DeclareServiceUse(string projectId, string serviceId, string sessionToken)
    {
        var session = RequireSession(projectId, sessionToken);
        session.DeclaredServiceIds.Add(serviceId);
        return new { instanceId = InstanceId, projectId, serviceId, sessionId = session.Id,
            status = "agent_declared_use", lastSeenUtc = session.LastSeenUtc,
            expiresUtc = session.ExpiresUtc };
    }

    internal object ReleaseServiceUse(string projectId, string serviceId, string sessionToken)
    {
        var session = RequireSession(projectId, sessionToken);
        session.DeclaredServiceIds.Remove(serviceId);
        return new { instanceId = InstanceId, projectId, serviceId, sessionId = session.Id,
            status = "released" };
    }

    internal IReadOnlyList<AgentServiceActivity> ServiceActivity(string projectId, string serviceId)
    {
        Prune();
        _projects.TryGetValue(projectId, out var state);
        var lease = state?.Current;
        var waiting = state?.Waiting.Select((agent, index) => (agent, position: index + 1))
            .Where(item => item.agent.ServiceId == serviceId)
            .ToDictionary(item => item.agent.SessionToken, item => item.position)
            ?? new Dictionary<string, int>();
        var active = _sessions.Select(item =>
        {
            var (token, session) = item;
            var declaredUse = session.DeclaredServiceIds.Contains(serviceId);
            var holdsReservation = lease?.ServiceId == serviceId && lease.SessionToken == token;
            var isActing = session.ActingServiceIds.Contains(serviceId);
            var queuePosition = waiting.GetValueOrDefault(token);
            if (session.ProjectId != projectId ||
                !(declaredUse || holdsReservation || isActing || queuePosition > 0)) return null;
            var states = new List<string>();
            if (isActing) states.Add("acting");
            if (declaredUse) states.Add("using (agent declared)");
            if (holdsReservation) states.Add("reserved");
            if (queuePosition > 0) states.Add("waiting");
            return new AgentServiceActivity(session.Id, session.Owner, string.Join(", ", states),
                session.LastSeenUtc, session.ExpiresUtc,
                queuePosition > 0 ? queuePosition : null, declaredUse, holdsReservation, isActing, false);
        }).Where(item => item is not null).Cast<AgentServiceActivity>();
        var stale = _staleDeclarations.Where(item => item.ProjectId == projectId && item.ServiceId == serviceId)
            .Select(item => new AgentServiceActivity(item.SessionId, item.Owner,
                "stale declaration", item.LastSeenUtc, item.ExpiresUtc,
                null, false, false, false, true));
        return active.Concat(stale).OrderBy(item => item.IsStale)
            .ThenBy(item => item.QueuePosition ?? 0).ThenBy(item => item.Owner, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal object ReservationStatus(string projectId)
    {
        Prune();
        var state = State(projectId);
        var lease = state.Current;
        return new
        {
            instanceId = InstanceId,
            owner = lease is not null && _sessions.TryGetValue(lease.SessionToken, out var session) ? session.Owner : null,
            purpose = lease?.Purpose,
            serviceId = lease?.ServiceId,
            startedUtc = lease?.StartedUtc,
            expiresUtc = lease?.ExpiresUtc,
            waiting = state.Waiting.Count,
            agentStartsSuspended = state.AgentStartsSuspended,
            suspendedUtc = state.SuspendedUtc
        };
    }

    internal object Claim(string projectId, string sessionToken, string purpose, string? serviceId = null)
    {
        RequireSession(projectId, sessionToken);
        purpose = CleanText(purpose, 200);
        if (purpose.Length == 0) throw new ArgumentException("Describe planned work before claiming a project.");
        var state = State(projectId);
        if (state.Current?.SessionToken == sessionToken)
        {
            if (state.Current.ServiceId != serviceId)
                throw new InvalidOperationException("Release the current reservation before changing its service scope.");
            return Acquired(projectId, state.Current);
        }
        var existing = state.Waiting.FirstOrDefault(item => item.SessionToken == sessionToken);
        if (existing is null)
        {
            if (state.Waiting.Count >= 64) throw new InvalidOperationException("Project wait queue is full.");
            existing = new WaitingAgent(sessionToken, purpose, serviceId);
            state.Waiting.AddLast(existing);
        }
        else if (existing.ServiceId != serviceId)
            throw new InvalidOperationException("Cancel the current wait ticket before changing its service scope.");
        GrantNext(projectId, state);
        if (state.Current?.Ticket == existing.Ticket) return Acquired(projectId, state.Current);
        return Waiting(projectId, existing.Ticket, state.Waiting.ToList().FindIndex(item => item.Ticket == existing.Ticket) + 1,
            existing.ServiceId);
    }

    internal object CheckWait(string projectId, string sessionToken, string ticket)
    {
        RequireSession(projectId, sessionToken);
        var state = State(projectId);
        if (state.Current?.Ticket == ticket && state.Current.SessionToken == sessionToken)
            return Acquired(projectId, state.Current);
        var position = state.Waiting.ToList().FindIndex(item => item.Ticket == ticket && item.SessionToken == sessionToken);
        if (position < 0) throw new InvalidOperationException("Wait ticket expired or was canceled. Claim the project again.");
        return Waiting(projectId, ticket, position + 1, state.Waiting.First(item =>
            item.Ticket == ticket && item.SessionToken == sessionToken).ServiceId);
    }

    internal bool IsTicketAcquired(string projectId, string sessionToken, string ticket) =>
        State(projectId).Current is { } lease && lease.SessionToken == sessionToken && lease.Ticket == ticket;

    internal object CancelWait(string projectId, string sessionToken, string ticket)
    {
        RequireSession(projectId, sessionToken);
        var state = State(projectId);
        if (state.Current?.Ticket == ticket && state.Current.SessionToken == sessionToken)
        {
            state.Current = null;
            GrantNext(projectId, state);
            return new { instanceId = InstanceId, projectId, status = "released" };
        }
        var node = state.Waiting.First;
        while (node is not null)
        {
            if (node.Value.Ticket == ticket && node.Value.SessionToken == sessionToken)
            {
                state.Waiting.Remove(node);
                return new { instanceId = InstanceId, projectId, status = "canceled" };
            }
            node = node.Next;
        }
        throw new InvalidOperationException("Wait ticket expired or was canceled.");
    }

    internal object Release(string projectId, string sessionToken, string leaseToken)
    {
        RequireLease(projectId, sessionToken, leaseToken);
        var state = State(projectId);
        state.Current = null;
        GrantNext(projectId, state);
        return new { instanceId = InstanceId, projectId, status = "released" };
    }

    internal void RequireLease(string projectId, string sessionToken, string leaseToken,
        string? serviceId = null)
    {
        RequireSession(projectId, sessionToken);
        var lease = State(projectId).Current;
        if (lease is null || lease.SessionToken != sessionToken || lease.Token != leaseToken)
            throw new InvalidOperationException("Project reservation missing, expired, or overridden. Claim again before changing services.");
        if (serviceId is not null && lease.ServiceId is not null && lease.ServiceId != serviceId)
            throw new InvalidOperationException("Reservation is scoped to another service. Release it before changing this service.");
        var expires = DateTime.UtcNow + SessionLifetime;
        lease.ExpiresUtc = expires;
        _sessions[sessionToken].ExpiresUtc = expires;
    }

    internal void BeginServiceAction(string projectId, string sessionToken, string serviceId)
    {
        RequireSession(projectId, sessionToken).ActingServiceIds.Add(serviceId);
    }

    internal void EndServiceAction(string projectId, string sessionToken, string serviceId)
    {
        if (_sessions.TryGetValue(sessionToken, out var session) && session.ProjectId == projectId)
            session.ActingServiceIds.Remove(serviceId);
    }

    internal void Override(string projectId)
    {
        var state = State(projectId);
        state.Current = null;
        state.Waiting.Clear();
        state.AgentStartsSuspended = true;
        state.SuspendedUtc = DateTime.UtcNow;
    }

    internal bool AreAgentStartsSuspended(string projectId) => State(projectId).AgentStartsSuspended;

    internal bool HasActiveProjectWork(string projectId)
    {
        Prune();
        var state = State(projectId);
        return state.Current is not null || state.Waiting.Count > 0 ||
            _sessions.Values.Any(session => session.ProjectId == projectId &&
                (session.ActingServiceIds.Count > 0 || session.DeclaredServiceIds.Count > 0));
    }

    internal long DashboardActionRevision(string projectId) => State(projectId).DashboardActionRevision;

    internal void ResumeAgentStarts(string projectId)
    {
        var state = State(projectId);
        state.AgentStartsSuspended = false;
        state.SuspendedUtc = null;
    }

    internal void Record(string projectId, string? serviceId, string action, string result)
    {
        if (result == "requested" && action is ("start" or "restart" or "stop" or "force_stop_all"))
            State(projectId).DashboardActionRevision++;
        Prune();
        if (!_sessions.Values.Any(session => session.ProjectId == projectId)) return;
        _events.Enqueue(new BridgeEvent(++_sequence, projectId, serviceId, action, result, DateTime.UtcNow));
        while (_events.Count > 200) _events.Dequeue();
    }

    internal void NotifyAgentRestart(string projectId, string sessionToken, string serviceId)
    {
        RequireSession(projectId, sessionToken);
        _events.Enqueue(new BridgeEvent(++_sequence, projectId, serviceId, "agent_restart", "initiated",
            DateTime.UtcNow, AgentRestartMessage, "warning"));
        while (_events.Count > 200) _events.Dequeue();
    }

    internal object Events(string projectId, string sessionToken, long sinceSequence)
    {
        var session = RequireSession(projectId, sessionToken);
        var events = _events.Where(item => item.ProjectId == projectId &&
            item.Sequence > Math.Max(session.StartSequence, sinceSequence)).ToArray();
        return new { instanceId = InstanceId, projectId, nextSequence = _sequence, events };
    }

    private AgentSession RequireSession(string projectId, string sessionToken)
    {
        Prune();
        if (!_sessions.TryGetValue(sessionToken, out var session) || session.ProjectId != projectId)
            throw new InvalidOperationException("Project session expired or unavailable. Register again; old event history is unavailable.");
        session.LastSeenUtc = DateTime.UtcNow;
        session.ExpiresUtc = session.LastSeenUtc + SessionLifetime;
        return session;
    }

    private ProjectReservations State(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var state)) _projects.Add(projectId, state = new ProjectReservations());
        return state;
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, session) in _sessions.Where(item => item.Value.ExpiresUtc <= now).ToArray())
        {
            foreach (var serviceId in session.DeclaredServiceIds)
                _staleDeclarations.Enqueue(new StaleServiceDeclaration(session.ProjectId, serviceId,
                    session.Id, session.Owner, session.LastSeenUtc, session.ExpiresUtc,
                    session.ExpiresUtc + StaleDeclarationRetention));
            _sessions.Remove(token);
        }
        var recentDeclarations = _staleDeclarations.Where(item => item.RetainUntilUtc > now)
            .TakeLast(128).ToArray();
        _staleDeclarations.Clear();
        foreach (var declaration in recentDeclarations) _staleDeclarations.Enqueue(declaration);
        foreach (var (projectId, state) in _projects)
        {
            if (state.Current is { } lease &&
                (lease.ExpiresUtc <= now || !_sessions.ContainsKey(lease.SessionToken))) state.Current = null;
            var node = state.Waiting.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!_sessions.ContainsKey(node.Value.SessionToken)) state.Waiting.Remove(node);
                node = next;
            }
            GrantNext(projectId, state);
        }
        if (_events.Count > 0)
        {
            var retained = _events.Where(item => _sessions.Values.Any(session =>
                session.ProjectId == item.ProjectId && item.Sequence > session.StartSequence)).ToArray();
            _events.Clear();
            foreach (var item in retained) _events.Enqueue(item);
        }
    }

    private void GrantNext(string projectId, ProjectReservations state)
    {
        if (state.Current is not null || state.Waiting.First is null) return;
        var first = state.Waiting.First.Value;
        state.Waiting.RemoveFirst();
        if (!_sessions.ContainsKey(first.SessionToken)) { GrantNext(projectId, state); return; }
        state.Current = new Reservation(first.SessionToken, first.Purpose, first.Ticket,
            first.ServiceId, DateTime.UtcNow);
    }

    private object Acquired(string projectId, Reservation lease) => new
    {
        instanceId = InstanceId, projectId, status = "acquired", leaseToken = lease.Token,
        purpose = lease.Purpose, serviceId = lease.ServiceId,
        startedUtc = lease.StartedUtc, expiresUtc = lease.ExpiresUtc
    };

    private object Waiting(string projectId, string ticket, int position, string? serviceId) => new
    {
        instanceId = InstanceId, projectId, status = "waiting", ticket, position, serviceId
    };

    private static string CleanText(string value, int maxLength)
    {
        value = value.Trim();
        if (value.Length > maxLength || value.Any(char.IsControl) || SensitiveDataProtection.ContainsLiteralCredential(value))
            throw new ArgumentException("Owner or purpose must be short text without controls or credentials.");
        return value;
    }
}
