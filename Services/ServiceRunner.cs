using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FullStackLauncher.Models;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>
/// Runs explicitly configured local commands. Stop shutdown awaits StopManagedAsync before Dispose.
/// Keep-running shutdown prepares retained identities and output, then disposes without termination. Externally launched services
/// are stopped only through explicit stop, restart, or confirmed conflict recovery.
/// </summary>
public sealed class ServiceRunner : IDisposable
{
    // Every saved service has a runner, including unselected and archived projects. Register
    // all folders before commands start so overlapping services cannot adopt each other's processes.
    private static readonly ConcurrentDictionary<Guid, string> ServiceFolders = new();
    private readonly Guid _runnerId = Guid.NewGuid();
    private static readonly Regex Ansi = new(@"\x1B(?:\][^\x07]*(?:\x07|\x1B\\)|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);
    private static readonly Regex StartupUrl = new(@"(?:Now listening on:|\bLocal:|(?:listening|running|started|ready)\s+(?:at|on)\s*:?|open your browser on|is being served at|Waiting for connection from Dart debug extension at)\s*(?<url>https?://[^\s<>""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FrontendBuildSucceeded = new(
        @"\b(?:application bundle generation complete|compiled successfully|successfully compiled|compiled with warnings|build succeeded)\b|\bVITE\s+v\S+\s+ready in\s+\d|^\s*(?:[✓✔√]\s*)?(?:Ready in\s+\d|Compiled\s+.+\s+in\s+\d)",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FrontendBuildFailed = new(
        @"\b(?:failed to compile|(?:build|compilation|generation) failed|fatal|unhandled exception|EADDRINUSE)\b|(?:^|\s|\[)\s*(?:error|err|fail)(?:\s*[:!\]]|\s+[a-z]*\d+\b)|\bERROR\s+in\b|\[(?:ERROR|FAIL|FAILED)\]",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FrontendBuildStarted = new(
        @"\b(?:changes detected\.?\s+rebuilding|generating browser application bundles)\b|^\s*(?:[○◌]\s*)?Compiling\b",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        // Only loopback URLs ever reach this client. Local development certificates may be untrusted.
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            request.RequestUri?.IsLoopback == true || errors == System.Net.Security.SslPolicyErrors.None
    }) { Timeout = TimeSpan.FromSeconds(1.2) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, ProcessIdentity> _owned = new();
    private readonly ConcurrentDictionary<int, byte> _expectedExits = new();
    private readonly Channel<ServiceLog> _logs = Channel.CreateBounded<ServiceLog>(new BoundedChannelOptions(500)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly object _consoleLogSync = new();
    private readonly object _swaggerUiSync = new();
    private SwaggerUiStatus _swaggerStatus = new(SwaggerUiAvailability.ApiUnavailable, "API is not running and ready yet.");
    private CancellationTokenSource? _swaggerCancellation;
    private string? _swaggerProbeKey;
    private long _swaggerProbeVersion;
    private bool _swaggerProbeInFlight;
    private DateTime _swaggerNextCheckUtc;
    private long _consoleSequence;
    private long _consoleResetSequence;
    private long _outputGeneration;
    private bool _repeatDiagnosticAfterConsoleReset;
    private Process? _startProcess;
    private Process? _maintenanceProcess;
    private string? _maintenanceLabel;
    private ServiceOutputCapture? _startOutputCapture;
    private ServiceOutputCapture? _maintenanceOutputCapture;
    private ConsoleProcessSession? _consoleSession;
    private ConsoleProcessSession? _maintenanceSession;
    private readonly Dictionary<string, SafeFileHandle> _retainedJobs = [];
    private readonly HashSet<ProcessIdentity> _recoveredRoots = [];
    private bool _runtimeLoaded;
    private volatile bool _preparingKeepRunning;
    private bool _keepRunningPrepared;
    private bool _consoleWasStopped;
    private CancellationTokenSource? _operationCancellation;
    private ServiceSnapshot _snapshot = new(ServiceState.Checking, "Waiting for the first process and endpoint check", []);
    private string? _detectedUrl;
    private string? _lastError;
    private volatile bool _intentionalStop;
    private volatile bool _disposed;
    private volatile bool _hasManagedProcess;
    private volatile bool _hasVerifiedNoServiceProcesses;
    private int _stopRevision;
    private ApiLaunchConfiguration? _launchConfiguration;
    private bool _apiProcessLaunched;
    private long _managedApiRunVersion;
    // Build output and HTTP probes must belong to one explicitly started frontend run.
    // Console clearing and hot reloads do not create another browser-open opportunity.
    private long _managedFrontendRunVersion;
    private long _frontendBuildRevision;
    private int _frontendRunStopRevision;
    private bool _frontendRunActive;
    private bool _frontendBuildSuccessful;
    private bool _frontendReadyEmitted;
    private long _managedFlutterRunVersion;
    private bool _flutterRunActive;
    private bool _flutterWebBuildSuccessful;
    private bool _flutterBuildFailed;
    private string _flutterStartupDetail = "Starting Flutter; waiting for the app window";
    private readonly HashSet<ProcessIdentity> _readyFlutterApps = [];
    private ProcessIdentity[] _flutterAppProcessIdentities = [];
    public string? AppliedConfigurationEnvironment => HasManagedProcess && _apiProcessLaunched ? _launchConfiguration?.Environment : null;
    public string? AppliedDatabaseIdentifier => HasManagedProcess && _apiProcessLaunched ? _launchConfiguration?.DatabaseIdentifier : null;
    public long ManagedApiRunVersion => Interlocked.Read(ref _managedApiRunVersion);
    internal int StopRevision => Volatile.Read(ref _stopRevision);
    public bool ConfigurationNeedsRestart { get; set; }

    public ServiceRunner(ServiceProfile profile, string workingDirectory)
    {
        Profile = profile;
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        ServiceFolders[_runnerId] = WorkingDirectory;
        if (profile.IsConsole) _snapshot = new(ServiceState.Checking, "Waiting for the first process check", []);
        _ = PumpLogsAsync();
    }

    public event Action<ServiceLog>? LogReceived;
    internal event Action<string, long>? FrontendReady;
    internal event Action? ConsoleOutputReset;
    internal event Action? SnapshotChanged;
    internal long ConsoleResetSequence => Interlocked.Read(ref _consoleResetSequence);
    internal long ManagedFrontendRunVersion => Interlocked.Read(ref _managedFrontendRunVersion);
    public ServiceProfile Profile { get; }
    public string WorkingDirectory { get; }
    public ServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool HasManagedProcess => _hasManagedProcess;
    public bool UsesManagedProcessTrackingOnly => Profile.IsGenericConsole || Profile.IsCommandApi || ServiceFolders.Any(folder =>
        folder.Key != _runnerId && SettingsStore.WorkingFoldersOverlap(WorkingDirectory, folder.Value));
    public bool HasVerifiedNoServiceProcesses => _hasVerifiedNoServiceProcesses;
    internal IReadOnlyList<ProcessIdentity> FlutterAppProcessIdentities => Volatile.Read(ref _flutterAppProcessIdentities);
    internal SwaggerUiStatus SwaggerStatus => Volatile.Read(ref _swaggerStatus);
    internal bool IsSwaggerUiLaunch => !Profile.IsConsole && SwaggerUiProbe.IsSwaggerUiUri(BuildUiUrl());

    internal bool CanOpenReadyFrontend(long runVersion)
    {
        lock (_consoleLogSync)
            return !_disposed && !_intentionalStop && _frontendRunActive && _frontendReadyEmitted &&
                _frontendBuildSuccessful && _lastError is null && runVersion == _managedFrontendRunVersion &&
                _frontendRunStopRevision == Volatile.Read(ref _stopRevision) &&
                FrontendServiceSupport.IsFrontend(Profile) && HasManagedProcess &&
                Snapshot.State == ServiceState.Running;
    }

    public Task RefreshAsync() => WithGateAsync(() => RefreshCoreAsync());
    public Task RefreshForProfileEditAsync() => WithGateAsync(async () =>
        await RefreshCoreAsync(await InspectAsync(fresh: true)));
    public Task StartAsync() => WithGateAsync(StartCoreAsync);
    public Task CleanAsync()
    {
        var revision = Volatile.Read(ref _stopRevision);
        return WithGateAsync(() => RunMaintenanceAsync(Profile.CleanCommand, "Clean", revision));
    }
    public Task SetupAsync()
    {
        var revision = Volatile.Read(ref _stopRevision);
        return WithGateAsync(() => RunMaintenanceAsync(Profile.SetupCommand, "Setup", revision));
    }

    public async Task RestartAsync(Action? beforeRestart = null, Action? beforeStart = null)
    {
        CancelMaintenance();
        var revision = Volatile.Read(ref _stopRevision);
        await WithGateAsync(async () =>
        {
            var configuration = await Task.Run(PrepareConfiguration);
            if (configuration is null) await Task.Run(() => ValidateCommand(Profile.StartCommand, "Start"));
            // Agent callbacks recheck their lease after asynchronous preparation and notify
            // immediately before the disruptive operation, never for rejected preflight.
            RequireCurrentRestart();
            beforeRestart?.Invoke();
            if (await StopAndClearConsoleAsync()) await StartCoreAsync(configuration, () =>
            {
                RequireCurrentRestart();
                beforeStart?.Invoke();
            });
        });

        void RequireCurrentRestart()
        {
            if (revision != Volatile.Read(ref _stopRevision))
                throw new InvalidOperationException("Restart canceled by a subsequent service stop.");
        }
    }

    public Task ApplyConfigurationAsync(ApiLaunchConfiguration configuration)
    {
        CancelMaintenance();
        var revision = Volatile.Read(ref _stopRevision);
        return WithGateAsync(async () =>
        {
            // The UI preflights and saves the selection before entering the process operation.
            RequireCurrentRestart();
            if (await StopAndClearConsoleAsync()) await StartCoreAsync(configuration, RequireCurrentRestart);
        });

        void RequireCurrentRestart()
        {
            if (revision != Volatile.Read(ref _stopRevision))
                throw new InvalidOperationException("Restart canceled by a subsequent service stop.");
        }
    }

    private ApiLaunchConfiguration? PrepareConfiguration() => !Profile.UsesProcessSession && Profile.ApiConfiguration is { } selection
        ? ApiLaunchConfiguration.Prepare(Profile, WorkingDirectory, selection.Environment) : null;

    public async Task ForceStopAsync(Func<string, bool>? confirmConflict = null)
    {
        CancelMaintenance();
        await WithGateAsync(async () =>
        {
            ConflictApproval? approval = null;
            if (confirmConflict is not null)
            {
                var inspection = await InspectAsync(fresh: true);
                if (inspection.Inventory.InspectionError is not null)
                    throw new InvalidOperationException(inspection.Inventory.InspectionError);
                await RefreshCoreAsync(inspection);
                if (Snapshot.State == ServiceState.Conflict)
                {
                    approval = ConfirmConflict(inspection, confirmConflict, restart: false);
                    if (approval is null) return;
                }
            }
            await StopAndClearConsoleAsync(approval);
        });
    }

    internal Task ResolveConflictAsync(Func<string, bool> confirm)
    {
        var revision = Volatile.Read(ref _stopRevision);
        return WithGateAsync(async () =>
        {
            if (revision != Volatile.Read(ref _stopRevision)) return;
            await Task.Run(() => ValidateCommand(Profile.StartCommand, "Start"));
            var configuration = await Task.Run(PrepareConfiguration);
            var inspection = await InspectAsync(fresh: true);
            if (inspection.Inventory.InspectionError is not null)
                throw new InvalidOperationException(inspection.Inventory.InspectionError);
            await RefreshCoreAsync(inspection);
            if (Snapshot.State != ServiceState.Conflict) return;

            var approval = ConfirmConflict(inspection, confirm, restart: true);
            if (approval is null) return;
            if (revision != Volatile.Read(ref _stopRevision)) return;
            if (await StopAndClearConsoleAsync(approval)
                && revision == Volatile.Read(ref _stopRevision))
                await StartCoreAsync(configuration);
        });
    }

    private sealed record ConflictApproval(int Port, IReadOnlyList<InspectedProcess> Targets);

    private ConflictApproval? ConfirmConflict(Inspection inspection, Func<string, bool> confirm, bool restart)
    {
        var endpoint = GetEndpoint(inspection)!;
        var serviceIds = inspection.All.Select(p => p.Id).ToHashSet();
        var blockers = inspection.Inventory.ListeningPorts
            .Where(p => p.Port == endpoint.Port && !serviceIds.Contains(p.ProcessId))
            .Select(p => p.ProcessId).Distinct().ToArray();
        var protectedIds = ProcessInspector.Ancestors(inspection.Inventory.Processes);
        var roots = new List<ProcessIdentity>();
        foreach (var id in blockers)
        {
            if (ProcessInspector.IsDefinitelyStopped(id)) continue;
            var process = inspection.Inventory.Processes.FirstOrDefault(p => p.Id == id);
            if (id <= 4 || protectedIds.Contains(id) || process is null || !ProcessInspector.IsSameProcess(process.Identity))
                throw new InvalidOperationException($"Cannot safely stop the process using port {endpoint.Port} (PID {id}). Stop it in its original application or check Windows process permissions, then retry.");
            roots.Add(process.Identity);
        }
        var targets = ProcessInspector.Descendants(inspection.Inventory.Processes, roots);
        if (targets.Any(p => p.Id <= 4 || protectedIds.Contains(p.Id)))
            throw new InvalidOperationException("The conflicting process tree includes the launcher or its parent. Stop the service in its original application.");
        if (targets.Count == 0)
        {
            Log($"The process blocking port {endpoint.Port} has already exited.");
            return new(endpoint.Port, []);
        }

        var processList = string.Join(Environment.NewLine, targets.OrderBy(p => p.Id)
            .Select(p => $"• {p.Name} (PID {p.Id})"));
        var action = restart ? $"Resolve the conflict on port {endpoint.Port} and restart {Profile.Name}?"
            : $"Force stop {Profile.Name} and clear its port {endpoint.Port} conflict?";
        if (!confirm(action + $"\n\nThese processes will be force stopped:\n{processList}\n\n" +
            "They could belong to another application; unsaved work in them may be lost. " +
            (restart ? "This service's matching processes will also be stopped, then its start command will run."
                : "This service's matching processes will also be stopped. The service will stay stopped.")))
        {
            Log(restart ? "Conflict recovery canceled." : "Force stop canceled.");
            return null;
        }
        return new(endpoint.Port, targets);
    }

    public async Task StopManagedAsync()
    {
        if (_disposed) return;
        CancelMaintenance();
        var succeeded = false;
        await WithGateAsync(async () => { succeeded = await StopCoreAsync(includeExternal: false); });
        if (!succeeded || HasManagedProcess)
            throw new InvalidOperationException(_lastError ?? "Launcher-owned processes could not be stopped. Keep the launcher open and try again.");
    }

    public async Task PrepareKeepRunningAsync()
    {
        if (_disposed) return;
        _preparingKeepRunning = true;
        // Maintenance normally owns the gate until completion. Relinquish only its wait;
        // its process, child job and output stay alive for this explicit keep-running choice.
        try { Volatile.Read(ref _operationCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            var inspection = await InspectAsync(fresh: true);
            if (inspection.Inventory.InspectionError is not null)
                throw new InvalidOperationException(inspection.Inventory.InspectionError + " Services were not detached. Keep the launcher open and try again.");
            await Task.Run(() =>
            {
                _startOutputCapture?.PrepareKeepRunning();
                _maintenanceOutputCapture?.PrepareKeepRunning();
                _consoleSession?.PrepareKeepRunning();
                _maintenanceSession?.PrepareKeepRunning();
            });
            // Runtime staging can take time. Capture the latest children before committing recovery data.
            inspection = await InspectAsync(fresh: true);
            if (inspection.Inventory.InspectionError is not null)
                throw new InvalidOperationException(inspection.Inventory.InspectionError + " Services were not detached. Keep the launcher open and try again.");
            await Task.Run(() =>
            {
                var jobs = _retainedJobs.Keys.Concat(new[] { _consoleSession?.JobName, _maintenanceSession?.JobName }.OfType<string>());
                ServiceRuntimeStore.Save(Profile.Id, WorkingDirectory, inspection.Owned.Select(p => p.Identity), jobs);
            });
            _keepRunningPrepared = true;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelKeepRunningPreparationAsync()
    {
        if (_disposed || (!_preparingKeepRunning && !_keepRunningPrepared)) return;
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                // Attempt every rollback even when Windows rejects one handle operation.
                var failures = new List<Exception>();
                foreach (var cancel in new Action[] {
                    () => _startOutputCapture?.CancelKeepRunningPreparation(),
                    () => _maintenanceOutputCapture?.CancelKeepRunningPreparation(),
                    () => _consoleSession?.CancelKeepRunningPreparation(),
                    () => _maintenanceSession?.CancelKeepRunningPreparation() })
                    try { cancel(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { failures.Add(ex); }
                if (failures.Count > 0) throw new InvalidOperationException("Services remain running, but output retention could not be fully reset. Keep the launcher open and try closing again.", failures[0]);
            });
        }
        finally
        {
            _preparingKeepRunning = false;
            _keepRunningPrepared = false;
            _gate.Release();
        }
    }

    public void DisposeKeepingServicesRunning()
    {
        if (_disposed) return;
        // Final disposal runs off the dispatcher. An operation's pending final refresh may
        // still own the gate after close preparation, so never release native handles concurrently.
        _gate.Wait();
        try
        {
            if (_disposed) return;
            if (!_keepRunningPrepared) throw new InvalidOperationException("Services must be prepared before leaving them running.");
            _disposed = true;
            _intentionalStop = true;
            ResetSwaggerUiStatus(ServiceState.Stopped);
            InvalidateFrontendRun();
            _consoleSession?.DisposeKeepingRunning();
            _maintenanceSession?.DisposeKeepingRunning();
            _startOutputCapture?.DisposeKeepingRunning();
            _maintenanceOutputCapture?.DisposeKeepingRunning();
            foreach (var job in _retainedJobs.Values) job.Dispose();
            _retainedJobs.Clear();
            _owned.Clear();
            _startProcess?.Dispose();
            _maintenanceProcess?.Dispose();
            _logs.Writer.TryComplete();
            ServiceFolders.TryRemove(_runnerId, out _);
        }
        finally { _gate.Release(); }
    }

    private async Task WithGateAsync(Func<Task> operation)
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (!_disposed) await operation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastError = SensitiveDataProtection.Redact(_launchConfiguration?.Redact(ex.Message) ?? ex.Message);
            Publish(ServiceState.Error, _lastError, Snapshot.ProcessIds, Snapshot.ActiveUrl);
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync() => await StartCoreAsync(await Task.Run(PrepareConfiguration));

    private async Task StartCoreAsync(ApiLaunchConfiguration? configuration, Action? beforeStart = null)
    {
        if (Profile.UsesProcessSession) configuration = null;
        if (!Profile.IsConsole && NormalizeLocalUri(Profile.Url) is null)
            throw new InvalidOperationException("Set a valid local HTTP or HTTPS URL in project settings.");
        var inspection = await InspectAsync(fresh: true);
        if (inspection.Inventory.InspectionError is not null)
            throw new InvalidOperationException(inspection.Inventory.InspectionError + " Start was skipped because ownership could not be checked.");
        await RefreshCoreAsync(inspection);
        if (inspection.All.Count > 0)
        {
            Log("A process already belongs to this service. Start skipped to avoid a duplicate.");
            return;
        }
        if (Snapshot.State == ServiceState.Conflict)
        {
            Log(Snapshot.Detail + " Start skipped.", true);
            return;
        }
        if (configuration is null) await Task.Run(() => ValidateCommand(Profile.StartCommand, "Start"));
        var previousSession = _consoleSession;
        _consoleSession = null;
        _lastError = null;
        _consoleWasStopped = false;
        Volatile.Write(ref _detectedUrl, null);
        _intentionalStop = false;
        if (previousSession is not null)
        {
            await DrainConsoleOutputAsync(previousSession);
            await Task.Run(previousSession.Dispose);
        }
        _startProcess?.Dispose();
        _startOutputCapture?.Dispose();
        _startOutputCapture = null;
        // Inspection and output draining yield. Respect a stop or dashboard override
        // that arrived during those awaits before launching another process.
        beforeStart?.Invoke();
        _startProcess = await Task.Run(() => StartCommand(Profile.StartCommand, detectUrl: true, configuration));
        ConfigurationNeedsRestart = false;
        Publish(ServiceState.Starting, FlutterRuntimeStatus.IsNative(Profile) ? "Starting Flutter; waiting for the app window"
            : Profile.IsConsole ? "Starting console command" : "Starting; waiting for the local HTTP endpoint",
            [_startProcess.Id], BuildUiUrl());
        // Capture early descendants immediately; subsequent refreshes keep identities even if the shell exits.
        var started = await InspectAsync(fresh: true);
        if (Profile.IsConsole) await RefreshCoreAsync(started);
    }

    private async Task RunMaintenanceAsync(string command, string label, int stopRevision)
    {
        if (stopRevision != Volatile.Read(ref _stopRevision)) return;
        await Task.Run(() => ValidateCommand(command, label));
        var inspection = await InspectAsync(fresh: true);
        if (inspection.Inventory.InspectionError is not null)
            throw new InvalidOperationException(inspection.Inventory.InspectionError + $" {label} was skipped.");
        await RefreshCoreAsync(inspection);
        if (inspection.All.Count > 0 || Snapshot.State == ServiceState.Conflict)
        {
            Log($"Stop this service before running {label.ToLowerInvariant()}.", true);
            return;
        }
        _lastError = null;
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref _operationCancellation, cancellation);
        if (_preparingKeepRunning || stopRevision != Volatile.Read(ref _stopRevision)) cancellation.Cancel();
        if (cancellation.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            return;
        }
        var process = await Task.Run(() => StartCommand(command, detectUrl: false));
        _maintenanceProcess = process;
        _maintenanceLabel = label;
        var maintenanceSession = _maintenanceSession;
        var retainedForExit = false;
        Publish(ServiceState.Busy, $"{label} in progress", [process.Id], BuildUiUrl());
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            if (maintenanceSession is not null)
                while ((await Task.Run(maintenanceSession.ReadProcesses)).Count > 0)
                    await Task.Delay(150, cancellation.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{label} exited with code {process.ExitCode}. See the output log.");
            Log($"{label} completed successfully.", kind: ServiceLogKind.Success);
        }
        catch (OperationCanceledException)
        {
            retainedForExit = _preparingKeepRunning;
            if (retainedForExit) Log($"{label} remains running while the launcher prepares to close.");
            else
            {
                if (maintenanceSession is not null) await Task.Run(maintenanceSession.Stop);
                await KillProcessesAsync(ProcessInspector.Descendants((await ProcessInspector.ReadAsync(true, includePorts: !Profile.IsConsole)).Processes,
                    [new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks)]));
                Log($"{label} stopped.");
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            try
            {
                if (!retainedForExit && maintenanceSession is not null)
                {
                    // A canceled/failed maintenance command must not leave descendants or output readers behind.
                    try
                    {
                        await Task.Run(maintenanceSession.Stop);
                        await DrainConsoleOutputAsync(maintenanceSession);
                    }
                    finally
                    {
                        _maintenanceSession = null;
                        await Task.Run(maintenanceSession.Dispose);
                    }
                }
            }
            finally
            {
                if (!retainedForExit)
                {
                    _maintenanceProcess = null;
                    _maintenanceLabel = null;
                    _maintenanceOutputCapture?.Dispose();
                    _maintenanceOutputCapture = null;
                    process.Dispose();
                }
                await RefreshCoreAsync();
            }
        }
    }

    private Process StartCommand(string command, bool detectUrl, ApiLaunchConfiguration? configuration = null)
    {
        if (Profile.UsesProcessSession) return StartConsoleCommand(command, isStart: detectUrl);
        var outputGeneration = Interlocked.Read(ref _outputGeneration);
        var frontendRunVersion = detectUrl && FrontendServiceSupport.IsFrontend(Profile)
            ? BeginFrontendRun() : (long?)null;
        var flutterRunVersion = detectUrl && FlutterRuntimeStatus.IsWeb(Profile) ? BeginFlutterRun() : (long?)null;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = "/d /s /c \"" + command + "\"",
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            },
            EnableRaisingEvents = true
        };
        if (configuration is not null) process.StartInfo = configuration.StartInfo;
        _apiProcessLaunched = detectUrl && configuration is not null;
        if (detectUrl) _launchConfiguration = configuration;
        if (detectUrl)
            process.Exited += (_, _) =>
            {
                try
                {
                    var code = process.ExitCode;
                    var expected = _expectedExits.TryRemove(process.Id, out _) || _intentionalStop || _disposed;
                    lock (_consoleLogSync)
                    {
                        if (outputGeneration != _outputGeneration) return;
                        if (frontendRunVersion is not null && frontendRunVersion != _managedFrontendRunVersion) return;
                        if (flutterRunVersion is not null && flutterRunVersion != _managedFlutterRunVersion) return;
                        if (!expected && code != 0)
                            _lastError = $"Start command exited with code {code}. See the output log.";
                        if (frontendRunVersion is not null && (expected || code != 0))
                            _frontendRunActive = false;
                    }
                    Log($"Start command exited with code {code}.", code != 0 && !expected, outputGeneration: outputGeneration);
                }
                catch (InvalidOperationException) { }
            };
        Log(configuration is null ? $"> {command}" : $"> dotnet run · {configuration.Environment} configuration (values hidden)",
            kind: ServiceLogKind.Command);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows did not start the command.");
        }
        catch
        {
            if (frontendRunVersion is not null) InvalidateFrontendRun();
            process.Dispose();
            throw;
        }
        if (detectUrl && configuration is not null) Interlocked.Increment(ref _managedApiRunVersion);
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        _owned[identity.Id] = identity;
        _hasManagedProcess = true;
        process.StandardInput.Close();
        var capture = new ServiceOutputCapture(process, (line, error) =>
            ReceiveOutput(line, error, detectUrl, configuration, outputGeneration, frontendRunVersion, flutterRunVersion));
        if (detectUrl) _startOutputCapture = capture;
        else _maintenanceOutputCapture = capture;
        return process;
    }

    private Process StartConsoleCommand(string command, bool isStart)
    {
        var outputGeneration = Interlocked.Read(ref _outputGeneration);
        Log($"> {command}", kind: ServiceLogKind.Command);
        var session = ConsoleProcessSession.Create(command, WorkingDirectory);
        var flutterRunVersion = isStart && FlutterRuntimeStatus.IsNative(Profile) ? BeginFlutterRun() : (long?)null;
        if (isStart) _consoleSession = session;
        else _maintenanceSession = session;
        _launchConfiguration = null;
        _apiProcessLaunched = false;
        var process = session.Process;
        try
        {
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
            _owned[identity.Id] = identity;
            _hasManagedProcess = true;
            if (isStart) process.Exited += (_, _) =>
            {
                try
                {
                    var code = process.ExitCode;
                    var expected = _expectedExits.TryRemove(process.Id, out _) || _intentionalStop || _disposed || _consoleWasStopped;
                    lock (_consoleLogSync)
                    {
                        if (!ReferenceEquals(_consoleSession, session) || outputGeneration != _outputGeneration) return;
                        if (!expected && code != 0) _lastError = $"Command exited with code {code}. See the output log.";
                    }
                    Log($"{(Profile.IsCommandApi ? "API" : "Console")} command exited with code {code}.", code != 0 && !expected,
                        code == 0 && !expected ? ServiceLogKind.Success : ServiceLogKind.Information, outputGeneration);
                }
                catch (InvalidOperationException) { }
            };
            process.EnableRaisingEvents = true;
            session.Resume((line, error) => ReceiveOutput(line, error, detectUrl: isStart && Profile.IsCommandApi, configuration: null,
                outputGeneration: outputGeneration, flutterRunVersion: flutterRunVersion));
            return process;
        }
        catch
        {
            if (isStart) _consoleSession = null;
            else _maintenanceSession = null;
            session.Dispose();
            throw;
        }
    }

    private void ReceiveOutput(string? line, bool error, bool detectUrl, ApiLaunchConfiguration? configuration,
        long? outputGeneration = null, long? frontendRunVersion = null, long? flutterRunVersion = null)
    {
        if (outputGeneration is not null && outputGeneration != Interlocked.Read(ref _outputGeneration)) return;
        if (frontendRunVersion is not null && frontendRunVersion != ManagedFrontendRunVersion) return;
        if (flutterRunVersion is not null && flutterRunVersion != Interlocked.Read(ref _managedFlutterRunVersion)) return;
        if (line is null) return;
        line = StripTerminalCodes(line);
        if (string.IsNullOrWhiteSpace(line)) return;
        if (flutterRunVersion is { } flutterVersion) ObserveFlutterOutput(line, flutterVersion);
        if (detectUrl)
        {
            var discovered = DiscoverLocalUrl(line);
            if (discovered is not null)
            {
                lock (_consoleLogSync)
                {
                    if (outputGeneration is not null && outputGeneration != _outputGeneration) return;
                    if (frontendRunVersion is not null && frontendRunVersion != _managedFrontendRunVersion) return;
                    var previous = Volatile.Read(ref _detectedUrl);
                    // Prefer the configured protocol when a server reports both HTTP and HTTPS.
                    if (previous is null || (Uri.TryCreate(Profile.Url, UriKind.Absolute, out var preferred)
                        && new Uri(discovered).Scheme == preferred.Scheme))
                        Volatile.Write(ref _detectedUrl, discovered);
                }
            }
            if (frontendRunVersion is { } runVersion)
                ObserveFrontendBuildOutput(line, runVersion, outputGeneration);
        }
        line = SensitiveDataProtection.Redact(configuration?.Redact(line) ?? line);
        Log(line.Length > 8192 ? line[..8192] + " … [line truncated]" : line, error, ServiceLogKind.Output, outputGeneration);
    }

    internal static string StripTerminalCodes(string line) => Ansi.Replace(line, "").Replace("\r", "");

    private long BeginFlutterRun()
    {
        lock (_consoleLogSync)
        {
            _flutterRunActive = true;
            _flutterWebBuildSuccessful = false;
            _flutterBuildFailed = false;
            _readyFlutterApps.Clear();
            Volatile.Write(ref _flutterAppProcessIdentities, []);
            _flutterStartupDetail = FlutterRuntimeStatus.IsWeb(Profile)
                ? "Starting Flutter; waiting for web compilation" : "Starting Flutter; waiting for the app window";
            return Interlocked.Increment(ref _managedFlutterRunVersion);
        }
    }

    private void ObserveFlutterOutput(string line, long version)
    {
        lock (_consoleLogSync)
        {
            if (!_flutterRunActive || version != _managedFlutterRunVersion || _intentionalStop || _disposed) return;
            var text = line.Trim();
            if (text.Contains("Failed to compile application", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Error launching application", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Unable to find executable", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Unable to start executable", StringComparison.OrdinalIgnoreCase))
            {
                _flutterBuildFailed = true;
                _flutterWebBuildSuccessful = false;
                _flutterStartupDetail = "Flutter could not start the app. See the output log.";
            }
            else if (FlutterRuntimeStatus.IsWeb(Profile) &&
                (text.Contains(" is being served at http", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("Waiting for connection from Dart debug extension at http", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("Flutter run key commands.", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("Recompile complete.", StringComparison.OrdinalIgnoreCase)))
            {
                _flutterWebBuildSuccessful = true;
                _flutterBuildFailed = false;
            }
            else if (text.StartsWith("Building ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Compiling ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Performing hot restart", StringComparison.OrdinalIgnoreCase))
            {
                _flutterStartupDetail = FlutterRuntimeStatus.IsWeb(Profile)
                    ? "Flutter is compiling the web app" : "Flutter is building the app; waiting for the app window";
                _flutterBuildFailed = false;
                if (FlutterRuntimeStatus.IsWeb(Profile)) _flutterWebBuildSuccessful = false;
            }
            else if (text.StartsWith("Resolving dependencies", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Downloading packages", StringComparison.OrdinalIgnoreCase))
                _flutterStartupDetail = "Flutter is preparing packages";
            else if (text.StartsWith("Launching ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Syncing files to device", StringComparison.OrdinalIgnoreCase))
                _flutterStartupDetail = FlutterRuntimeStatus.IsWeb(Profile)
                    ? "Flutter is starting the web app; waiting for compilation" : "Flutter is launching the app; waiting for the app window";
        }
    }

    private long BeginFrontendRun()
    {
        lock (_consoleLogSync)
        {
            var version = Interlocked.Increment(ref _managedFrontendRunVersion);
            _frontendRunStopRevision = Volatile.Read(ref _stopRevision);
            _frontendRunActive = true;
            _frontendBuildSuccessful = false;
            _frontendReadyEmitted = false;
            _frontendBuildRevision = 0;
            return version;
        }
    }

    private void InvalidateFrontendRun()
    {
        lock (_consoleLogSync) _frontendRunActive = false;
    }

    private void ObserveFrontendBuildOutput(string line, long runVersion, long? outputGeneration)
    {
        lock (_consoleLogSync)
        {
            if (!_frontendRunActive || runVersion != _managedFrontendRunVersion ||
                outputGeneration != _outputGeneration) return;
            // stderr alone is not a failed build; frontend tools also use it for normal progress.
            if (FrontendBuildFailed.IsMatch(line) || FrontendBuildStarted.IsMatch(line))
            {
                _frontendBuildSuccessful = false;
                _frontendBuildRevision++;
            }
            else if (FrontendBuildSucceeded.IsMatch(line))
            {
                _frontendBuildSuccessful = true;
                _frontendBuildRevision++;
            }
        }
    }

    private readonly record struct FrontendReadyAttempt(long RunVersion, long BuildRevision);

    private FrontendReadyAttempt? PendingFrontendReadiness(Inspection inspection, IReadOnlyList<ListeningPort> listeners)
    {
        var ownedIds = inspection.Owned.Select(process => process.Id).ToHashSet();
        if (!listeners.Any(listener => ownedIds.Contains(listener.ProcessId))) return null;
        lock (_consoleLogSync)
        {
            if (_disposed || _intentionalStop || !_frontendRunActive || !_frontendBuildSuccessful ||
                _frontendReadyEmitted || _lastError is not null || !FrontendServiceSupport.IsFrontend(Profile) ||
                _frontendRunStopRevision != Volatile.Read(ref _stopRevision)) return null;
            return new(_managedFrontendRunVersion, _frontendBuildRevision);
        }
    }

    private void NotifyFrontendReady(string? url, int httpStatus, FrontendReadyAttempt? attempt)
    {
        if (string.IsNullOrWhiteSpace(url) || httpStatus is < 200 or >= 400 || attempt is not { } verified) return;
        lock (_consoleLogSync)
        {
            // A rebuild/failure arriving during HTTP invalidates that probe, even if an old
            // bundle still answered. A later refresh can verify the next successful build.
            if (_disposed || _intentionalStop || !_frontendRunActive || !_frontendBuildSuccessful ||
                _frontendReadyEmitted || _lastError is not null || !HasManagedProcess ||
                Snapshot.State != ServiceState.Running || verified.RunVersion != _managedFrontendRunVersion ||
                verified.BuildRevision != _frontendBuildRevision ||
                _frontendRunStopRevision != Volatile.Read(ref _stopRevision)) return;
            _frontendReadyEmitted = true;
        }
        try { FrontendReady?.Invoke(url, verified.RunVersion); }
        catch (Exception) { /* Browser/UI subscribers must never break process supervision. */ }
    }

    internal static string? DiscoverLocalUrl(string line)
    {
        var match = StartupUrl.Match(StripTerminalCodes(line));
        if (!match.Success) return null;
        var value = match.Groups["url"].Value.TrimEnd(',', ';', '.', ')', '*');
        // Listener output is only endpoint discovery. Never propagate embedded credentials,
        // request paths, query tokens, or fragments into status, health probes, or browser links.
        return NormalizeLocalUri(value)?.GetLeftPart(UriPartial.Authority);
    }

    private sealed record Inspection(ProcessInventory Inventory, IReadOnlyList<InspectedProcess> All,
        IReadOnlyList<InspectedProcess> Owned, IReadOnlyList<ListeningPort> ReportedListeners,
        FlutterRuntimeStatus.NativeEvidence FlutterApps);

    // The inventory cache may already be complete. Dispatch the whole inspection, including
    // native identity/job checks and ownership traversal, instead of resuming that work on WPF.
    // The caller holds _gate until this task finishes, preserving the operation's ownership scope.
    private Task<Inspection> InspectAsync(bool fresh = false) => Task.Run(async () =>
    {
        var inventory = await ProcessInspector.ReadAsync(fresh, includePorts: !Profile.IsConsole).ConfigureAwait(false);
        var ancestors = ProcessInspector.Ancestors(inventory.Processes);
        if (!_runtimeLoaded)
        {
            var previous = ServiceRuntimeStore.Load(Profile.Id, WorkingDirectory);
            if (previous is not null)
            {
                foreach (var identity in previous.Processes.Where(p => !ancestors.Contains(p.Id) && ProcessInspector.IsSameProcess(p)))
                {
                    _owned[identity.Id] = identity;
                    _recoveredRoots.Add(identity);
                }
                if (Profile.UsesProcessSession)
                    foreach (var name in previous.ConsoleJobs)
                        if (!_retainedJobs.ContainsKey(name) && ConsoleProcessSession.OpenRetainedJob(name) is { } job)
                            _retainedJobs.Add(name, job);
            }
            _runtimeLoaded = true;
        }
        var consoleMembers = new List<InspectedProcess>();
        var consoleInspectionFailed = false;
        foreach (var session in new[] { _consoleSession, _maintenanceSession }.OfType<ConsoleProcessSession>())
        {
            try
            {
                consoleMembers.AddRange(session.ReadProcesses());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                consoleInspectionFailed = true;
                inventory = inventory with { InspectionError = ex.Message };
            }
        }
        foreach (var retained in _retainedJobs.ToArray())
        {
            try
            {
                var members = ConsoleProcessSession.ReadJobProcesses(retained.Value);
                if (members.Count == 0)
                {
                    // A completed retained run must not pollute the next run's origin or recovery record.
                    _retainedJobs.Remove(retained.Key);
                    retained.Value.Dispose();
                }
                else consoleMembers.AddRange(members);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                consoleInspectionFailed = true;
                inventory = inventory with { InspectionError = ex.Message };
            }
        }
        var processById = inventory.Processes.ToDictionary(p => p.Id);
        foreach (var member in consoleMembers)
            if (!processById.TryGetValue(member.Id, out var existing) || existing.Identity != member.Identity)
                processById[member.Id] = member;
            else if (string.IsNullOrEmpty(existing.Executable) && !string.IsNullOrEmpty(member.Executable))
                processById[member.Id] = existing with { Executable = member.Executable };
        inventory = inventory with { Processes = processById.Values.ToArray() };
        var reportedListeners = inventory.ListeningPorts;
        // Process and TCP snapshots are taken separately, and a cached listener can outlive its
        // process. Drop only confirmed exits; an inaccessible listener must remain a blocker.
        var exitedListeners = inventory.ListeningPorts.Select(p => p.ProcessId).Distinct()
            .Where(ProcessInspector.IsDefinitelyStopped).ToHashSet();
        if (exitedListeners.Count > 0)
            inventory = inventory with { ListeningPorts = inventory.ListeningPorts
                .Where(p => !exitedListeners.Contains(p.ProcessId)).ToArray() };
        var owned = ProcessInspector.Descendants(inventory.Processes, _owned.Values.Concat(consoleMembers.Select(p => p.Identity)))
            .Where(p => !ancestors.Contains(p.Id) && ProcessInspector.IsSameProcess(p.Identity)).ToArray();
        if (consoleMembers.Count > 0 && owned.Length == 0)
        {
            consoleInspectionFailed = true;
            inventory = inventory with { InspectionError = "Console child processes changed during inspection; retry the operation." };
        }
        if (inventory.InspectionError is null) _owned.Clear();
        foreach (var process in owned) _owned[process.Id] = process.Identity;
        _hasManagedProcess = owned.Length > 0 || consoleInspectionFailed ||
            (inventory.InspectionError is not null && _owned.Values.Any(ProcessInspector.IsSameProcess));
        // Folder evidence cannot distinguish overlapping services. Managed/recovered
        // identities and verified job membership remain sufficient for both services.
        InspectedProcess[] external = UsesManagedProcessTrackingOnly ? [] : inventory.Processes.Where(p => !ancestors.Contains(p.Id)
            && ProcessInspector.BelongsToDirectory(p, WorkingDirectory)).ToArray();
        var all = ProcessInspector.Descendants(inventory.Processes,
            external.Select(p => p.Identity).Concat(owned.Select(p => p.Identity)))
            .Where(p => !ancestors.Contains(p.Id) && ProcessInspector.IsSameProcess(p.Identity)).ToArray();
        var flutterApps = FlutterRuntimeStatus.IsNative(Profile)
            ? FlutterRuntimeStatus.InspectNativeApps(WorkingDirectory, all) : FlutterRuntimeStatus.NativeEvidence.Empty;
        Volatile.Write(ref _flutterAppProcessIdentities, _intentionalStop || inventory.InspectionError is not null
            ? [] : flutterApps.Apps.Select(app => app.Identity).ToArray());
        return new Inspection(inventory, all, owned, reportedListeners, flutterApps);
    });

    private async Task RefreshCoreAsync(Inspection? inspection = null)
    {
        inspection ??= await InspectAsync();
        var ids = inspection.All.Select(p => p.Id).Order().ToArray();
        _hasVerifiedNoServiceProcesses = inspection.Inventory.InspectionError is null && ids.Length == 0;
        if (inspection.Inventory.InspectionError is not null)
        {
            Publish(ServiceState.Error, inspection.Inventory.InspectionError, ids, BuildUiUrl());
            return;
        }
        // A maintenance wait retained for a canceled close no longer owns the gate.
        // Keep its streams/job while any verified descendants still exist; release after completion.
        if (_operationCancellation is null && _maintenanceProcess is { HasExited: true } maintenance
            && inspection.Owned.Count == 0
            && (_maintenanceSession is null || (await Task.Run(_maintenanceSession.ReadProcesses)).Count == 0))
        {
            if (!_intentionalStop)
            {
                var exitCode = maintenance.ExitCode;
                if (exitCode != 0) _lastError = $"{_maintenanceLabel ?? "Maintenance"} exited with code {exitCode}. See the output log.";
                Log(exitCode == 0 ? $"{_maintenanceLabel ?? "Maintenance"} completed successfully." : _lastError!,
                    error: exitCode != 0, kind: exitCode == 0 ? ServiceLogKind.Success : ServiceLogKind.Error);
            }
            if (_maintenanceSession is { } completed) await DrainConsoleOutputAsync(completed);
            _maintenanceSession?.Dispose();
            _maintenanceSession = null;
            _maintenanceOutputCapture?.Dispose();
            _maintenanceOutputCapture = null;
            _maintenanceProcess.Dispose();
            _maintenanceProcess = null;
            _maintenanceLabel = null;
        }
        if (Profile.UsesProcessSession && ids.Length == 0 &&
            _consoleSession is { OutputDrainAttempted: false } completedSession && completedSession.Process.HasExited)
            await DrainConsoleOutputAsync(completedSession);
        if (Profile.IsConsole)
        {
            if (FlutterRuntimeStatus.IsNative(Profile)) RefreshFlutter(inspection, ids);
            else RefreshConsole(inspection, ids);
            return;
        }
        if (inspection.Owned.Count == 0) InvalidateFrontendRun();
        // A stopped service may still have an old-port conflict. Its previous detected
        // address must not override the saved address after the user edits its port.
        if (ids.Length == 0) Volatile.Write(ref _detectedUrl, null);
        var endpoint = GetEndpoint(inspection);
        if (endpoint is null)
        {
            Publish(ServiceState.Error, "Set a valid local HTTP or HTTPS URL in project settings.", ids);
            return;
        }
        var ours = ids.ToHashSet();
        var listeners = inspection.Inventory.ListeningPorts.Where(p => p.Port == endpoint.Port).ToArray();
        var foreign = listeners.Where(p => !ours.Contains(p.ProcessId)).Select(p => p.ProcessId).Distinct().ToArray();
        var uiUrl = BuildUiUrl(endpoint.AbsoluteUri);
        if (foreign.Length > 0)
        {
            var prefix = ids.Length == 0 ? "Service is stopped; " : "";
            var detail = $"{prefix}port {endpoint.Port} is occupied by another process (PID {string.Join(", ", foreign)}). Use Force stop or Resolve conflict & restart to review it.";
            Publish(ServiceState.Conflict, detail, ids);
            return;
        }
        if (ids.Length == 0)
        {
            Volatile.Write(ref _detectedUrl, null);
            Publish(_lastError is null ? ServiceState.Stopped : ServiceState.Error,
                _lastError ?? "No matching service processes", ids);
            return;
        }
        if (listeners.Length == 0)
        {
            Publish(ServiceState.Starting, $"Process detected; waiting for HTTP on port {endpoint.Port}", ids, uiUrl);
            return;
        }
        var verifyFlutterAssets = false;
        if (FlutterRuntimeStatus.IsWeb(Profile))
        {
            bool waiting, failed;
            string detail;
            lock (_consoleLogSync)
            {
                waiting = _flutterRunActive && !_flutterWebBuildSuccessful;
                failed = _flutterRunActive && _flutterBuildFailed;
                detail = _lastError ?? _flutterStartupDetail;
                verifyFlutterAssets = !_flutterRunActive;
            }
            if (_lastError is not null || failed || waiting)
            {
                Publish(_lastError is not null || failed ? ServiceState.Error : ServiceState.Starting, detail, ids, uiUrl);
                return;
            }
        }
        try
        {
            var frontendAttempt = PendingFrontendReadiness(inspection, listeners);
            using var request = new HttpRequestMessage(HttpMethod.Get, uiUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var origin = ProcessOrigin(inspection);
            var status = (int)response.StatusCode;
            if (FlutterRuntimeStatus.IsWeb(Profile))
            {
                bool waiting, failed;
                string detail;
                lock (_consoleLogSync)
                {
                    waiting = _flutterRunActive && !_flutterWebBuildSuccessful;
                    failed = _flutterRunActive && _flutterBuildFailed;
                    detail = _lastError ?? _flutterStartupDetail;
                }
                // Compiler output can change while the HTTP probe is pending. A listening
                // WebDevFS server alone does not prove that Flutter finished compiling.
                if (_lastError is not null || failed || waiting)
                {
                    Publish(_lastError is not null || failed ? ServiceState.Error : ServiceState.Starting, detail, ids, uiUrl);
                    return;
                }
                if (status >= 400)
                {
                    Publish(ServiceState.Error, $"Flutter web endpoint returned HTTP {status}", ids, uiUrl);
                    return;
                }
                if (verifyFlutterAssets && !await FlutterWebHasCompiledAssetsAsync(endpoint))
                {
                    Publish(ServiceState.Starting, "Flutter web server detected; waiting for compiled app assets", ids, uiUrl);
                    return;
                }
            }
            Publish(ServiceState.Running, status >= 500 ? $"{origin}; HTTP {status} (server reports an error)" : $"{origin}; HTTP {status}", ids, uiUrl,
                status >= 500 ? ServiceLogKind.Warning : ServiceLogKind.Success);
            RefreshSwaggerUiStatus(inspection, uiUrl);
            NotifyFrontendReady(uiUrl, status, frontendAttempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Publish(ServiceState.Starting, "Port is listening; waiting for an HTTP response", ids, uiUrl);
        }
    }

    private static async Task<bool> FlutterWebHasCompiledAssetsAsync(Uri endpoint)
    {
        // Recovered/external runs have no captured readiness output. Inspect only the normal
        // generated JavaScript entry point; index.html can be served before compilation finishes.
        foreach (var entrypoint in new[] { "/main.dart.js", "/main.dart.mjs" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.GetLeftPart(UriPartial.Authority) + entrypoint));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode == System.Net.HttpStatusCode.OK &&
                mediaType?.Contains("javascript", StringComparison.OrdinalIgnoreCase) == true &&
                response.Content.Headers.ContentLength != 0)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                var firstByte = new byte[1];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                return await stream.ReadAsync(firstByte, timeout.Token) > 0;
            }
        }
        return false;
    }

    private void RefreshSwaggerUiStatus(Inspection inspection, string? uiUrl)
    {
        if (!IsSwaggerUiLaunch || _disposed || _intentionalStop || Snapshot.State != ServiceState.Running ||
            !Uri.TryCreate(uiUrl, UriKind.Absolute, out var uri)) return;
        var stopRevision = Volatile.Read(ref _stopRevision);
        var runVersion = ManagedApiRunVersion;
        var profileUrl = Profile.Url;
        var uiPath = Profile.UiPath;
        // Start times distinguish a replacement process even when Windows reuses its PID.
        var processes = string.Join(",", inspection.All.OrderBy(process => process.Id)
            .Select(process => $"{process.Id}:{process.Identity.StartedUtcTicks}"));
        var key = $"{uiUrl}|{profileUrl}|{uiPath}|{stopRevision}|{runVersion}|{processes}";
        CancellationTokenSource cancellation;
        long version;
        var changed = false;
        lock (_swaggerUiSync)
        {
            if (!string.Equals(_swaggerProbeKey, key, StringComparison.Ordinal))
            {
                _swaggerCancellation?.Cancel();
                _swaggerCancellation = null;
                _swaggerProbeKey = key;
                _swaggerProbeVersion++;
                _swaggerProbeInFlight = false;
                _swaggerNextCheckUtc = DateTime.MinValue;
                var checking = new SwaggerUiStatus(SwaggerUiAvailability.Checking, "API is running; checking its configured Swagger UI.", uiUrl);
                changed = _swaggerStatus != checking;
                Volatile.Write(ref _swaggerStatus, checking);
            }
            if (_swaggerProbeInFlight || DateTime.UtcNow < _swaggerNextCheckUtc) return;
            cancellation = new CancellationTokenSource();
            _swaggerCancellation = cancellation;
            _swaggerProbeInFlight = true;
            version = _swaggerProbeVersion;
        }
        if (changed) SnapshotChanged?.Invoke();
        // Probing is independent of the process-operation gate and never blocks stop/restart.
        _ = Task.Run(() => ProbeSwaggerUiAsync(uri, key, version, stopRevision, runVersion, profileUrl, uiPath, cancellation));
    }

    private async Task ProbeSwaggerUiAsync(Uri uri, string key, long version, int stopRevision,
        long runVersion, string profileUrl, string uiPath, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await SwaggerUiProbe.ProbeAsync(uri, cancellation.Token).ConfigureAwait(false);
            var changed = false;
            lock (_swaggerUiSync)
            {
                if (_disposed || _intentionalStop || cancellation.IsCancellationRequested || version != _swaggerProbeVersion ||
                    !string.Equals(key, _swaggerProbeKey, StringComparison.Ordinal) ||
                    stopRevision != Volatile.Read(ref _stopRevision) || runVersion != ManagedApiRunVersion ||
                    !string.Equals(profileUrl, Profile.Url, StringComparison.Ordinal) || !string.Equals(uiPath, Profile.UiPath, StringComparison.Ordinal) ||
                    Snapshot.State != ServiceState.Running || !string.Equals(uri.AbsoluteUri, Snapshot.ActiveUrl, StringComparison.Ordinal) ||
                    !IsSwaggerUiLaunch) return;
                changed = _swaggerStatus != result;
                Volatile.Write(ref _swaggerStatus, result);
                _swaggerNextCheckUtc = DateTime.UtcNow.AddSeconds(15);
            }
            if (changed) SnapshotChanged?.Invoke();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (_swaggerUiSync)
            {
                if (ReferenceEquals(_swaggerCancellation, cancellation))
                {
                    _swaggerCancellation = null;
                    _swaggerProbeInFlight = false;
                }
            }
            cancellation.Dispose();
        }
    }

    private bool ResetSwaggerUiStatus(ServiceState state)
    {
        if (state == ServiceState.Running && IsSwaggerUiLaunch && !_disposed && !_intentionalStop) return false;
        lock (_swaggerUiSync)
        {
            _swaggerCancellation?.Cancel();
            _swaggerCancellation = null;
            _swaggerProbeKey = null;
            _swaggerProbeVersion++;
            _swaggerProbeInFlight = false;
            _swaggerNextCheckUtc = DateTime.MinValue;
            var status = new SwaggerUiStatus(IsSwaggerUiLaunch ? SwaggerUiAvailability.ApiUnavailable : SwaggerUiAvailability.NotApplicable,
                IsSwaggerUiLaunch ? "API is not running and ready yet." : "This launch address does not target Swagger UI.");
            if (_swaggerStatus == status) return false;
            Volatile.Write(ref _swaggerStatus, status);
            return true;
        }
    }

    private void RefreshConsole(Inspection inspection, int[] ids)
    {
        int? exitCode = null;
        if (!_consoleWasStopped && _consoleSession is { } session && session.Process.HasExited)
            exitCode = session.Process.ExitCode;
        if (ids.Length > 0)
        {
            var origin = ProcessOrigin(inspection);
            var detail = exitCode is { } code
                ? $"{origin}; command exited with code {code}; child processes still running"
                : $"{origin}; console processes running";
            Publish(ServiceState.Running, detail, ids,
                runningLogKind: exitCode is not null && exitCode != 0 ? ServiceLogKind.Warning : null);
            return;
        }
        if (_lastError is not null || exitCode is not null and not 0)
        {
            Publish(ServiceState.Error, _lastError ?? $"Command exited with code {exitCode}. See the output log.", ids);
            return;
        }
        if (exitCode == 0)
        {
            Publish(ServiceState.Completed, "Command completed successfully (exit code 0)", ids);
            return;
        }
        Publish(ServiceState.Stopped, _consoleWasStopped ? "Console processes stopped" : "No matching console processes", ids);
    }

    private void RefreshFlutter(Inspection inspection, int[] ids)
    {
        int? exitCode = null;
        if (!_consoleWasStopped && _consoleSession is { } session && session.Process.HasExited)
            exitCode = session.Process.ExitCode;
        bool appReady, appWasReady, failed;
        string startupDetail;
        lock (_consoleLogSync)
        {
            foreach (var identity in inspection.FlutterApps.ReadyApps) _readyFlutterApps.Add(identity);
            appReady = inspection.FlutterApps.Apps.Any(app => _readyFlutterApps.Contains(app.Identity));
            appWasReady = _readyFlutterApps.Count > 0;
            failed = _flutterRunActive && _flutterBuildFailed;
            startupDetail = _flutterStartupDetail;
        }
        if (_lastError is not null || exitCode is not null and not 0 || failed)
        {
            Publish(ServiceState.Error, _lastError ?? (exitCode is not null and not 0
                ? $"Flutter command exited with code {exitCode}. See the output log." : startupDetail), ids);
            return;
        }
        if (appReady)
        {
            Publish(ServiceState.Running, exitCode is { } code
                ? $"{ProcessOrigin(inspection)}; Flutter app running; Flutter command exited with code {code}"
                : $"{ProcessOrigin(inspection)}; Flutter app window detected", ids);
            return;
        }
        if (ids.Length > 0)
        {
            var detail = inspection.FlutterApps.Apps.Count > 0 ? "Flutter app process detected; waiting for the app window"
                : appWasReady ? "Flutter app exited; waiting for Flutter tools to finish" : startupDetail;
            Publish(ServiceState.Starting, detail, ids);
            return;
        }
        if (exitCode == 0)
        {
            Publish(ServiceState.Completed, appWasReady ? "Flutter app exited; command completed (exit code 0)"
                : "Flutter command completed (exit code 0); no local app process remains; app readiness was not verified", ids);
            return;
        }
        Publish(ServiceState.Stopped, _consoleWasStopped ? "Flutter app and tools stopped" : "No matching Flutter app processes", ids);
    }

    private string ProcessOrigin(Inspection inspection) => inspection.Owned.Count == 0 ? "Started outside launcher"
        : _retainedJobs.Count > 0 || inspection.Owned.Any(p => _recoveredRoots.Contains(p.Identity))
            ? "Left running by a previous launcher" : "Started by launcher";

    private async Task DrainConsoleOutputAsync(ConsoleProcessSession session)
    {
        session.OutputDrainAttempted = true;
        try { await session.OutputCompletion.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            Log("Console output is still draining; some final lines may arrive after the command status.", kind: ServiceLogKind.Warning);
        }
    }

    private Uri? GetEndpoint(Inspection inspection)
    {
        var endpoint = NormalizeLocalUri(Volatile.Read(ref _detectedUrl) ?? Profile.Url);
        if (endpoint is null) return null;
        var ours = inspection.All.Select(p => p.Id).ToHashSet();
        var matchingPorts = inspection.Inventory.ListeningPorts.Where(p => ours.Contains(p.ProcessId)).ToArray();
        if (!matchingPorts.Any(p => p.Port == endpoint.Port) && Volatile.Read(ref _detectedUrl) is null)
        {
            var ports = matchingPorts.Select(p => p.Port).Distinct().ToArray();
            if (ports.Length == 1)
                endpoint = new UriBuilder(endpoint) { Port = ports[0] }.Uri;
        }
        return endpoint;
    }

    private async Task<bool> StopCoreAsync(bool includeExternal, ConflictApproval? conflictApproval = null)
    {
        _intentionalStop = true;
        InvalidateFrontendRun();
        lock (_consoleLogSync) _flutterRunActive = false;
        Volatile.Write(ref _flutterAppProcessIdentities, []);
        try
        {
            var inspection = await InspectAsync(fresh: true);
            var stoppedJobIds = new HashSet<int>();
            int? stoppedJobPort = null;
            if (Profile.UsesProcessSession && _hasManagedProcess)
            {
                stoppedJobIds.UnionWith(inspection.Owned.Select(process => process.Id));
                if (Profile.IsCommandApi) stoppedJobPort = GetEndpoint(inspection)?.Port;
                _consoleWasStopped = true;
                // Job membership covers children created between snapshots, with no path or PID-name guess.
                await Task.Run(() =>
                {
                    _consoleSession?.Stop();
                    _maintenanceSession?.Stop();
                    foreach (var job in _retainedJobs.Values) ConsoleProcessSession.StopRetainedJob(job);
                });
                inspection = await InspectAsync(fresh: true);
            }
            var conflictIdentities = conflictApproval?.Targets.Select(p => p.Identity).ToHashSet() ?? [];
            if (conflictApproval is not null)
            {
                if (inspection.Inventory.InspectionError is not null)
                    throw new InvalidOperationException(inspection.Inventory.InspectionError);
                var protectedIds = ProcessInspector.Ancestors(inspection.Inventory.Processes);
                if (conflictApproval.Targets.Any(p => p.Id <= 4 || protectedIds.Contains(p.Id)))
                    throw new InvalidOperationException("The conflicting process tree now includes the launcher or its parent. Stop it in its original application.");
                var serviceIds = inspection.All.Select(p => p.Id).ToHashSet();
                // Approval covers these exact identities, not the port or a replacement listener.
                if (inspection.Inventory.ListeningPorts.Where(p => p.Port == conflictApproval.Port && !serviceIds.Contains(p.ProcessId))
                    .Any(p => !inspection.Inventory.Processes.Any(actual => actual.Id == p.ProcessId && conflictIdentities.Contains(actual.Identity))))
                    throw new InvalidOperationException("The process using the port changed. Click Force stop or Resolve conflict & restart again to review it.");
                Log($"Clearing port {conflictApproval.Port} conflict; approved PID {string.Join(", ", conflictApproval.Targets.Select(p => p.Id))}.");
            }
            IReadOnlyList<InspectedProcess> Remaining(Inspection state) => (includeExternal ? state.All : state.Owned)
                .Concat(state.Inventory.Processes.Where(p => conflictIdentities.Contains(p.Identity)))
                .DistinctBy(p => p.Id).ToArray();
            var targets = Remaining(inspection);
            if (targets.Count == 0 && conflictApproval is null && stoppedJobPort is null)
            {
                // An explicit stop also acknowledges a previous failed start. A released port
                // must not leave a historical conflict/error on an otherwise stopped service.
                _lastError = inspection.Inventory.InspectionError;
                Log(Profile.UsesProcessSession && _consoleWasStopped ? "Command and child processes stopped."
                    : includeExternal ? "No verified service processes to stop." : "No launcher-owned processes to stop.");
                await RefreshCoreAsync(inspection);
                return inspection.Inventory.InspectionError is null;
            }
            Publish(ServiceState.Busy, conflictApproval is null ? "Stopping verified process trees" : "Stopping verified service and confirmed conflict processes",
                targets.Select(p => p.Id).ToArray(), BuildUiUrl());
            if (!Profile.IsConsole && _startProcess is not null && targets.Any(p => p.Id == _startProcess.Id))
                _expectedExits[_startProcess.Id] = 0;
            var errors = await KillProcessesAsync(targets);
            _lastError = errors.Count > 0 ? string.Join(" ", errors) : null;
            foreach (var error in errors) Log(error, true);
            var after = await InspectAsync(fresh: true);
            var remaining = Remaining(after);
            var stoppedIds = targets.Select(p => p.Id).Concat(stoppedJobIds).ToHashSet();
            bool HasListeners(Inspection state) => !Profile.IsConsole && state.ReportedListeners
                .Any(p => stoppedIds.Contains(p.ProcessId) || p.Port == conflictApproval?.Port || p.Port == stoppedJobPort);
            var releaseDeadline = DateTime.UtcNow.AddSeconds(5);
            // Windows can report a terminated process's listener briefly after WaitForExit returns.
            // Restart must wait for both process termination and socket release.
            while ((remaining.Count > 0 || HasListeners(after))
                && DateTime.UtcNow < releaseDeadline)
            {
                await Task.Delay(100);
                after = await InspectAsync(fresh: true);
                remaining = Remaining(after);
            }
            if (after.Inventory.InspectionError is not null)
                _lastError ??= after.Inventory.InspectionError;
            else if (remaining.Count > 0)
                _lastError ??= $"Some processes are still running (PID {string.Join(", ", remaining.Select(p => p.Id))}). Check the output log or Windows process permissions.";
            else if (HasListeners(after))
                _lastError ??= "The service port is still occupied. A process may have restarted automatically. Review the conflict and try again.";
            else Volatile.Write(ref _detectedUrl, null);
            await RefreshCoreAsync(after);
            if (_lastError is not null)
                Publish(ServiceState.Error, _lastError, after.All.Select(p => p.Id).ToArray(), Snapshot.ActiveUrl);
            else Log(conflictApproval is null ? "Service processes stopped." : $"Service stopped; port {conflictApproval.Port} is clear.",
                kind: ServiceLogKind.Success);
            return remaining.Count == 0 && errors.Count == 0 && _lastError is null;
        }
        finally { _intentionalStop = false; }
    }

    private async Task<bool> StopAndClearConsoleAsync(ConflictApproval? conflictApproval = null)
    {
        var previousOutputGeneration = Interlocked.Read(ref _outputGeneration);
        ResetConsoleForLifecycle();
        var stopped = false;
        try
        {
            stopped = await StopCoreAsync(includeExternal: true, conflictApproval: conflictApproval);
            return stopped;
        }
        finally
        {
            // A failed stop can leave the original process alive. Resume its capture while
            // retaining the new stop errors; successful restarts reject late old-process output.
            if (!stopped)
            {
                lock (_consoleLogSync) Interlocked.Exchange(ref _outputGeneration, previousOutputGeneration);
            }
        }
    }

    private static Task<List<string>> KillProcessesAsync(IReadOnlyList<InspectedProcess> targets) => Task.Run(async () =>
    {
        var errors = new List<string>();
        // Children are newer than parents; check identity immediately before every individual kill.
        foreach (var target in targets.OrderByDescending(p => p.StartedUtcTicks))
        {
            if (!ProcessInspector.IsSameProcess(target.Identity)) continue;
            try
            {
                using var process = Process.GetProcessById(target.Id);
                if (process.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks) continue;
                process.Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { errors.Add($"PID {target.Id} did not exit within four seconds."); }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) { errors.Add($"Could not stop PID {target.Id}: {ex.Message}"); }
        }
        return errors;
    });

    private void ValidateCommand(string command, string operation)
    {
        if (!Directory.Exists(WorkingDirectory)) throw new DirectoryNotFoundException($"Service folder does not exist: {WorkingDirectory}");
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException($"Set the {operation.ToLowerInvariant()} command in project settings first.");
        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidOperationException("Commands must be a single line.");
    }

    private string? BuildUiUrl(string? value = null)
    {
        if (Profile.IsConsole) return null;
        var uri = NormalizeLocalUri(value ?? Volatile.Read(ref _detectedUrl) ?? Profile.Url);
        if (uri is null) return null;
        var builder = new UriBuilder(uri);
        if (!string.IsNullOrWhiteSpace(Profile.UiPath))
        {
            var path = Profile.UiPath.Trim();
            var relative = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/" + path.TrimStart('/'));
            builder.Path = relative.AbsolutePath;
            builder.Query = relative.Query;
            builder.Fragment = relative.Fragment;
        }
        return builder.Uri.AbsoluteUri;
    }

    private static Uri? NormalizeLocalUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Host is "0.0.0.0" or "[::]" or "::") uri = new UriBuilder(uri) { Host = "localhost" }.Uri;
        return uri.IsLoopback ? uri : null;
    }

    private void Publish(ServiceState state, string detail, IReadOnlyList<int> ids, string? url = null,
        ServiceLogKind? runningLogKind = null)
    {
        var previous = Snapshot;
        Volatile.Write(ref _snapshot, new(state, detail, ids.ToArray(), url, _hasManagedProcess));
        var swaggerChanged = ResetSwaggerUiStatus(state);
        // A new stop/restart clears earlier diagnostics, including an identical error from
        // the previous attempt. Emit that current failure once in the fresh console buffer.
        var repeatDiagnostic = _repeatDiagnosticAfterConsoleReset && state is ServiceState.Error or ServiceState.Conflict;
        if (previous.State == state && previous.Detail == detail && previous.ActiveUrl == url && !swaggerChanged && !repeatDiagnostic) return;
        SnapshotChanged?.Invoke();
        switch (state)
        {
            case ServiceState.Running:
            case ServiceState.Completed:
                Log(detail, kind: runningLogKind ?? ServiceLogKind.Success);
                break;
            case ServiceState.Conflict:
                _repeatDiagnosticAfterConsoleReset = false;
                Log(detail, kind: ServiceLogKind.Warning);
                break;
            case ServiceState.Error:
                _repeatDiagnosticAfterConsoleReset = false;
                Log(detail, error: true);
                break;
        }
    }

    private void CancelMaintenance()
    {
        Interlocked.Increment(ref _stopRevision);
        if (ResetSwaggerUiStatus(ServiceState.Stopped)) SnapshotChanged?.Invoke();
        try { Volatile.Read(ref _operationCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void RecordConsoleMessage(string message, ServiceLogKind kind) =>
        Log(message, kind == ServiceLogKind.Error, kind);

    internal void ClearConsoleOutput() => ResetConsoleOutput(discardPreviousProcessOutput: false);

    private void ResetConsoleForLifecycle()
    {
        // Console apps retain their completed command history. Web services start a fresh view
        // only once restart validation or conflict confirmation has succeeded.
        if (!Profile.IsConsole) ResetConsoleOutput(discardPreviousProcessOutput: true);
    }

    private void ResetConsoleOutput(bool discardPreviousProcessOutput)
    {
        lock (_consoleLogSync)
        {
            if (discardPreviousProcessOutput)
            {
                Interlocked.Increment(ref _outputGeneration);
                _repeatDiagnosticAfterConsoleReset = true;
            }
            Interlocked.Exchange(ref _consoleResetSequence, ++_consoleSequence);
            // The UI subscriber queues work without waiting for the dispatcher. Notify before
            // admitting new logs so clearing can never erase this operation's fresh output.
            try { ConsoleOutputReset?.Invoke(); }
            catch (Exception) { /* Console display must never break process supervision. */ }
        }
    }

    private void Log(string message, bool error = false, ServiceLogKind kind = ServiceLogKind.Information,
        long? outputGeneration = null)
    {
        var safeMessage = SensitiveDataProtection.Redact(_launchConfiguration?.Redact(message) ?? message);
        lock (_consoleLogSync)
        {
            if (outputGeneration is not null && outputGeneration != _outputGeneration) return;
            _logs.Writer.TryWrite(new(DateTime.Now, Profile.Id, safeMessage,
                error, error && kind != ServiceLogKind.Output ? ServiceLogKind.Error : kind)
            { ConsoleSequence = ++_consoleSequence });
        }
    }

    private async Task PumpLogsAsync()
    {
        await foreach (var log in _logs.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { LogReceived?.Invoke(log); }
            catch (Exception) { /* A UI subscriber must never break process supervision. */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalStop = true;
        Volatile.Write(ref _flutterAppProcessIdentities, []);
        CancelMaintenance();
        _consoleSession?.Dispose();
        _maintenanceSession?.Dispose();
        _startOutputCapture?.Dispose();
        _maintenanceOutputCapture?.Dispose();
        foreach (var job in _retainedJobs.Values)
        {
            try { ConsoleProcessSession.StopRetainedJob(job); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            job.Dispose();
        }
        _retainedJobs.Clear();
        // This fallback is deliberately limited to identities recorded while they belonged to us.
        // Normal UI shutdown awaits StopManagedAsync, which also captures current descendants.
        foreach (var identity in _owned.Values.ToArray())
            if (ProcessInspector.IsSameProcess(identity))
                try
                {
                    using var process = Process.GetProcessById(identity.Id);
                    if (process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _startProcess?.Dispose();
        _maintenanceProcess?.Dispose();
        _logs.Writer.TryComplete();
        ServiceFolders.TryRemove(_runnerId, out _);
    }
}
