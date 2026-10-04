using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

internal static partial class CodexResetService
{
    private sealed record ResetInventory(IReadOnlyList<CodexResetProcess> Processes,
        IReadOnlyDictionary<int, int> Parents, IReadOnlyList<string> Issues);

    private static int CurrentSessionId()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((int)session);
    }

    private static ResetInventory Capture(int sessionId, CancellationToken token)
    {
        var processes = new List<CodexResetProcess>();
        var parents = new Dictionary<int, int>();
        var issues = new List<string>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            return new(processes, parents, [$"Windows could not inspect processes. Windows error {Marshal.GetLastWin32Error()}."]);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var path = new StringBuilder(32768);
        if (!Process32First(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 18) issues.Add($"Windows could not inspect processes. Windows error {error}.");
            return new(processes, parents, issues);
        }
        do
        {
            token.ThrowIfCancellationRequested();
            parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            if (!IsTargetName(entry.ExecutableName ?? "") || entry.ProcessId == Environment.ProcessId) continue;
            if (!ProcessIdToSessionId(entry.ProcessId, out var session))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 87) issues.Add($"Cannot inspect the Windows session for {entry.ExecutableName} (PID {entry.ProcessId}). Windows error {error}.");
                continue;
            }
            if (session != sessionId) continue;
            using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, entry.ProcessId);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 87) issues.Add($"Cannot inspect {entry.ExecutableName} (PID {entry.ProcessId}). Windows error {error}.");
                continue;
            }
            if (!GetProcessTimes(handle, out var creation, out var exited, out _, out _))
            {
                issues.Add($"Cannot verify the process identity for {entry.ExecutableName} (PID {entry.ProcessId}). Windows error {Marshal.GetLastWin32Error()}.");
                continue;
            }
            if (exited != 0 || WaitForSingleObject(handle, 0) == WaitSignaled) continue;
            path.Clear();
            var length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length))
            {
                issues.Add($"Cannot verify the executable for {entry.ExecutableName} (PID {entry.ProcessId}). Windows error {Marshal.GetLastWin32Error()}.");
                continue;
            }
            var executable = path.ToString();
            if (!IsTargetName(System.IO.Path.GetFileName(executable)))
            {
                issues.Add($"The executable identity changed for {entry.ExecutableName} (PID {entry.ProcessId}). Refresh before resetting.");
                continue;
            }
            processes.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId,
                entry.ExecutableName!, executable, creation));
        } while (Process32Next(snapshot, ref entry));
        var enumerationError = Marshal.GetLastWin32Error();
        if (enumerationError != 18)
            issues.Add($"Windows process inspection was incomplete. Windows error {enumerationError}.");
        return new(processes, parents, issues);
    }

    private static bool Matches(SafeProcessHandle handle, CodexResetProcess process)
    {
        if (!GetProcessTimes(handle, out var created, out var exited, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (exited != 0 || created != process.CreationTime) return false;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return PathsEqual(path.ToString(), process.Executable);
    }

    private static string? ReadPackageFullName(SafeProcessHandle handle)
    {
        uint length = 0;
        var error = GetPackageFullName(handle, ref length, null);
        if (error == NoPackage) return null;
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var buffer = new StringBuilder((int)length);
        error = GetPackageFullName(handle, ref length, buffer);
        if (error != 0) throw new Win32Exception(error);
        return buffer.ToString();
    }

    private static string PackageFamily(string packageFullName)
    {
        uint length = 0;
        var error = PackageFamilyNameFromFullName(packageFullName, ref length, null);
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var buffer = new StringBuilder((int)length);
        error = PackageFamilyNameFromFullName(packageFullName, ref length, buffer);
        if (error != 0) throw new Win32Exception(error);
        return buffer.ToString();
    }

    private static string PackagePath(string packageFullName)
    {
        uint length = 0;
        var error = GetPackagePathByFullName(packageFullName, ref length, null);
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var buffer = new StringBuilder((int)length);
        error = GetPackagePathByFullName(packageFullName, ref length, buffer);
        if (error != 0) throw new Win32Exception(error);
        return buffer.ToString();
    }

    private static IReadOnlyList<string> RegisteredPackages(string family)
    {
        uint count = 0;
        uint length = 0;
        var error = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (error == 0 && count == 0) return [];
        if (error != InsufficientBuffer || count is 0 or > 128 || length is 0 or > 1024 * 1024)
            throw new Win32Exception(error);
        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            error = GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
            if (error != 0) throw new Win32Exception(error);
            var result = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, checked(index * IntPtr.Size)));
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void AddLauncherJobIssue(ResetInventory inventory, List<string> issues)
    {
        var ancestors = new HashSet<int>();
        var current = Environment.ProcessId;
        while (inventory.Parents.TryGetValue(current, out var parent) && parent > 0 && ancestors.Add(parent)) current = parent;
        var candidateAncestors = inventory.Processes.Where(process => ancestors.Contains(process.Id)).ToArray();
        if (candidateAncestors.Length == 0) return;

        // Ordinary single-process termination preserves child processes. A shared job with
        // kill-on-close is different: ending an ancestor may close the launcher-owning job.
        // Parentage alone does not block reset; require actual job evidence. A job-owning
        // ancestor can hold the last job handle without being a member of that job.
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob))
        {
            issues.Add("Cannot verify launcher job protection. Reopen the launcher from File Explorer, then retry.");
            return;
        }
        if (!inJob) return;
        var limits = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimitInformation>());
        try
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), out _))
            {
                issues.Add("Cannot inspect launcher job limits. Reopen the launcher from File Explorer, then retry.");
                return;
            }
            if ((Marshal.PtrToStructure<ExtendedLimitInformation>(limits).Basic.LimitFlags & 0x2000) == 0) return;
            issues.Add("Stopping a Codex ancestor could close this launcher's kill-on-close job. Reopen the launcher from File Explorer, then retry.");
        }
        catch (Win32Exception)
        {
            issues.Add("Cannot inspect launcher job membership. Reopen the launcher from File Explorer, then retry.");
        }
        finally { Marshal.FreeHGlobal(limits); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr items, out uint processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder? packageFullName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder? family);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint length, StringBuilder? path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr packageFullNames, ref uint length, IntPtr buffer);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length, out uint returnedLength);
}
