using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Services;

public sealed record CodexGitCheckIdleUpdate(
    bool Ready,
    string Status,
    string? IdleSignature = null);

/// <summary>
/// Serializes read-only local Codex status reads for scheduled and manual Git
/// checks. It does not depend on the monitor's saved project watches or alerts.
/// A captured ready result must pass ConfirmReadyAsync immediately before the
/// Git operation; local metadata cannot prevent a new task starting afterward.
/// </summary>
public sealed class CodexGitCheckIdleGate
{
    private readonly LocalCodexReader _reader;
    private readonly CodexGitCheckIdleState _state = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);

    public CodexGitCheckIdleGate(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        _reader = new LocalCodexReader(codexHome);
    }

    public async Task<CodexGitCheckIdleUpdate> ReadAsync(CancellationToken token = default)
    {
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var retainedIds = _state.RetainedAgentIds.ToArray();
            var agents = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _reader.ReadAllSnapshot(retainedIds);
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return _state.Observe(agents, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _state.ConnectionLost();
            throw;
        }
        catch (Exception)
        {
            // Never surface local task titles, IDs, paths, or source content in
            // the dashboard. Missing or changed metadata keeps the check queued.
            return _state.ConnectionLost();
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<bool> ConfirmReadyAsync(CodexGitCheckIdleUpdate captured,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!captured.Ready || captured.IdleSignature is null)
            return false;
        var fresh = await ReadAsync(token).ConfigureAwait(false);
        return fresh.Ready && string.Equals(captured.IdleSignature, fresh.IdleSignature,
            StringComparison.Ordinal);
    }
}

/// <summary>
/// Evaluates complete, reliable snapshots supplied by the local status reader.
/// Call from one serialized worker. No observed-work arming is required: a
/// trustworthy initial idle baseline can release an already-due Git check.
/// Failed or interrupted terminal turns prove inactivity, not successful work.
/// </summary>
public sealed class CodexGitCheckIdleState
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(10);
    private readonly HashSet<string> _unresolvedAgentIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retainedAgentIds = new(StringComparer.Ordinal);
    private string? _stableMarker;
    private DateTimeOffset _stableSince;
    private int _stableScans;

    public IReadOnlyCollection<string> RetainedAgentIds => _retainedAgentIds.ToArray();

    public CodexGitCheckIdleUpdate Observe(IReadOnlyList<AgentSnapshot> agents,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var current = new Dictionary<string, AgentSnapshot>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (agent is null || string.IsNullOrWhiteSpace(agent.Id) ||
                !Enum.IsDefined(agent.State) || !current.TryAdd(agent.Id, agent))
                return ConnectionLost("Codex status is inconsistent; Git check queued.");
        }

        var families = new Dictionary<string, List<AgentSnapshot>>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var member = agent;
            while (!string.IsNullOrWhiteSpace(member.ParentId))
            {
                if (!visited.Add(member.Id) || visited.Contains(member.ParentId))
                    return ConnectionLost("Codex agent membership is inconsistent; Git check queued.");
                if (!current.TryGetValue(member.ParentId, out var parent))
                    break;
                member = parent;
            }
            var rootId = string.IsNullOrWhiteSpace(member.ParentId) ? member.Id : member.ParentId;
            if (!families.TryGetValue(rootId, out var members))
                families[rootId] = members = [];
            members.Add(agent);
        }

        foreach (var (rootId, members) in families)
        {
            if (!members.Any(agent => BlocksIdle(agent.State)))
                continue;
            // Keep the entire observed active family, including the actual
            // missing ancestor identity. Archival/disappearance is not proof
            // that a task stopped; retained terminal rows can settle this later.
            _unresolvedAgentIds.Add(rootId);
            foreach (var member in members)
            {
                _unresolvedAgentIds.Add(member.Id);
                if (!string.IsNullOrWhiteSpace(member.ParentId))
                    _unresolvedAgentIds.Add(member.ParentId);
            }
        }
        // Retain settled identities in reader requests too. Otherwise an
        // archived terminal row would vanish between readiness and confirmation
        // merely because its retained activity has just been resolved.
        _retainedAgentIds.UnionWith(_unresolvedAgentIds);

        var running = agents.Count(agent => agent.State == AgentRunState.Running);
        var waiting = agents.Count(agent => agent.State == AgentRunState.Waiting);
        var needsInput = agents.Count(agent => agent.State == AgentRunState.NeedsInput);
        if (running + waiting + needsInput > 0)
            return Block($"Waiting for all local Codex agents: {running} running · {waiting} queued or waiting · {needsInput} need an answer.");
        if (agents.Any(agent => agent.State == AgentRunState.Unknown))
            return Block("Local Codex status is unknown; Git check queued.");
        if (_unresolvedAgentIds.Any(id => !current.TryGetValue(id, out var agent) ||
            !IsTerminal(agent.State)))
            return Block("An observed Codex chat or agent is missing or has no terminal status; Git check queued.");

        var metadata = agents.OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .Select(agent => new
            {
                agent.Id,
                agent.ParentId,
                agent.ProjectPath,
                agent.State,
                agent.LatestTurnId,
                agent.CompletedAt
            }).ToArray();
        var marker = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata))));
        if (_stableMarker != marker || now < _stableSince)
        {
            _stableMarker = marker;
            _stableSince = now;
            _stableScans = 0;
        }
        _stableScans = Math.Min(2, _stableScans + 1);
        if (_stableScans < 2 || now - _stableSince < IdleGrace)
            return new(false, "All local Codex agents appear idle; confirming for 10 seconds.");

        // Only stable, explicit terminal observations resolve retained work.
        // Preserve missing IDs through read failures and later empty snapshots.
        _unresolvedAgentIds.Clear();
        return new(true, "All local Codex agents idle; Git check ready.", marker);
    }

    public CodexGitCheckIdleUpdate ConnectionLost() =>
        ConnectionLost("Local Codex status unavailable; Git check queued.");

    private CodexGitCheckIdleUpdate ConnectionLost(string status) => Block(status);

    private CodexGitCheckIdleUpdate Block(string status)
    {
        _stableMarker = null;
        _stableScans = 0;
        return new(false, status);
    }

    private static bool BlocksIdle(AgentRunState state) =>
        state is AgentRunState.Running or AgentRunState.Waiting or AgentRunState.NeedsInput or AgentRunState.Unknown;

    private static bool IsTerminal(AgentRunState state) =>
        state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Idle;
}
