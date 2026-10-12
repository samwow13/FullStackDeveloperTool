using System.ComponentModel;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public sealed partial class ServiceRunner
{
    // Separate from console generations: console history survives retries, and automatic
    // dependency installation and application start can share the same output generation.
    private sealed class CommandAttempt(bool isStart, int stopRevision)
    {
        internal readonly ServiceDependencyDiagnostics Diagnostics = new();
        internal readonly bool IsStart = isStart;
        internal readonly int StopRevision = stopRevision;
        internal string? Failure;
        internal bool StartupDiagnosticsComplete;
    }

    private CommandAttempt? _commandAttempt;

    private CommandAttempt BeginCommandDiagnostics(bool isStart)
    {
        lock (_consoleLogSync)
            return _commandAttempt = new(isStart, Volatile.Read(ref _stopRevision));
    }

    private bool IsCurrentCommand(CommandAttempt? attempt) => attempt is not null &&
        ReferenceEquals(attempt, _commandAttempt) && attempt.StopRevision == Volatile.Read(ref _stopRevision) && !_disposed;

    private bool IsLatestCommand(CommandAttempt? attempt) => attempt is not null &&
        ReferenceEquals(attempt, _commandAttempt) && !_disposed;

    private void ClearCommandDiagnostics()
    {
        lock (_consoleLogSync) _commandAttempt = null;
    }

    private void ObserveCommandOutput(CommandAttempt? attempt, string redactedLine)
    {
        lock (_consoleLogSync)
            if (IsCurrentCommand(attempt) && !attempt!.StartupDiagnosticsComplete) attempt.Diagnostics.Observe(redactedLine);
    }

    private void CompleteStartupDiagnostics()
    {
        lock (_consoleLogSync)
        {
            if (!IsLatestCommand(_commandAttempt)) return;
            _commandAttempt!.StartupDiagnosticsComplete = true;
            // Keep the attempt identity so a later nonzero exit still reports an error.
        }
    }

    private void RecordCommandFailure(CommandAttempt? attempt, string failure)
    {
        lock (_consoleLogSync)
        {
            // Canceled restart preflight or a failed stop can advance the stop revision
            // while leaving this command alive. Its eventual exit must still be reported.
            if (!IsLatestCommand(attempt)) return;
            attempt!.Failure = failure;
            _lastError = failure;
        }
    }

    private void ObserveCommandLaunchFailure(CommandAttempt attempt, string executable, Exception exception)
    {
        // A missing working folder is not evidence of a missing runtime. Only Windows'
        // executable-not-found result is eligible, and the detector allowlists tool names.
        if (exception is not Win32Exception { NativeErrorCode: 2 }) return;
        lock (_consoleLogSync)
        {
            if (!IsCurrentCommand(attempt)) return;
            attempt.Diagnostics.ObserveMissingExecutable(executable);
            if (attempt.Diagnostics.Guidance is null) return;
            RecordCommandFailure(attempt, SensitiveDataProtection.Redact(_launchConfiguration?.Redact(exception.Message) ?? exception.Message));
        }
    }

    private string CommandFailureDetail(string detail)
    {
        lock (_consoleLogSync)
            return IsCurrentCommand(_commandAttempt) && !_commandAttempt!.StartupDiagnosticsComplete && _commandAttempt.Failure == detail &&
                _commandAttempt.Diagnostics.Guidance is { } guidance
                ? $"{guidance} {detail}" : detail;
    }

    private string? PendingStartupDependencyFailure()
    {
        lock (_consoleLogSync)
            return IsCurrentCommand(_commandAttempt) && _commandAttempt!.IsStart && !_commandAttempt.StartupDiagnosticsComplete
                ? _commandAttempt.Diagnostics.Guidance : null;
    }

    private async Task DrainCapturedOutputAsync(ServiceOutputCapture capture)
    {
        capture.OutputDrainAttempted = true;
        try { await capture.OutputCompletion.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            Log("Service output is still draining; some final lines may arrive after the command status.", kind: ServiceLogKind.Warning);
        }
    }
}
