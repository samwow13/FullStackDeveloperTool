using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>
/// Observes named open SQLite files in already verified Flutter app processes.
/// Call only from a worker thread. PSS captures handle metadata without cloning the
/// app, duplicating its handles, reading its memory, or changing its file position.
/// Unsupported or inaccessible snapshots yield no connection evidence.
/// </summary>
internal static class FlutterSqliteConnectionProbe
{
    private const int MaximumProcesses = 2;
    private const uint MaximumHandles = 2048;
    private const int MaximumPaths = 32;
    private static readonly TimeSpan WorkLimit = TimeSpan.FromSeconds(2);

    internal static IReadOnlyList<string> ReadOpenPaths(IReadOnlyList<ProcessIdentity> identities,
        CancellationToken cancellationToken = default)
    {
        // This layout supports 64-bit Windows. Other architectures keep ordinary
        // file discovery rather than risking an incompatible native structure.
        if (IntPtr.Size != 8 || !OperatingSystem.IsWindowsVersionAtLeast(6, 3) || cancellationToken.IsCancellationRequested ||
            identities.Count == 0) return [];
        var elapsed = Stopwatch.StartNew();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var volumes = ReadVolumeMappings();
            foreach (var identity in identities.Distinct().Take(MaximumProcesses))
            {
                if (cancellationToken.IsCancellationRequested || elapsed.Elapsed >= WorkLimit) break;
                if (identity.Id <= 4 || identity.StartedUtcTicks <= 0) continue;
                // QUERY_INFORMATION | DUP_HANDLE permits the documented PSS handle
                // snapshot. No debug privilege, VM access, or termination right.
                using var process = OpenProcess(0x0440, false, (uint)identity.Id);
                if (process.IsInvalid || !IsSameProcess(process, identity) ||
                    !GetProcessHandleCount(process, out var handleCount) || handleCount > MaximumHandles) continue;
                var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IntPtr snapshot = IntPtr.Zero, marker = IntPtr.Zero;
                try
                {
                    // HANDLES | HANDLE_NAME_INFORMATION only. Never capture VA,
                    // threads, contexts, or a clone of the target process.
                    if (PssCaptureSnapshot(process, 0x0000000C, 0, out snapshot) != 0 || snapshot == IntPtr.Zero ||
                        PssWalkMarkerCreate(IntPtr.Zero, out marker) != 0 || marker == IntPtr.Zero) continue;
                    var size = (uint)Marshal.SizeOf<PssHandleEntry>();
                    for (uint entryIndex = 0; entryIndex < MaximumHandles; entryIndex++)
                    {
                        if (cancellationToken.IsCancellationRequested || elapsed.Elapsed >= WorkLimit ||
                            observed.Count >= MaximumPaths) break;
                        // PSS_WALK_HANDLES. PSS owns the returned strings until the
                        // walk marker is freed; copy them while that marker is alive.
                        if (PssWalkSnapshot(snapshot, 2, marker, out var entry, size) != 0) break;
                        if ((entry.Flags & 0x02) == 0 || ReadName(entry.TypeName, entry.TypeNameLength, 16) != "File") continue;
                        var objectName = ReadName(entry.ObjectName, entry.ObjectNameLength, 4096);
                        var path = ToLocalPath(objectName, volumes);
                        if (path is null || !IsDatabasePath(path) || !FlutterSqliteDiscovery.IsAvailable(path)) continue;
                        observed.Add(path);
                    }
                    // PID reuse and exited apps never contribute connection evidence.
                    if (!cancellationToken.IsCancellationRequested && IsSameProcess(process, identity))
                        foreach (var path in observed)
                            if (paths.Count < MaximumPaths) paths.Add(path);
                }
                finally
                {
                    if (marker != IntPtr.Zero) PssWalkMarkerFree(marker);
                    if (snapshot != IntPtr.Zero) PssFreeSnapshot(GetCurrentProcess(), snapshot);
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Passive status must still work when PSS or local paths are unavailable.
            return [];
        }
        return cancellationToken.IsCancellationRequested ? [] : paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsSameProcess(SafeProcessHandle process, ProcessIdentity identity)
    {
        if (!GetProcessTimes(process, out var created, out var exited, out _, out _) || exited != 0) return false;
        try { return DateTime.FromFileTimeUtc(created).Ticks == identity.StartedUtcTicks; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static string? ReadName(IntPtr pointer, ushort byteLength, int maximumCharacters) =>
        pointer == IntPtr.Zero || byteLength == 0 || (byteLength & 1) != 0 || byteLength / 2 > maximumCharacters
            ? null : Marshal.PtrToStringUni(pointer, byteLength / 2);

    private static List<(string Device, string Drive)> ReadVolumeMappings()
    {
        var mappings = new List<(string Device, string Drive)>();
        var buffer = new char[4096];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = letter + ":";
            if (QueryDosDevice(drive, buffer, (uint)buffer.Length) == 0) continue;
            var end = Array.IndexOf(buffer, '\0');
            if (end <= 0) continue;
            // Only current local disk mappings. Network/device/pipe names never
            // become filesystem paths or trigger a remote filesystem request.
            var device = new string(buffer, 0, end);
            if (device.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase))
                mappings.Add((device, drive));
        }
        return mappings.OrderByDescending(mapping => mapping.Device.Length).ToList();
    }

    private static string? ToLocalPath(string? objectName, IReadOnlyList<(string Device, string Drive)> mappings)
    {
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Any(char.IsControl)) return null;
        foreach (var (device, drive) in mappings)
            if (objectName.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(drive + objectName[device.Length..]);
        return null;
    }

    private static bool IsDatabasePath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".db" or ".sqlite" or ".sqlite3";

    // PSS_HANDLE_ENTRY layout from processsnapshot.h. The largest union member is
    // Thread (48 bytes on 64-bit Windows); it supplies the native union's size and
    // alignment. Only the name metadata is consumed here.
    [StructLayout(LayoutKind.Sequential)]
    private struct PssHandleEntry
    {
        public IntPtr Handle;
        public uint Flags, ObjectType;
        public long CaptureTime;
        public uint Attributes, GrantedAccess, HandleCount, PointerCount, PagedPoolCharge, NonPagedPoolCharge;
        public long CreationTime;
        public ushort TypeNameLength;
        public IntPtr TypeName;
        public ushort ObjectNameLength;
        public IntPtr ObjectName;
        public PssThreadInformation TypeSpecificInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PssThreadInformation
    {
        public uint ExitStatus;
        public IntPtr TebBaseAddress;
        public uint ProcessId, ThreadId;
        public UIntPtr AffinityMask;
        public int Priority, BasePriority;
        public IntPtr Win32StartAddress;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(SafeProcessHandle process, out uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
    private static extern uint QueryDosDevice(string device, [Out] char[] target, uint length);
    [DllImport("kernel32.dll")]
    private static extern uint PssCaptureSnapshot(SafeProcessHandle process, uint captureFlags, uint threadContextFlags, out IntPtr snapshot);
    [DllImport("kernel32.dll")]
    private static extern uint PssWalkMarkerCreate(IntPtr allocator, out IntPtr marker);
    [DllImport("kernel32.dll")]
    private static extern uint PssWalkSnapshot(IntPtr snapshot, uint informationClass, IntPtr marker, out PssHandleEntry buffer, uint length);
    [DllImport("kernel32.dll")]
    private static extern uint PssWalkMarkerFree(IntPtr marker);
    [DllImport("kernel32.dll")]
    private static extern uint PssFreeSnapshot(IntPtr process, IntPtr snapshot);
}
