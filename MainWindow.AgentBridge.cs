using FullStackLauncher.AgentBridge;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private AgentBridgeHost? _agentBridge;
    private readonly AgentBridgeCoordination _agentCoordination = new();
    private readonly HashSet<ServiceRunner> _agentLogSubscriptions = [];
    private readonly Dictionary<string, Queue<ServiceLog>> _agentActivity = [];
    private readonly object _agentActivityLock = new();

    internal void StartAgentBridge()
    {
        try
        {
            _agentBridge = new AgentBridgeHost(_store.SettingsPath, HandleAgentBridgeAsync);
            if (!_agentBridge.IsActive)
                Notice = "Agent bridge already belongs to another launcher using this settings file.";
            SubscribeAgentActivity();
            _agentRestartWarningTimer.Tick += (_, _) => CloseAgentRestartWarning();
            _timer.Tick += (_, _) => SubscribeAgentActivity();
            Closed += (_, _) =>
            {
                CloseAgentRestartWarning();
                _agentBridge?.Dispose();
            };
        }
        catch (Exception)
        {
            Notice = "Agent bridge could not start. Dashboard service controls remain available.";
        }
    }

    // Called by dashboard control paths. Events contain identifiers and outcomes only.
    private void RecordAgentBridgeEvent(ServiceViewModel service, string action, string result)
    {
        var projectId = _runners.FirstOrDefault(item => item.Value.Contains(service)).Key;
        if (projectId is not null)
        {
            if (action is "start" or "restart" && result == "completed" &&
                service.Runner.Snapshot.State is ServiceState.Starting or ServiceState.Running)
                _agentCoordination.ResumeAgentStarts(projectId);
            _agentCoordination.Record(projectId, service.Profile.Id, action, result);
        }
    }

    private void RecordAgentBridgeProjectEvent(string projectId, string action, string result,
        bool revokeReservations = false)
    {
        if (revokeReservations) _agentCoordination.Override(projectId);
        _agentCoordination.Record(projectId, null, action, result);
    }

    private void SubscribeAgentActivity()
    {
        foreach (var (projectId, services) in _runners)
        foreach (var service in services)
        {
            if (!_agentLogSubscriptions.Add(service.Runner)) continue;
            service.Runner.LogReceived += log =>
            {
                // Never forward raw stdout/stderr or the configured command. Launcher-generated
                // lifecycle text has already passed the runner's credential filter.
                if (log.Kind is not (ServiceLogKind.Information or ServiceLogKind.Success
                    or ServiceLogKind.Warning or ServiceLogKind.Error)) return;
                var message = SensitiveDataProtection.Redact(log.Message);
                var safe = log with
                {
                    Message = message.Length > 500 ? message[..500] + " … [truncated]" : message
                };
                lock (_agentActivityLock)
                {
                    var key = projectId + ":" + log.ServiceId;
                    if (!_agentActivity.TryGetValue(key, out var lines))
                        _agentActivity.Add(key, lines = new Queue<ServiceLog>());
                    lines.Enqueue(safe);
                    while (lines.Count > 100) lines.Dequeue();
                }
            };
        }
    }

    private async Task<AgentBridgeResponse> HandleAgentBridgeAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.InvokeAsync(() => HandleAgentBridgeOnDispatcherAsync(request, cancellationToken))
                .Task.Unwrap();
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            return AgentBridgeResponse.Failure("Launcher bridge request timed out.");
        }
    }

    private async Task<AgentBridgeResponse> HandleAgentBridgeOnDispatcherAsync(AgentBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_closing || _closed || _closeRequested)
            return AgentBridgeResponse.Failure("Launcher dashboard is closing. Reconnect after it opens again.");
        SubscribeAgentActivity();
        try
        {
            return request.Action switch
            {
                "projects" => AgentBridgeResponse.Success(new
                {
                    instanceId = _agentCoordination.InstanceId,
                    projects = _settings.Projects.Select(project => new
                    {
                        project.Id, project.Name, rootPath = _store.ResolveRoot(project),
                        project.IsArchived, serviceCount = project.Services.Count,
                        reservation = _agentCoordination.ReservationStatus(project.Id)
                    }).ToArray()
                }),
                "services" => AgentBridgeResponse.Success(ListAgentServices(request.ProjectId)),
                "git_connections" => AgentBridgeResponse.Success(await ListAgentGitConnectionsAsync(request, cancellationToken)),
                "record_git_changes" => AgentBridgeResponse.Success(await RecordAgentGitChangesAsync(request, cancellationToken)),
                "service_status" => AgentBridgeResponse.Success(await AgentServiceStatusAsync(request)),
                "recent_activity" => AgentBridgeResponse.Success(AgentRecentActivity(request)),
                "reservation_status" => AgentBridgeResponse.Success(_agentCoordination.ReservationStatus(RequireProject(request.ProjectId).Id)),
                "register_project" => AgentBridgeResponse.Success(_agentCoordination.Register(
                    RequireProject(request.ProjectId).Id, request.Owner ?? "")),
                "declare_service_use" => AgentBridgeResponse.Success(DeclareAgentServiceUse(request)),
                "release_service_use" => AgentBridgeResponse.Success(ReleaseAgentServiceUse(request)),
                "heartbeat_project" => AgentBridgeResponse.Success(_agentCoordination.Heartbeat(
                    RequireProject(request.ProjectId).Id, request.SessionToken ?? "")),
                "unregister_project" => AgentBridgeResponse.Success(_agentCoordination.Unregister(
                    RequireProject(request.ProjectId).Id, request.SessionToken ?? "")),
                "project_events" => AgentBridgeResponse.Success(_agentCoordination.Events(
                    RequireProject(request.ProjectId).Id, request.SessionToken ?? "", request.SinceSequence ?? 0)),
                "claim_project" => AgentBridgeResponse.Success(ClaimAgentProject(request)),
                "wait_project" => AgentBridgeResponse.Success(await WaitForAgentProjectAsync(request, cancellationToken)),
                "cancel_wait" => AgentBridgeResponse.Success(_agentCoordination.CancelWait(
                    RequireProject(request.ProjectId).Id, request.SessionToken ?? "", request.Ticket ?? "")),
                "release_project" => AgentBridgeResponse.Success(_agentCoordination.Release(
                    RequireProject(request.ProjectId).Id, request.SessionToken ?? "", request.LeaseToken ?? "")),
                "service_action" => AgentBridgeResponse.Success(await AgentServiceActionAsync(request, cancellationToken)),
                _ => AgentBridgeResponse.Failure("Unknown launcher bridge action.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            return AgentBridgeResponse.Failure(SensitiveDataProtection.Redact(ex.Message));
        }
        catch (Exception)
        {
            return AgentBridgeResponse.Failure("Launcher bridge could not complete this request.");
        }
    }

    private ProjectProfile RequireProject(string? id) =>
        _settings.Projects.FirstOrDefault(project => project.Id == id)
        ?? throw new ArgumentException("Choose a configured project ID from launcher_projects.");

    private ServiceViewModel RequireService(ProjectProfile project, string? id) =>
        _runners.GetValueOrDefault(project.Id)?.FirstOrDefault(service => service.Profile.Id == id)
        ?? throw new ArgumentException("Choose a configured service ID from launcher_services.");

    private object DeclareAgentServiceUse(AgentBridgeRequest request)
    {
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        return _agentCoordination.DeclareServiceUse(project.Id, service.Profile.Id,
            request.SessionToken ?? "");
    }

    private object ReleaseAgentServiceUse(AgentBridgeRequest request)
    {
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        return _agentCoordination.ReleaseServiceUse(project.Id, service.Profile.Id,
            request.SessionToken ?? "");
    }

    private object ClaimAgentProject(AgentBridgeRequest request)
    {
        var project = RequireProject(request.ProjectId);
        var serviceId = request.ServiceId is null ? null : RequireService(project, request.ServiceId).Profile.Id;
        return _agentCoordination.Claim(project.Id, request.SessionToken ?? "",
            request.Purpose ?? "", serviceId);
    }

    private object ListAgentServices(string? projectId)
    {
        List<ProjectProfile> projects = projectId is null ? _settings.Projects : [RequireProject(projectId)];
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            observedUtc = DateTime.UtcNow,
            lastDashboardCheck = LastChecked,
            services = projects.SelectMany(project =>
                (_runners.GetValueOrDefault(project.Id) ?? []).Select(service => AgentServiceData(project, service))).ToArray()
        };
    }

    private object AgentServiceData(ProjectProfile project, ServiceViewModel service)
    {
        var snapshot = service.Runner.Snapshot;
        return new
        {
            projectId = project.Id, projectName = project.Name,
            serviceId = service.Profile.Id, serviceName = service.Name,
            kind = service.Profile.Kind, isConsole = service.Profile.IsConsole,
            state = snapshot.State.ToString(),
            detail = SensitiveDataProtection.Redact(snapshot.Detail),
            processIds = snapshot.ProcessIds,
            isManaged = snapshot.IsManaged,
            activeUrl = service.Profile.IsConsole ? null : AgentLoopbackOrigin(snapshot.ActiveUrl),
            configuredUrl = service.Profile.IsConsole ? null : AgentLoopbackOrigin(service.Profile.Url),
            configuredPort = ServicePortConfiguration.GetPort(service.Profile),
            selectedApiConfiguration = service.Profile.ApiConfiguration?.Environment,
            appliedApiConfiguration = service.Runner.AppliedConfigurationEnvironment,
            configurationNeedsRestart = service.Runner.ConfigurationNeedsRestart,
            reservation = _agentCoordination.ReservationStatus(project.Id),
            agentActivity = _agentCoordination.ServiceActivity(project.Id, service.Profile.Id)
        };
    }

    private static string? AgentLoopbackOrigin(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Authority) : null;

    private async Task<object> AgentServiceStatusAsync(AgentBridgeRequest request)
    {
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        await service.Runner.RefreshAsync();
        service.Update();
        return new { instanceId = _agentCoordination.InstanceId, checkedUtc = DateTime.UtcNow,
            service = AgentServiceData(project, service) };
    }

    private object AgentRecentActivity(AgentBridgeRequest request)
    {
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        var limit = Math.Clamp(request.Limit ?? 20, 1, 50);
        ServiceLog[] lines;
        lock (_agentActivityLock)
            lines = _agentActivity.TryGetValue(project.Id + ":" + service.Profile.Id, out var history)
                ? history.TakeLast(limit).ToArray() : [];
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = project.Id, serviceId = service.Profile.Id,
            scope = "launcher lifecycle only; raw application output is excluded",
            entries = lines.Select(line => new { timestamp = line.Timestamp, kind = line.Kind?.ToString(), line.Message }).ToArray()
        };
    }

    private async Task<object> WaitForAgentProjectAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        var project = RequireProject(request.ProjectId);
        var sessionToken = request.SessionToken ?? "";
        var ticket = request.Ticket ?? "";
        var seconds = Math.Clamp(request.WaitSeconds ?? 15, 0, 20);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        object status;
        do
        {
            status = _agentCoordination.CheckWait(project.Id, sessionToken, ticket);
            if (_agentCoordination.IsTicketAcquired(project.Id, sessionToken, ticket) || DateTime.UtcNow >= until) break;
            await Task.Delay(250, cancellationToken);
        } while (true);
        return status;
    }

    private async Task<object> AgentServiceActionAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        var sessionToken = request.SessionToken ?? "";
        var operation = request.Operation;
        if (operation is not ("start" or "stop" or "restart"))
            throw new ArgumentException("Operation must be start, stop, or restart.");
        _agentCoordination.RequireLease(project.Id, sessionToken, request.LeaseToken ?? "", service.Profile.Id);
        if (_batchBusy || _forceStopBatchBusy || _savingProjectEdits || IsEditing || service.IsBusy || service.IsStopping)
            throw new InvalidOperationException("Dashboard service controls are busy. Retry after they finish.");

        service.IsBusy = true;
        UpdateActions();
        var attempted = false;
        var dashboardActionRevision = _agentCoordination.DashboardActionRevision(project.Id);
        try
        {
            await service.Runner.RefreshAsync();
            service.Update();
            if (_savingProjectEdits || IsEditing || _batchBusy || _forceStopBatchBusy ||
                service.IsStopping || _agentCoordination.DashboardActionRevision(project.Id) != dashboardActionRevision)
                throw new InvalidOperationException("Dashboard editing or a service action began during inspection. Check fresh status before another agent action.");
            var before = service.Runner.Snapshot;
            if (before.State == ServiceState.Conflict)
                throw new InvalidOperationException("Service has a port conflict. Review the exact process in the dashboard.");
            if (operation is "start" or "restart" && _agentCoordination.AreAgentStartsSuspended(project.Id))
                throw new InvalidOperationException("Dashboard Force stop all suspended agent starts. A user must start a service in the dashboard before agent starts resume.");
            if (operation is "start" or "restart" &&
                (service.Profile.ApiConfiguration?.Environment == "Prod" ||
                 service.Runner.AppliedConfigurationEnvironment == "Prod"))
                throw new InvalidOperationException("Production API start and restart require dashboard review.");

            if (operation == "start" && (before.ProcessIds.Count > 0 || service.Runner.HasManagedProcess))
                return new { outcome = "existing_process_reused", checkedUtc = DateTime.UtcNow,
                    service = AgentServiceData(project, service) };
            if (operation == "stop" && before.ProcessIds.Count == 0 && !service.Runner.HasManagedProcess)
                return new { outcome = "already_stopped", checkedUtc = DateTime.UtcNow,
                    service = AgentServiceData(project, service) };

            // Refresh yields to dashboard controls. Human Force stop all may revoke the
            // reservation while this request waits for an inspection.
            if (_closeRequested || _closing || _forceStopBatchBusy)
                throw new InvalidOperationException("Dashboard is closing or Force stop all is active. No agent service action started.");
            _agentCoordination.RequireLease(project.Id, sessionToken, request.LeaseToken ?? "", service.Profile.Id);
            _agentCoordination.BeginServiceAction(project.Id, sessionToken, service.Profile.Id);
            _agentCoordination.Record(project.Id, service.Profile.Id, "agent_" + operation, "requested");
            attempted = true;
            if (operation == "start") await service.Runner.StartAsync();
            else if (operation == "restart")
            {
                var restartInitiated = false;
                var replacementLaunchApproved = false;
                void RequireAgentRestartAllowed()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_closeRequested || _closing || _closed || _forceStopBatchBusy || _batchBusy ||
                        _savingProjectEdits || IsEditing || service.IsStopping ||
                        _agentCoordination.DashboardActionRevision(project.Id) != dashboardActionRevision)
                        throw new InvalidOperationException("Dashboard controls changed during restart. No further agent restart action permitted.");
                    _agentCoordination.RequireLease(project.Id, sessionToken, request.LeaseToken ?? "", service.Profile.Id);
                    if (_agentCoordination.AreAgentStartsSuspended(project.Id))
                        throw new InvalidOperationException("Dashboard Force stop all suspended agent starts. A user must start a service in the dashboard before agent starts resume.");
                    if (service.Profile.ApiConfiguration?.Environment == "Prod" ||
                        service.Runner.AppliedConfigurationEnvironment == "Prod")
                        throw new InvalidOperationException("Production API start and restart require dashboard review.");
                }
                await service.Runner.RestartAsync(() =>
                {
                    RequireAgentRestartAllowed();
                    _agentCoordination.NotifyAgentRestart(project.Id, sessionToken, service.Profile.Id);
                    ShowAgentRestartWarning();
                    restartInitiated = true;
                }, () =>
                {
                    RequireAgentRestartAllowed();
                    replacementLaunchApproved = true;
                });
                // The runner records preparation failures instead of rethrowing them.
                // Never report an untouched surviving process as a completed restart.
                if (!restartInitiated)
                    throw new InvalidOperationException("Agent restart did not begin. Check service status and launcher output.");
                if (!replacementLaunchApproved || service.Runner.Snapshot.State == ServiceState.Error)
                    throw new InvalidOperationException("Agent restart did not start a replacement process. Check service status and launcher output.");
            }
            else await service.Runner.ForceStopAsync();

            // A launch can return before HTTP is ready. Report observed health, never a
            // speculative success. Agent can call launcher_service_status later.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(operation == "stop" ? 5 : 8);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                await service.Runner.RefreshAsync();
                service.Update();
                var current = service.Runner.Snapshot;
                if (current.State is ServiceState.Running or ServiceState.Stopped or ServiceState.Completed
                    or ServiceState.Error or ServiceState.Conflict || DateTime.UtcNow >= until) break;
                await Task.Delay(400, cancellationToken);
            } while (true);
            var after = service.Runner.Snapshot;
            var outcome = operation == "stop"
                ? after.State is ServiceState.Stopped or ServiceState.Completed &&
                  after.ProcessIds.Count == 0 && !service.Runner.HasManagedProcess ? "stopped" : "needs_attention"
                : after.State == ServiceState.Running ? "running"
                    : after.State == ServiceState.Starting ? "starting" : "needs_attention";
            _agentCoordination.Record(project.Id, service.Profile.Id, "agent_" + operation,
                outcome == "needs_attention" ? "failed" : "completed");
            attempted = false;
            _agentCoordination.EndServiceAction(project.Id, sessionToken, service.Profile.Id);
            return new { outcome, checkedUtc = DateTime.UtcNow, service = AgentServiceData(project, service) };
        }
        catch
        {
            if (attempted) _agentCoordination.Record(project.Id, service.Profile.Id, "agent_" + operation, "failed");
            throw;
        }
        finally
        {
            _agentCoordination.EndServiceAction(project.Id, sessionToken, service.Profile.Id);
            service.IsBusy = false;
            UpdateActions();
        }
    }
}
