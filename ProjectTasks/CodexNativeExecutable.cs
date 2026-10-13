using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Finds an installed native CLI without executing or reading package-manager shims.</summary>
internal static class CodexNativeExecutable
{
    // Package identity is read only to verify a native CLI's desktop parent.
    // Packaged resources can be readable yet reject an unpackaged process launch.
    private const string DesktopPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;
    private const int InsufficientBuffer = 122;
    private const int NoPackage = 15700;

    internal static string Find()
    {
        // A separate, older npm CLI may coexist with the desktop. Prefer the
        // desktop's executable extraction without launching protected package files.
        if (RunningDesktopExecutable() is { } runningDesktop)
            return runningDesktop;
        if (ExtractedDesktopExecutable() is { } extractedDesktop)
            return extractedDesktop;
        if (UnpackagedDesktopExecutable() is { } unpackagedDesktop)
            return unpackagedDesktop;

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => FullyQualifiedDirectory(directory.Trim().Trim('"')))
            .Where(directory => directory is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Preserve PATH ordering among standalone CLI/npm fallbacks. Resolve only
        // direct native files and official package layouts, never shell shims.
        foreach (var directory in pathDirectories)
        {
            if (ExistingNativeFile(directory, "codex.exe") is { } executable)
                return executable;
            // Resolve only official package layouts; never parse or execute shims.
            if (NpmExecutableIn(directory) is { } npmExecutable)
                return npmExecutable;
        }

        throw new InvalidOperationException(CodexAppServerConnection.NotFoundMessage);
    }

    private static string? ExtractedDesktopExecutable()
    {
        var bin = ExtractedDesktopBin;
        try
        {
            var candidates = new List<FileInfo>();
            if (ExistingNativeFile(bin, "codex.exe") is { } direct)
                candidates.Add(new FileInfo(direct));
            if (Directory.Exists(bin))
                foreach (var directory in new DirectoryInfo(bin).EnumerateDirectories()
                    .Take(128))
                    if (ExistingNativeFile(directory.FullName, "codex.exe") is { } executable)
                        candidates.Add(new FileInfo(executable));
            return candidates.OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Directory?.LastWriteTimeUtc).Select(file => file.FullName).FirstOrDefault();
        }
        catch (Exception ex) when (IsPathFailure(ex)) { }
        return null;
    }

    private static string? RunningDesktopExecutable()
    {
        try
        {
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId)) return null;
            var capturedBefore = DateTime.UtcNow.ToFileTimeUtc();
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) return null;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            var candidates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var inspected = 0;
            do
            {
                if (++inspected > 32768) return null;
                if (!string.Equals(entry.ExecutableName, "codex.exe", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!ProcessIdToSessionId(entry.ProcessId, out var childSession) || childSession != sessionId)
                        continue;
                    using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, entry.ProcessId);
                    if (handle.IsInvalid || WaitForSingleObject(handle, 0) != WaitTimeout) continue;
                    if (!GetProcessTimes(handle, out var childCreated, out var childExited, out _, out _)
                        || childExited != 0 || childCreated > capturedBefore)
                        continue;
                    var executable = ReadExecutable(handle);
                    if (!IsExtractedDesktopExecutable(executable)
                        || ReadExecutableSubsystem(executable) != 3)
                        continue;
                    // A Launcher-owned app-server is not proof of the desktop's
                    // current runtime. Require a live, verified direct desktop parent.
                    if (!ProcessIdToSessionId(entry.ParentProcessId, out var parentSession) || parentSession != sessionId)
                        continue;
                    using var parent = OpenProcess(QueryLimitedInformation | Synchronize, false, entry.ParentProcessId);
                    if (parent.IsInvalid || WaitForSingleObject(parent, 0) != WaitTimeout
                        || !GetProcessTimes(parent, out var parentCreated, out var parentExited, out _, out _)
                        || parentExited != 0 || parentCreated > childCreated
                        || !IsDesktopParent(parent))
                        continue;
                    if (WaitForSingleObject(handle, 0) == WaitTimeout
                        && WaitForSingleObject(parent, 0) == WaitTimeout)
                        candidates[executable] = Math.Max(candidates.GetValueOrDefault(executable), parentCreated);
                }
                catch (Exception ex) when (IsDiscoveryFailure(ex)) { }
            } while (Process32Next(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) return null; // ERROR_NO_MORE_FILES
            return candidates.OrderByDescending(candidate => candidate.Value).Select(candidate => candidate.Key).FirstOrDefault();
        }
        catch (Exception ex) when (IsDiscoveryFailure(ex)) { }
        return null;
    }

    private static bool IsDesktopParent(SafeProcessHandle handle)
    {
        var executable = ReadExecutable(handle);
        var fileName = Path.GetFileName(executable);
        if (!fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ReadExecutableSubsystem(executable) != 2) return false;
        var package = ReadPackageFullName(handle);
        if (package is not null)
            return PackageFamily(package).Equals(DesktopPackageFamily, StringComparison.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(executable);
        return directory is not null && DesktopDirectories().Any(root => PathsEqual(directory, root)
                || PathsEqual(directory, Path.Combine(root, "app"))) && IsUnpackagedDesktop(executable);
    }

    private static string? UnpackagedDesktopExecutable()
    {
        foreach (var root in DesktopDirectories())
            foreach (var directory in new[] { root, Path.Combine(root, "app") })
            {
                try
                {
                    var desktop = ExistingFile(directory, "Codex.exe") ?? ExistingFile(directory, "ChatGPT.exe");
                    if (desktop is not null && IsUnpackagedDesktop(desktop) && ReadExecutableSubsystem(desktop) == 2
                        && DesktopExecutableIn(directory) is { } native)
                        return native;
                }
                catch (Exception ex) when (IsDiscoveryFailure(ex)) { }
            }
        return null;
    }

    private static IEnumerable<string> DesktopDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(local, "OpenAI", "Codex");
        yield return Path.Combine(local, "Programs", "Codex");
        yield return Path.Combine(programs, "OpenAI", "Codex");
        yield return Path.Combine(programs, "Codex");
    }

    private static string ExtractedDesktopBin => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");

    private static bool IsExtractedDesktopExecutable(string executable)
    {
        if (!Path.IsPathFullyQualified(executable)
            || !Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var directory = new FileInfo(executable).Directory;
        return directory is not null && (PathsEqual(directory.FullName, ExtractedDesktopBin)
            || directory.Parent is { } parent && PathsEqual(parent.FullName, ExtractedDesktopBin));
    }

    private static string? DesktopExecutableIn(string directory) => ExistingNativeFile(directory, "resources", "codex.exe");

    private static bool IsUnpackagedDesktop(string executable)
    {
        var version = FileVersionInfo.GetVersionInfo(executable);
        return version.ProductName?.Equals("Codex", StringComparison.OrdinalIgnoreCase) == true
            && version.CompanyName?.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static ushort? ReadExecutableSubsystem(string executable)
    {
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return null;
        stream.Position = 0x3C;
        var headerOffset = reader.ReadInt32();
        if (headerOffset < 64 || (long)headerOffset + 94 > stream.Length) return null;
        stream.Position = headerOffset;
        if (reader.ReadUInt32() != 0x00004550) return null;
        stream.Position = headerOffset + 24;
        if (reader.ReadUInt16() is not (0x10B or 0x20B)) return null;
        stream.Position = headerOffset + 24 + 68;
        return reader.ReadUInt16(); // WINDOWS_GUI = 2; WINDOWS_CUI = 3.
    }

    private static string ReadExecutable(SafeProcessHandle handle)
    {
        var executable = new StringBuilder(32768);
        var length = executable.Capacity;
        if (!QueryFullProcessImageName(handle, 0, executable, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return executable.ToString();
    }

    private static string? ReadPackageFullName(SafeProcessHandle handle)
    {
        uint length = 0;
        var error = GetPackageFullName(handle, ref length, null);
        if (error == NoPackage) return null;
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var package = new StringBuilder((int)length);
        error = GetPackageFullName(handle, ref length, package);
        if (error != 0) throw new Win32Exception(error);
        return package.ToString();
    }

    private static string PackageFamily(string packageFullName)
    {
        uint length = 0;
        var error = PackageFamilyNameFromFullName(packageFullName, ref length, null);
        if (error != InsufficientBuffer || length is 0 or > 32768) throw new Win32Exception(error);
        var family = new StringBuilder((int)length);
        error = PackageFamilyNameFromFullName(packageFullName, ref length, family);
        if (error != 0) throw new Win32Exception(error);
        return family.ToString();
    }

    internal static void AddBundledToolsToPath(ProcessStartInfo startInfo)
    {
        // Older npm wrappers add vendor/<target>/path to the native child's PATH.
        // Preserve that bounded tool lookup when starting the native CLI directly.
        try
        {
            var binaryDirectory = new FileInfo(startInfo.FileName).Directory;
            // Desktop extractions share rg.exe in the bin folder, while native
            // helpers remain beside codex.exe in its selected extraction folder.
            if (IsExtractedDesktopExecutable(startInfo.FileName))
            {
                if (ExistingFile(ExtractedDesktopBin, "rg.exe") is not null)
                    PrependToPath(startInfo, ExtractedDesktopBin);
                return;
            }
            // The desktop bundle places rg.exe beside its native CLI. Starting the
            // bundle directly must retain that tool lookup as well as npm's layout.
            if (binaryDirectory?.Name.Equals("resources", StringComparison.OrdinalIgnoreCase) == true
                && ExistingFile(binaryDirectory.FullName, "rg.exe") is not null)
            {
                PrependToPath(startInfo, binaryDirectory.FullName);
                return;
            }
            if (binaryDirectory is null || binaryDirectory.Name is not ("codex" or "bin"))
                return;
            var targetDirectory = binaryDirectory.Parent;
            if (targetDirectory is null || targetDirectory.Parent?.Name != "vendor"
                || targetDirectory.Name is not ("x86_64-pc-windows-msvc" or "aarch64-pc-windows-msvc"))
                return;
            var tools = Path.Combine(targetDirectory.FullName, "path");
            if (!Directory.Exists(tools)) return;
            PrependToPath(startInfo, tools);
        }
        catch (Exception ex) when (IsPathFailure(ex)) { }
    }

    private static void PrependToPath(ProcessStartInfo startInfo, string directory)
    {
        startInfo.Environment.TryGetValue("PATH", out var existingPath);
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
            ? directory : directory + Path.PathSeparator + existingPath;
    }

    private static string? NpmExecutableIn(string directory)
    {
        try { return FindNpmExecutableIn(directory); }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    private static string? FindNpmExecutableIn(string directory)
    {
        if (ExistingFile(directory, "codex.cmd") is null && ExistingFile(directory, "codex.ps1") is null)
            return null;
        var moduleDirectories = new List<string> { Path.Combine(directory, "node_modules") };
        // Local npm installations place their shims in node_modules/.bin.
        var parent = new DirectoryInfo(directory).Parent;
        if (Path.GetFileName(directory).Equals(".bin", StringComparison.OrdinalIgnoreCase)
            && parent?.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) == true)
            moduleDirectories.Add(parent.FullName);

        foreach (var modules in moduleDirectories)
        {
            var packageRoot = Path.Combine(modules, "@openai", "codex");
            if (ExistingFile(packageRoot, "bin", "codex.js") is null) continue;
            foreach (var (target, platformPackage) in WindowsTargets())
            {
                // Node resolves nested dependencies before sibling dependencies.
                // The original npm package also bundled its own vendor directory.
                var roots = new[]
                {
                    Path.Combine(packageRoot, "node_modules", "@openai", platformPackage),
                    Path.Combine(modules, "@openai", platformPackage),
                    packageRoot
                };
                foreach (var root in roots)
                    foreach (var leaf in new[] { "bin", "codex" })
                        if (ExistingNativeFile(root, "vendor", target, leaf, "codex.exe") is { } executable)
                            return executable;
            }
        }
        return null;
    }

    private static IEnumerable<(string Target, string PlatformPackage)> WindowsTargets()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            yield return ("aarch64-pc-windows-msvc", "codex-win32-arm64");
        yield return ("x86_64-pc-windows-msvc", "codex-win32-x64");
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64
            && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            yield return ("aarch64-pc-windows-msvc", "codex-win32-arm64");
    }

    private static string? FullyQualifiedDirectory(string directory)
    {
        try { return Path.IsPathFullyQualified(directory) ? Path.GetFullPath(directory) : null; }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    private static string? ExistingFile(string directory, params string[] parts)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return null;
            var candidate = Path.GetFullPath(Path.Combine(new[] { directory }.Concat(parts).ToArray()));
            if (IsPackagedExecutablePath(candidate)) return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    private static bool IsPackagedExecutablePath(string executable)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
            Path.Combine(Path.GetPathRoot(executable) ?? "", "WindowsApps")
        };
        return roots.Any(root => executable.StartsWith(Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExistingNativeFile(string directory, params string[] parts)
    {
        try
        {
            var executable = ExistingFile(directory, parts);
            return executable is not null && ReadExecutableSubsystem(executable) == 3 ? executable : null;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    private static bool IsPathFailure(Exception exception) => exception is
        ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException
        or System.Security.SecurityException;

    private static bool IsDiscoveryFailure(Exception exception) => IsPathFailure(exception)
        || exception is Win32Exception or DllNotFoundException or EntryPointNotFoundException;

    private static bool PathsEqual(string left, string right) =>
        Path.TrimEndingDirectorySeparator(left).Equals(Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder executable, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder? packageFullName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder? family);
}
