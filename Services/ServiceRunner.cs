using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Runs explicitly configured local commands. Normal shutdown should await StopManagedAsync before
/// Dispose; Dispose also performs a best-effort kill of owned trees. Externally launched services
/// are stopped only through explicit stop, restart, or confirmed conflict recovery.
/// </summary>
public sealed class ServiceRunner : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B(?:\][^\x07]*(?:\x07|\x1B\\)|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);
    private static readonly Regex StartupUrl = new(@"(?:Now listening on:|\bLocal:|(?:listening|running|started|ready)\s+(?:at|on)\s*:?|open your browser on)\s*(?<url>https?://[^\s<>""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
    private long _consoleSequence;
    private long _consoleResetSequence;
    private long _outputGeneration;
    private bool _repeatDiagnosticAfterConsoleReset;
    private Process? _startProcess;
    private ConsoleProcessSession? _consoleSession;
    private ConsoleProcessSession? _maintenanceSession;
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
    public string? AppliedConfigurationEnvironment => HasManagedProcess && _apiProcessLaunched ? _launchConfiguration?.Environment : null;
    public string? AppliedDatabaseIdentifier => HasManagedProcess && _apiProcessLaunched ? _launchConfiguration?.DatabaseIdentifier : null;
    public long ManagedApiRunVersion => Interlocked.Read(ref _managedApiRunVersion);
    internal bool MatchesAppliedLocalDatabaseConnection(DatabaseConnectionSource source) =>
        source is not null && Snapshot.State == ServiceState.Running && HasManagedProcess &&
        _apiProcessLaunched && _launchConfiguration?.MatchesAppliedLocalDatabaseConnection(source) == true;
    public bool ConfigurationNeedsRestart { get; set; }

    public ServiceRunner(ServiceProfile profile, string workingDirectory)
    {
        Profile = profile;
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        if (profile.IsConsole) _snapshot = new(ServiceState.Checking, "Waiting for the first process check", []);
        _ = PumpLogsAsync();
    }

    public event Action<ServiceLog>? LogReceived;
    internal event Action<string, long>? FrontendReady;
    internal event Action? ConsoleOutputReset;
    internal long ConsoleResetSequence => Interlocked.Read(ref _consoleResetSequence);
    internal long ManagedFrontendRunVersion => Interlocked.Read(ref _managedFrontendRunVersion);
    public ServiceProfile Profile { get; }
    public string WorkingDirectory { get; }
    public ServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool HasManagedProcess => _hasManagedProcess;
    public bool HasVerifiedNoServiceProcesses => _hasVerifiedNoServiceProcesses;

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

    public Task ApplyConfigurationAsync(ApiLaunchConfiguration configuration) => WithGateAsync(async () =>
    {
        // The UI preflights and saves the selection before entering the process operation.
        if (await StopAndClearConsoleAsync()) await StartCoreAsync(configuration);
    });

    private ApiLaunchConfiguration? PrepareConfiguration() => !Profile.IsConsole && Profile.ApiConfiguration is { } selection
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
        if (Profile.IsConsole) configuration = null;
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
        // Inspection and output draining yield. Respect a stop or dashboard override
        // that arrived during those awaits before launching another process.
        beforeStart?.Invoke();
        _startProcess = await Task.Run(() => StartCommand(Profile.StartCommand, detectUrl: true, configuration));
        ConfigurationNeedsRestart = false;
        Publish(ServiceState.Starting, Profile.IsConsole ? "Starting console command" : "Starting; waiting for the local HTTP endpoint",
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
        if (stopRevision != Volatile.Read(ref _stopRevision)) cancellation.Cancel();
        if (cancellation.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            return;
        }
        using var process = await Task.Run(() => StartCommand(command, detectUrl: false));
        var maintenanceSession = _maintenanceSession;
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
            if (maintenanceSession is not null) await Task.Run(maintenanceSession.Stop);
            await KillProcessesAsync(ProcessInspector.Descendants((await ProcessInspector.ReadAsync(true, includePorts: !Profile.IsConsole)).Processes,
                [new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks)]));
            Log($"{label} stopped.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            try
            {
                if (maintenanceSession is not null)
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
            finally { await RefreshCoreAsync(); }
        }
    }

    private Process StartCommand(string command, bool detectUrl, ApiLaunchConfiguration? configuration = null)
    {
        if (Profile.IsConsole) return StartConsoleCommand(command, isStart: detectUrl);
        var outputGeneration = Interlocked.Read(ref _outputGeneration);
        var frontendRunVersion = detectUrl && FrontendServiceSupport.IsFrontend(Profile)
            ? BeginFrontendRun() : (long?)null;
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
        process.OutputDataReceived += (_, e) => ReceiveOutput(e.Data, false, detectUrl, configuration, outputGeneration, frontendRunVersion);
        process.ErrorDataReceived += (_, e) => ReceiveOutput(e.Data, true, detectUrl, configuration, outputGeneration, frontendRunVersion);
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
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private Process StartConsoleCommand(string command, bool isStart)
    {
        Log($"> {command}", kind: ServiceLogKind.Command);
        var session = ConsoleProcessSession.Create(command, WorkingDirectory);
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
                    if (!ReferenceEquals(_consoleSession, session)) return;
                    if (!expected && code != 0) _lastError = $"Command exited with code {code}. See the output log.";
                    Log($"Console command exited with code {code}.", code != 0 && !expected,
                        code == 0 && !expected ? ServiceLogKind.Success : ServiceLogKind.Information);
                }
                catch (InvalidOperationException) { }
            };
            process.EnableRaisingEvents = true;
            session.Resume((line, error) => ReceiveOutput(line, error, detectUrl: false, configuration: null));
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
        long? outputGeneration = null, long? frontendRunVersion = null)
    {
        if (outputGeneration is not null && outputGeneration != Interlocked.Read(ref _outputGeneration)) return;
        if (frontendRunVersion is not null && frontendRunVersion != ManagedFrontendRunVersion) return;
        if (line is null) return;
        line = StripTerminalCodes(line);
        if (string.IsNullOrWhiteSpace(line)) return;
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
        IReadOnlyList<InspectedProcess> Owned, IReadOnlyList<ListeningPort> ReportedListeners);

    // The inventory cache may already be complete. Dispatch the whole inspection, including
    // native identity/job checks and ownership traversal, instead of resuming that work on WPF.
    // The caller holds _gate until this task finishes, preserving the operation's ownership scope.
    private Task<Inspection> InspectAsync(bool fresh = false) => Task.Run(async () =>
    {
        var inventory = await ProcessInspector.ReadAsync(fresh, includePorts: !Profile.IsConsole).ConfigureAwait(false);
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
        var processById = inventory.Processes.ToDictionary(p => p.Id);
        foreach (var member in consoleMembers)
            if (!processById.TryGetValue(member.Id, out var existing) || existing.Identity != member.Identity)
                processById[member.Id] = member;
        inventory = inventory with { Processes = processById.Values.ToArray() };
        var reportedListeners = inventory.ListeningPorts;
        // Process and TCP snapshots are taken separately, and a cached listener can outlive its
        // process. Drop only confirmed exits; an inaccessible listener must remain a blocker.
        var exitedListeners = inventory.ListeningPorts.Select(p => p.ProcessId).Distinct()
            .Where(ProcessInspector.IsDefinitelyStopped).ToHashSet();
        if (exitedListeners.Count > 0)
            inventory = inventory with { ListeningPorts = inventory.ListeningPorts
                .Where(p => !exitedListeners.Contains(p.ProcessId)).ToArray() };
        var ancestors = ProcessInspector.Ancestors(inventory.Processes);
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
        var external = inventory.Processes.Where(p => !ancestors.Contains(p.Id)
            && ProcessInspector.BelongsToDirectory(p, WorkingDirectory));
        var all = ProcessInspector.Descendants(inventory.Processes,
            external.Select(p => p.Identity).Concat(owned.Select(p => p.Identity)))
            .Where(p => !ancestors.Contains(p.Id) && ProcessInspector.IsSameProcess(p.Identity)).ToArray();
        return new Inspection(inventory, all, owned, reportedListeners);
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
        if (Profile.IsConsole)
        {
            if (ids.Length == 0 && _consoleSession is { OutputDrainAttempted: false } completedSession && completedSession.Process.HasExited)
                await DrainConsoleOutputAsync(completedSession);
            RefreshConsole(inspection, ids);
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
        try
        {
            var frontendAttempt = PendingFrontendReadiness(inspection, listeners);
            using var request = new HttpRequestMessage(HttpMethod.Get, uiUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var origin = inspection.Owned.Count > 0 ? "Started by launcher" : "Started outside launcher";
            var status = (int)response.StatusCode;
            Publish(ServiceState.Running, status >= 500 ? $"{origin}; HTTP {status} (server reports an error)" : $"{origin}; HTTP {status}", ids, uiUrl,
                status >= 500 ? ServiceLogKind.Warning : ServiceLogKind.Success);
            NotifyFrontendReady(uiUrl, status, frontendAttempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Publish(ServiceState.Starting, "Port is listening; waiting for an HTTP response", ids, uiUrl);
        }
    }

    private void RefreshConsole(Inspection inspection, int[] ids)
    {
        int? exitCode = null;
        if (!_consoleWasStopped && _consoleSession is { } session && session.Process.HasExited)
            exitCode = session.Process.ExitCode;
        if (ids.Length > 0)
        {
            var origin = inspection.Owned.Count > 0 ? "Started by launcher" : "Started outside launcher";
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
        try
        {
            var inspection = await InspectAsync(fresh: true);
            if (Profile.IsConsole && _consoleSession is { } session && _hasManagedProcess)
            {
                _consoleWasStopped = true;
                // Job membership covers children created between snapshots, with no path or PID-name guess.
                await Task.Run(session.Stop);
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
            if (targets.Count == 0 && conflictApproval is null)
            {
                // An explicit stop also acknowledges a previous failed start. A released port
                // must not leave a historical conflict/error on an otherwise stopped service.
                _lastError = inspection.Inventory.InspectionError;
                Log(Profile.IsConsole && _consoleWasStopped ? "Console command and child processes stopped."
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
            var stoppedIds = targets.Select(p => p.Id).ToHashSet();
            bool HasListeners(Inspection state) => !Profile.IsConsole && state.ReportedListeners
                .Any(p => stoppedIds.Contains(p.ProcessId) || p.Port == conflictApproval?.Port);
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
        // A new stop/restart clears earlier diagnostics, including an identical error from
        // the previous attempt. Emit that current failure once in the fresh console buffer.
        var repeatDiagnostic = _repeatDiagnosticAfterConsoleReset && state is ServiceState.Error or ServiceState.Conflict;
        if (previous.State == state && previous.Detail == detail && !repeatDiagnostic) return;
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
        CancelMaintenance();
        _consoleSession?.Dispose();
        _maintenanceSession?.Dispose();
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
        _logs.Writer.TryComplete();
    }
}
