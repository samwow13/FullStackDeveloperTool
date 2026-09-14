using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace FullStackLauncher.Services;

internal sealed record ProcessIdentity(int Id, long StartedUtcTicks);
internal sealed record InspectedProcess(int Id, int ParentId, string Name, string Executable,
    string CommandLine, long StartedUtcTicks)
{
    public ProcessIdentity Identity => new(Id, StartedUtcTicks);
}

internal sealed record ListeningPort(int Port, int ProcessId);
internal sealed record ProcessInventory(IReadOnlyList<InspectedProcess> Processes,
    IReadOnlyList<ListeningPort> ListeningPorts, string? InspectionError = null);

/// <summary>Read-only Windows inspection. Never uses a process name or a port as proof of ownership.</summary>
internal static class ProcessInspector
{
    private static readonly object CacheLock = new();
    private static Task<ProcessInventory>? _cached;
    private static DateTime _cacheAt;
    private static Task<ProcessInventory>? _processesCached;
    private static DateTime _processesCacheAt;

    public static Task<ProcessInventory> ReadAsync(bool fresh = false, bool includePorts = true)
    {
        lock (CacheLock)
        {
            if (!includePorts)
            {
                if (fresh || _processesCached is null || (_processesCached.IsCompleted
                    && DateTime.UtcNow - _processesCacheAt > TimeSpan.FromSeconds(1)))
                {
                    _processesCacheAt = DateTime.UtcNow;
                    _processesCached = Task.Run(() => Read(includePorts: false));
                }
                return _processesCached;
            }
            // Slow passive reads remain shared. Explicit operation checks still request a new
            // snapshot, so a pre-start/pre-stop inventory can never stand in for verification.
            if (fresh || _cached is null || (_cached.IsCompleted
                && DateTime.UtcNow - _cacheAt > TimeSpan.FromSeconds(1)))
            {
                _cacheAt = DateTime.UtcNow;
                _cached = Task.Run(() => Read(includePorts: true));
            }
            return _cached;
        }
    }

    public static bool IsSameProcess(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool IsDefinitelyStopped(int processId)
    {
        if (processId <= 4) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        // Inaccessible processes can still own listeners. Lack of permission is not an exit.
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static HashSet<int> Ancestors(IReadOnlyList<InspectedProcess> processes)
    {
        var result = new HashSet<int> { Environment.ProcessId };
        var byId = processes.ToDictionary(p => p.Id);
        var current = Environment.ProcessId;
        while (byId.TryGetValue(current, out var info) && info.ParentId > 0 && result.Add(info.ParentId))
            current = info.ParentId;
        return result;
    }

    public static IReadOnlyList<InspectedProcess> Descendants(IReadOnlyList<InspectedProcess> processes,
        IEnumerable<ProcessIdentity> roots)
    {
        var selected = new Dictionary<int, InspectedProcess>();
        var byId = processes.ToDictionary(p => p.Id);
        foreach (var root in roots)
            if (byId.TryGetValue(root.Id, out var actual) && actual.StartedUtcTicks == root.StartedUtcTicks)
                selected[actual.Id] = actual;
        bool changed;
        do
        {
            changed = false;
            foreach (var process in processes)
                if (!selected.ContainsKey(process.Id) && selected.TryGetValue(process.ParentId, out var parent)
                    && process.StartedUtcTicks >= parent.StartedUtcTicks)
                {
                    selected[process.Id] = process;
                    changed = true;
                }
        } while (changed);
        return selected.Values.ToArray();
    }

    public static bool BelongsToDirectory(InspectedProcess process, string directory)
    {
        // A shell may contain a project path merely because it launched this app. Never adopt it.
        var name = Path.GetFileNameWithoutExtension(process.Name);
        if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("conhost", StringComparison.OrdinalIgnoreCase)) return false;

        var prefix = Path.GetFullPath(directory).TrimEnd('\\', '/') + "\\";
        var executable = process.Executable.Replace('/', '\\');
        if (executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // Only runtimes are eligible through command-line evidence. Editors, terminals, and tools
        // with an arbitrary project-path argument are not application processes.
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("node", StringComparison.OrdinalIgnoreCase)) return false;
        var command = process.CommandLine.Replace('/', '\\');
        var index = command.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (index == 0 || char.IsWhiteSpace(command[index - 1]) || command[index - 1] is '"' or '\'' or '=')
                return true;
            index = command.IndexOf(prefix, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static ProcessInventory Read(bool includePorts)
    {
        var processes = new List<InspectedProcess>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1))
            return new(processes, [], "Windows could not inspect the process list.");
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            // One reusable native path buffer avoids allocating about 64 KB per process on
            // every status poll. Only the resulting executable strings belong to the snapshot.
            var path = new StringBuilder(32768);
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    var handle = OpenProcess(0x1000, false, entry.ProcessId);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        if (!GetProcessTimes(handle, out var created, out var exited, out _, out _) || exited != 0) continue;
                        long started;
                        try { started = DateTime.FromFileTimeUtc(created).Ticks; }
                        catch (ArgumentOutOfRangeException) { continue; }
                        path.Clear();
                        var length = path.Capacity;
                        var executable = QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : "";
                        var name = entry.ExeFile ?? "";
                        var commandLine = name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
                            ? ReadCommandLine(handle) : "";
                        processes.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, name,
                            executable, commandLine, started));
                    }
                    finally { CloseHandle(handle); }
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }

        if (!includePorts) return new(processes, []);
        var ports = new List<ListeningPort>();
        var ipv4 = ReadPorts(2, ports);
        var ipv6 = ReadPorts(23, ports);
        return new(processes, ports.Distinct().ToArray(),
            ipv4 is not null || ipv6 is not null ? string.Join(" ", new[] { ipv4, ipv6 }.Where(s => s is not null)) : null);
    }

    private static string ReadCommandLine(IntPtr handle)
    {
        NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var size);
        if (size <= 0 || size > 1024 * 1024) return "";
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, size, out _) != 0) return "";
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Length == 0 || offset < 0 || offset + value.Length > size) return "";
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? ReadPorts(int family, List<ListeningPort> ports)
    {
        var size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (error != 122 && error != 0) return $"TCP inspection failed ({error}).";
        if (size <= 0) return null;
        // The table can grow between calls; retry with the newly requested size.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (error == 122) continue;
                if (error != 0) return $"TCP inspection failed ({error}).";
                var count = Marshal.ReadInt32(buffer);
                var stride = family == 2 ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * stride);
                    var portOffset = family == 2 ? 8 : 20;
                    var pidOffset = family == 2 ? 20 : 52;
                    var port = unchecked((ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(row, portOffset)));
                    ports.Add(new(port, Marshal.ReadInt32(row, pidOffset)));
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return "TCP listener list changed too quickly to inspect.";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
