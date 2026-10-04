using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using FullStackLauncher.CodexMonitor;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

/// <summary>Keeps inherited output pipes readable after the dashboard exits, without storing raw output.</summary>
internal static class ServiceOutputDrain
{
    private enum StartupStage { Starting, OpeningStatus, OpeningEvent, ValidatingHandles, VerifyingParent, Ready, OpeningStreams, Draining, Completed }
    private const int StatusCapacity = 16;

    internal static async Task<int> RunAsync(string[] args)
    {
        string Value(string key)
        {
            var index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Incomplete output drain arguments.");
            return args[index + 1];
        }
        SafeFileHandle Handle(string key) => new(new IntPtr(long.Parse(Value(key), CultureInfo.InvariantCulture)), ownsHandle: true);
        var stage = StartupStage.Starting;
        MemoryMappedFile? status = null;
        MemoryMappedViewAccessor? state = null;
        EventWaitHandle? ready = null;
        Process? parent = null;
        try
        {
            var readyName = Value("--ready-event");
            stage = StartupStage.OpeningStatus;
            status = MemoryMappedFile.OpenExisting(readyName + ".Status", MemoryMappedFileRights.ReadWrite);
            state = status.CreateViewAccessor(0, StatusCapacity);
            void SetStage(StartupStage next)
            {
                stage = next;
                state.Write(0, (int)next);
            }
            SetStage(StartupStage.OpeningEvent);
            ready = EventWaitHandle.OpenExisting(readyName);
            SetStage(StartupStage.ValidatingHandles);
            using var outputHandle = Handle("--stdout-handle");
            using var errorHandle = Handle("--stderr-handle");
            using var job = args.Contains("--job-handle", StringComparer.Ordinal)
                ? Handle("--job-handle") : null;
            foreach (var handle in new[] { outputHandle, errorHandle, job }.OfType<SafeFileHandle>())
            {
                if (handle.IsInvalid) throw new Win32Exception(6, "An inherited service handle is invalid.");
                if (!GetHandleInformation(handle, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "An inherited service handle could not be verified.");
            }
            SetStage(StartupStage.VerifyingParent);
            var parentId = int.Parse(Value("--parent-pid"), CultureInfo.InvariantCulture);
            var parentTicks = long.Parse(Value("--parent-start"), CultureInfo.InvariantCulture);
            try
            {
                parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentTicks)
                {
                    parent.Dispose();
                    parent = null;
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { parent?.Dispose(); parent = null; }
            SetStage(StartupStage.Ready);
            ready.Set();
            // Preparation may be canceled. Do not consume dashboard output while its process lives.
            if (parent is not null) await parent.WaitForExitAsync().ConfigureAwait(false);
            // Duplicate handles share the synchronous pipe file object. FileStream's constructor
            // queries its I/O mode, which can wait behind the dashboard's pending pipe read.
            // Never construct these streams before the dashboard exits and releases that read.
            SetStage(StartupStage.OpeningStreams);
            using var output = new FileStream(outputHandle, FileAccess.Read);
            using var error = new FileStream(errorHandle, FileAccess.Read);
            SetStage(StartupStage.Draining);
            await Task.WhenAll(DrainAsync(output), DrainAsync(error), WaitForJobAsync(job)).ConfigureAwait(false);
            SetStage(StartupStage.Completed);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // This transient shared-memory handshake contains fixed stages and numeric codes only.
            // Do not expose exception messages, arguments, paths, output, or configuration values.
            try
            {
                state?.Write(4, ex.HResult);
                state?.Write(8, ex is Win32Exception native ? native.NativeErrorCode : 0);
                state?.Write(0, -(int)stage);
                ready?.Set();
            }
            catch (Exception reporting) when (reporting is not OutOfMemoryException) { }
            return 1;
        }
        finally
        {
            parent?.Dispose();
            ready?.Dispose();
            state?.Dispose();
            status?.Dispose();
        }
    }

    private static async Task DrainAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (IOException) { }
    }

    private static async Task WaitForJobAsync(SafeFileHandle? job)
    {
        if (job is null) return;
        while (true)
        {
            try { if (ConsoleProcessSession.ReadJobProcesses(job).Count == 0) return; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // An inspection failure is not proof of completion. Keep the retained job alive.
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    internal static Process Prepare(SafeFileHandle output, SafeFileHandle error, SafeFileHandle? job = null)
    {
        using var ownProcess = Process.GetCurrentProcess();
        var readyName = "Local\\FullStackLauncher.Output." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var status = MemoryMappedFile.CreateNew(readyName + ".Status", StatusCapacity);
        using var state = status.CreateViewAccessor(0, StatusCapacity);
        var duplicates = new List<SafeFileHandle>();
        var attributes = IntPtr.Zero;
        var inherited = IntPtr.Zero;
        var initialized = false;
        var created = new ProcessInformation();
        Process? helper = null;
        try
        {
            foreach (var source in new[] { output, error, job }.OfType<SafeFileHandle>())
            {
                if (!DuplicateHandle(ownProcess.Handle, source, ownProcess.Handle, out var duplicate, 0, true, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not retain service output handles.");
                duplicates.Add(duplicate);
            }
            var args = new List<string> { "--service-output-drain", "--parent-pid", ownProcess.Id.ToString(CultureInfo.InvariantCulture),
                "--parent-start", ownProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                "--stdout-handle", duplicates[0].DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture),
                "--stderr-handle", duplicates[1].DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture),
                "--ready-event", readyName };
            if (job is not null) args.AddRange(["--job-handle", duplicates[2].DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)]);
            var start = MonitorRuntime.CreateStart(args.ToArray());
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            inherited = Marshal.AllocHGlobal(IntPtr.Size * duplicates.Count);
            for (var index = 0; index < duplicates.Count; index++)
                Marshal.WriteIntPtr(inherited, index * IntPtr.Size, duplicates[index].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), inherited,
                new IntPtr(IntPtr.Size * duplicates.Count), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInformationEx
            {
                Startup = new StartupInformation { Size = Marshal.SizeOf<StartupInformationEx>(), Flags = 1, ShowWindow = 0 },
                AttributeList = attributes
            };
            var command = new StringBuilder(Quote(start.FileName));
            foreach (var argument in start.ArgumentList) command.Append(' ').Append(Quote(argument));
            if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000,
                IntPtr.Zero, start.WorkingDirectory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not prepare service output retention.");
            helper = Process.GetProcessById(created.ProcessId);
            _ = helper.Handle;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                var signaled = ready.WaitOne(100);
                var recordedStage = state.ReadInt32(0);
                if (recordedStage < 0 || helper.HasExited || DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException(StartupFailure(state, helper));
                if (signaled && recordedStage == (int)StartupStage.Ready) break;
            }
            var result = helper;
            helper = null;
            return result;
        }
        finally
        {
            if (helper is not null)
            {
                try { if (!helper.HasExited) helper.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                helper.Dispose();
            }
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
            foreach (var duplicate in duplicates) duplicate.Dispose();
        }
    }

    private static string StartupFailure(MemoryMappedViewAccessor state, Process helper)
    {
        var recorded = state.ReadInt32(0);
        var stage = Math.Abs(recorded) switch
        {
            (int)StartupStage.OpeningStatus => "opening status channel",
            (int)StartupStage.OpeningEvent => "opening readiness signal",
            (int)StartupStage.ValidatingHandles => "validating inherited service handles",
            (int)StartupStage.VerifyingParent => "verifying launcher identity",
            (int)StartupStage.Ready => "confirming helper readiness",
            _ => "initializing helper"
        };
        var detail = recorded < 0 ? $"failed while {stage}" : helper.HasExited
            ? $"exited while {stage} (exit code {helper.ExitCode})" : $"timed out while {stage}";
        var error = state.ReadInt32(4);
        var native = state.ReadInt32(8);
        if (error != 0) detail += $"; HRESULT 0x{unchecked((uint)error):X8}";
        if (native != 0) detail += $"; Windows error {native}";
        return $"Service output retention {detail}. The launcher remains open; services have not been detached.";
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInformationEx { public StartupInformation Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInformationEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
