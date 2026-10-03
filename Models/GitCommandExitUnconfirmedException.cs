using System.Diagnostics;

namespace FullStackLauncher.Models;

/// <summary>Cancellation whose owned Git process has not been confirmed stopped.</summary>
public sealed class GitCommandExitUnconfirmedException(string message, int processId, DateTime? startedAt,
    CancellationToken token) : OperationCanceledException(message, token)
{
    public bool IsExitConfirmed()
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return true;
            // A reused PID belongs to another process; never terminate it.
            return startedAt is { } captured && process.StartTime.ToUniversalTime() != captured;
        }
        catch (ArgumentException) { return true; }
        catch (Exception) { return false; }
    }
}
