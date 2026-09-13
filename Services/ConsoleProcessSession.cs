using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>
/// Owns one console command and its inherited child processes, including executables outside the
/// project folder. The shell remains suspended until it belongs to the job, so even a short-lived
/// command cannot leave an unobserved child behind before ownership is established.
/// </summary>
internal sealed class ConsoleProcessSession : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _thread;
    private readonly StreamReader _output;
    private readonly StreamReader _error;
    private Task _outputCompletion = Task.CompletedTask;
    private bool _disposed;

    private ConsoleProcessSession(Process process, SafeFileHandle job, SafeFileHandle thread,
        SafeFileHandle output, SafeFileHandle error)
    {
        Process = process;
        _job = job;
        _thread = thread;
        _output = new(new FileStream(output, FileAccess.Read));
        _error = new(new FileStream(error, FileAccess.Read));
    }

    public Process Process { get; }
    public Task OutputCompletion => _outputCompletion;
    public bool OutputDrainAttempted { get; set; }

    public static ConsoleProcessSession Create(string command, string directory)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create console process supervision.");
        SafeFileHandle? thread = null;
        SafeFileHandle? outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, inputRead = null, inputWrite = null;
        Process? process = null;
        var attributes = IntPtr.Zero;
        var inheritedHandles = IntPtr.Zero;
        var attributesInitialized = false;
        var created = new ProcessInformation();
        try
        {
            var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the console process tree.");
            CreateRedirectedPipe(out outputRead, out outputWrite, childReads: false);
            CreateRedirectedPipe(out errorRead, out errorWrite, childReads: false);
            CreateRedirectedPipe(out inputRead, out inputWrite, childReads: true);

            // Inherit only this command's streams, never unrelated handles from the dashboard.
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            attributesInitialized = true;
            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inheritedHandles, 0, inputRead.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), inheritedHandles,
                new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInformationEx
            {
                Startup = new StartupInformation
                {
                    Size = Marshal.SizeOf<StartupInformationEx>(), Flags = 0x101, ShowWindow = 0,
                    StandardInput = inputRead.DangerousGetHandle(),
                    StandardOutput = outputWrite.DangerousGetHandle(), StandardError = errorWrite.DangerousGetHandle()
                },
                AttributeList = attributes
            };
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var arguments = new StringBuilder("\"" + executable + "\" /d /s /c \"" + command + "\"");
            if (!CreateProcess(executable, arguments, IntPtr.Zero, IntPtr.Zero, true,
                0x08080004, IntPtr.Zero, directory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start the console command.");
            thread = new SafeFileHandle(created.Thread, ownsHandle: true);
            created.Thread = IntPtr.Zero;
            if (!AssignProcessToJobObject(job, created.Process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not attach console process supervision; the command was not run.");

            process = Process.GetProcessById(created.ProcessId);
            _ = process.Handle; // Open the managed handle while the suspended process is still present.
            var session = new ConsoleProcessSession(process, job, thread, outputRead, errorRead);
            process = null;
            thread = null;
            outputRead = errorRead = null;
            return session;
        }
        catch
        {
            // A failure must never release the suspended shell to execute an unsupervised command.
            if (created.Process != IntPtr.Zero) TerminateProcess(created.Process, 1);
            job.Dispose();
            process?.Dispose();
            throw;
        }
        finally
        {
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            thread?.Dispose();
            outputRead?.Dispose(); outputWrite?.Dispose();
            errorRead?.Dispose(); errorWrite?.Dispose();
            inputRead?.Dispose(); inputWrite?.Dispose(); // Console commands receive EOF, like other launcher commands.
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
        }
    }

    public void Resume(Action<string, bool> receiveOutput)
    {
        _outputCompletion = Task.WhenAll(ReadOutputAsync(_output, false, receiveOutput),
            ReadOutputAsync(_error, true, receiveOutput));
        if (ResumeThread(_thread) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not resume the console command.");
        _thread.Dispose();
    }

    public IReadOnlyList<InspectedProcess> ReadProcesses()
    {
        var result = new List<InspectedProcess>();
        foreach (var id in ReadProcessIds())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!IsProcessInJob(process.SafeHandle, _job, out var belongs))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not verify a console child process.");
                // A PID alone is insufficient: membership is checked on the opened process handle.
                if (!belongs || process.HasExited) continue;
                result.Add(new(id, 0, process.ProcessName, "", "", process.StartTime.ToUniversalTime().Ticks));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        if (result.Count == 0 && ReadProcessIds().Count > 0)
            throw new InvalidOperationException("Console child processes changed during inspection; retry the operation.");
        return result;
    }

    private IReadOnlyList<int> ReadProcessIds()
    {
        var capacity = 32;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var size = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_job, 3, buffer, size, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 234) { capacity = checked(capacity * 4); continue; }
                    throw new Win32Exception(error, "Windows could not inspect console child processes.");
                }
                var assigned = Marshal.ReadInt32(buffer);
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > capacity) throw new InvalidOperationException("Windows returned an invalid console process list.");
                if (assigned > count) { capacity = Math.Max(capacity * 4, assigned); continue; }
                var ids = new List<int>(count);
                for (var index = 0; index < count; index++)
                {
                    var id = Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64();
                    if (id > 4 && id <= int.MaxValue) ids.Add((int)id);
                }
                return ids;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Console process membership changed too quickly to inspect.");
    }

    public void Stop()
    {
        if (!TerminateJobObject(_job, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not stop the console process tree.");
    }

    private static async Task ReadOutputAsync(StreamReader reader, bool error, Action<string, bool> receiveOutput)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) receiveOutput(line, error);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static void CreateRedirectedPipe(out SafeFileHandle read, out SafeFileHandle write, bool childReads)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out read, out write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetHandleInformation(childReads ? write : read, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing the last job handle terminates only this run's members, even if the parent exited.
        _job.Dispose();
        _thread.Dispose();
        _output.Dispose();
        _error.Dispose();
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx { public StartupInformation Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessUserTimeLimit, JobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInformationEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
