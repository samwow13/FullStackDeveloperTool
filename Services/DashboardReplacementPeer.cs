using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>Verifies the actual current-user pipe peer and pins its process identity.</summary>
internal static class DashboardReplacementPeer
{
    internal static Process VerifyAndPin(SafePipeHandle pipe, int? processId, long? startedAtUtcTicks)
    {
        if (processId is null or <= 4 || startedAtUtcTicks is null or <= 0 ||
            !GetNamedPipeClientProcessId(pipe, out var peer) || peer != (uint)processId.Value)
            throw new IOException("Launcher replacement peer could not be verified. Existing dashboard and work were preserved.");
        return Pin(processId.Value, startedAtUtcTicks.Value);
    }

    internal static Process Pin(int processId, long startedAtUtcTicks)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            _ = process.Handle;
            using var current = Process.GetCurrentProcess();
            if (processId <= 4 || process.SessionId != current.SessionId ||
                process.StartTime.ToUniversalTime().Ticks != startedAtUtcTicks || process.HasExited)
                throw new IOException("Launcher replacement process is no longer available. Existing dashboard and work were preserved.");
            var result = process;
            process = null;
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or System.Security.SecurityException)
        {
            throw new IOException("Launcher replacement process could not be verified. Existing dashboard and work were preserved.", ex);
        }
        finally { process?.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
