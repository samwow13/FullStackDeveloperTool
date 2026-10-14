using System.Diagnostics;
using System.IO;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

internal sealed partial class QueueOwnerHost
{
    private PendingDashboardHandoff? _dashboardHandoff;
    private readonly HashSet<(int ProcessId, long StartedAt)> _retiredDashboards = [];

    private void BindDashboardForRequest(QueueOwnerRequest request)
    {
        lock (_dashboardSync)
        {
            if (request.DashboardProcessId is { } pid && request.DashboardProcessStartedAtUtcTicks is { } ticks &&
                _retiredDashboards.Contains((pid, ticks)))
                throw new InvalidOperationException("This dashboard handed queue ownership to its replacement. Reconnect through the replacement dashboard.");
            if (_dashboardHandoff != null)
            {
                if (request.Command == "status") return; // Old in-flight polls cannot reclaim the owner.
                var actualPauseOrStop = request.Command is "shutdown" or "stop" ||
                    request.Command == "pause" && request.Paused != false ||
                    request.Command == "pause-all" && request.Paused == true;
                if (!actualPauseOrStop)
                    throw new InvalidOperationException("Launcher replacement is in progress. Existing queue work continues; new queue commands wait for startup.");
                return; // Explicit stopping/pausing remains available without rebinding.
            }
            BindDashboard(request.DashboardProcessId, request.DashboardProcessStartedAtUtcTicks);
        }
    }

    private QueueOwnerReply ApplyDashboardHandoff(QueueOwnerRequest request)
    {
        lock (_dashboardSync)
        {
            if (request.Command == "handoff-complete")
            {
                if (_dashboardHandoff == null)
                {
                    if (_dashboardProcessId != request.DashboardProcessId ||
                        _dashboardStartedAtUtcTicks != request.DashboardProcessStartedAtUtcTicks)
                        throw new InvalidOperationException("No pending queue handoff belongs to this dashboard. Existing ownership was preserved.");
                    return new(true, "Queue dashboard ownership is unchanged.", "handoff-not-pending");
                }
                var completing = _dashboardHandoff;
                if (!completing.Transferred || request.DashboardProcessId != completing.Target.Id ||
                    request.DashboardProcessStartedAtUtcTicks != completing.TargetStartedAt ||
                    !ConfirmedExited(completing.Source) || !ConfirmedAlive(completing.Target))
                    throw new InvalidOperationException("Queue handoff startup could not be confirmed. Active work continues; new dispatch remains paused.");
                _retiredDashboards.Add((completing.Source.Id, completing.SourceStartedAt));
                _dashboardHandoff = null;
                completing.Dispose();
                return new(true, "Queue owner connected to the replacement dashboard.", "handoff-complete");
            }

            if (!Guid.TryParseExact(request.HandoffId, "N", out _) || request.DashboardProcessId is null or <= 4 ||
                request.DashboardProcessStartedAtUtcTicks is null or <= 0)
                throw new ArgumentException("The queue dashboard handoff identity is invalid.");
            var sourceId = request.DashboardProcessId.Value;
            var sourceStartedAt = request.DashboardProcessStartedAtUtcTicks.Value;
            if (request.Command == "handoff-rollback" && _dashboardHandoff == null)
            {
                if (_dashboardProcessId != sourceId || _dashboardStartedAtUtcTicks != sourceStartedAt)
                    throw new InvalidOperationException("Queue ownership rollback could not be confirmed. Dispatch remains paused; keep Launcher open and review the queue owner.");
                return new(true, "Queue owner remains with the original dashboard.", "handoff-rolled-back");
            }
            if (request.Command == "handoff-prepare" && _dashboardHandoff == null)
            {
                if (_dashboardProcessId != sourceId || _dashboardStartedAtUtcTicks != sourceStartedAt ||
                    _dashboard == null || !ConfirmedAlive(_dashboard))
                    throw new InvalidOperationException("The queue owner does not belong to this dashboard. Existing work and ownership were preserved.");
                if (request.ReplacementDashboardProcessId is null or <= 4 ||
                    request.ReplacementDashboardProcessStartedAtUtcTicks is null or <= 0 ||
                    request.ReplacementDashboardProcessId == sourceId || request.ReplacementDashboardProcessId == Environment.ProcessId)
                    throw new ArgumentException("The replacement queue dashboard identity is invalid.");
                Process? source = DashboardReplacementPeer.Pin(sourceId, sourceStartedAt);
                Process? target = null;
                try
                {
                    target = DashboardReplacementPeer.Pin(request.ReplacementDashboardProcessId.Value,
                        request.ReplacementDashboardProcessStartedAtUtcTicks.Value);
                    _dashboardHandoff = new(request.HandoffId!, source, sourceStartedAt,
                        target, request.ReplacementDashboardProcessStartedAtUtcTicks.Value);
                    source = null;
                    target = null;
                }
                finally { source?.Dispose(); target?.Dispose(); }
            }

            var pending = _dashboardHandoff;
            if (pending == null || pending.Id != request.HandoffId || pending.Source.Id != sourceId ||
                pending.SourceStartedAt != sourceStartedAt ||
                pending.Target.Id != request.ReplacementDashboardProcessId ||
                pending.TargetStartedAt != request.ReplacementDashboardProcessStartedAtUtcTicks)
                throw new InvalidOperationException("A different queue dashboard handoff is pending. Existing work and ownership were preserved.");
            switch (request.Command)
            {
                case "handoff-prepare":
                    if (!ConfirmedAlive(pending.Source) || !ConfirmedAlive(pending.Target))
                        throw new InvalidOperationException("A queue handoff dashboard is no longer available. Existing work and ownership were preserved.");
                    return new(true, "Queue work retained; new dispatch waits for replacement startup.",
                        pending.Transferred ? "handoff-transferred" : "handoff-prepared");
                case "handoff-transfer":
                    if (!ConfirmedAlive(pending.Source) || !ConfirmedAlive(pending.Target))
                        throw new InvalidOperationException("Queue dashboard transfer could not verify both processes. Existing work and ownership were preserved.");
                    BindDashboard(pending.Target.Id, pending.TargetStartedAt);
                    pending.Transferred = true;
                    return new(true, "Queue owner transferred; active work continues and new dispatch waits for startup.", "handoff-transferred");
                case "handoff-rollback":
                    if (!ConfirmedAlive(pending.Source))
                        throw new InvalidOperationException("The original queue dashboard is no longer available. Existing work remains retained; new dispatch stays paused.");
                    BindDashboard(pending.Source.Id, pending.SourceStartedAt);
                    _dashboardHandoff = null;
                    pending.Dispose();
                    return new(true, "Queue ownership restored to the original dashboard.", "handoff-rolled-back");
                default:
                    throw new ArgumentException("The queue dashboard handoff command is invalid.");
            }
        }
    }

    // Called under the command gate. A failed replacement returns ownership to
    // the still-live source; confirmed loss of both dashboards uses normal shutdown.
    private bool ReconcileDashboardHandoff()
    {
        lock (_dashboardSync)
        {
            var pending = _dashboardHandoff;
            if (pending == null) return false;
            if (!ConfirmedExited(pending.Target)) return true;
            if (ConfirmedAlive(pending.Source))
            {
                BindDashboard(pending.Source.Id, pending.SourceStartedAt);
                _dashboardHandoff = null;
                pending.Dispose();
                return true;
            }
            if (!ConfirmedExited(pending.Source)) return true;
            _dashboardHandoff = null;
            pending.Dispose();
            return false;
        }
    }

    private static bool ConfirmedAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static bool ConfirmedExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private sealed class PendingDashboardHandoff(string id, Process source, long sourceStartedAt, Process target, long targetStartedAt) : IDisposable
    {
        internal string Id { get; } = id;
        internal Process Source { get; } = source;
        internal long SourceStartedAt { get; } = sourceStartedAt;
        internal Process Target { get; } = target;
        internal long TargetStartedAt { get; } = targetStartedAt;
        internal bool Transferred { get; set; }
        public void Dispose() { Source.Dispose(); Target.Dispose(); }
    }
}
