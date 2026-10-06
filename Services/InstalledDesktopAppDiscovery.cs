using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace FullStackLauncher.Services;

/// <summary>
/// Reads local Start Menu shortcut metadata without resolving, repairing, or opening shortcuts.
/// Call from a background worker. Cancellation stops between native/filesystem metadata reads.
/// </summary>
internal static class InstalledDesktopAppDiscovery
{
    private const int MaximumDepth = 4;
    private const int MaximumDirectories = 128;
    private const int MaximumEntries = 4096;
    private const int MaximumShortcuts = 512;
    private const long MaximumShortcutBytes = 1024 * 1024;

    internal static IReadOnlyList<ServiceEditorTarget> FindStartMenuApps(
        Func<string, string> resolveExecutable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return [];
        var completion = new TaskCompletionSource<IReadOnlyList<ServiceEditorTarget>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(ReadStartMenuApps(resolveExecutable, cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "Installed desktop app shortcuts" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
    }

    private static IReadOnlyList<ServiceEditorTarget> ReadStartMenuApps(
        Func<string, string> resolveExecutable, CancellationToken cancellationToken)
    {
        var installed = new List<ServiceEditorTarget>();
        object? shellObject = null;
        try
        {
            shellObject = new ShellLinkObject();
            var link = (IShellLinkReader)shellObject;
            var persistence = (IPersistFile)shellObject;
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsLocalPathWithoutReparsePoints(root, cancellationToken)) continue;
                foreach (var shortcut in EnumerateShortcuts(root, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (new FileInfo(shortcut).Length > MaximumShortcutBytes) continue;
                        // STGM_READ. Loading only reads the saved link; Resolve is deliberately
                        // absent from our interface so discovery cannot repair links or invoke MSI.
                        persistence.Load(shortcut, 0);
                        var arguments = new StringBuilder(2);
                        if (link.GetArguments(arguments, arguments.Capacity) != 0 || arguments.Length != 0) continue;
                        var path = new StringBuilder(260);
                        if (link.GetPath(path, path.Capacity, IntPtr.Zero, 4) != 0 // SLGP_RAWPATH
                            || path.Length == 0 || path.Length >= path.Capacity - 1) continue;
                        var rawPath = path.ToString();
                        if (!IsLocalPathWithoutReparsePoints(rawPath, cancellationToken)) continue;
                        var executable = resolveExecutable(rawPath);
                        if (!IsLocalPathWithoutReparsePoints(executable, cancellationToken) || !File.Exists(executable)) continue;
                        var name = Path.GetFileNameWithoutExtension(shortcut).Trim();
                        if (name.Length == 0 || name.Any(char.IsControl)) continue;
                        installed.Add(new(name, executable));
                    }
                    // Broken, inaccessible, advertised, or non-file links are omitted. Never
                    // resolve a missing target or inspect arguments beyond their presence.
                    catch (Exception exception) when (IsExpectedMetadataFailure(exception) || exception is COMException) { }
                }
            }
        }
        catch (Exception exception) when (IsExpectedMetadataFailure(exception) || exception is COMException) { }
        finally
        {
            if (shellObject is not null) Marshal.FinalReleaseComObject(shellObject);
        }
        return installed;
    }

    private static IEnumerable<string> EnumerateShortcuts(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        var directories = 0;
        var entries = 0;
        var shortcuts = 0;
        while (pending.Count > 0 && directories < MaximumDirectories && entries < MaximumEntries && shortcuts < MaximumShortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            directories++;
            IEnumerator<string>? children = null;
            try { children = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).GetEnumerator(); }
            catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { }
            if (children is null) continue;
            using (children)
            {
                while (entries < MaximumEntries && shortcuts < MaximumShortcuts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool hasChild;
                    try { hasChild = children.MoveNext(); }
                    catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { break; }
                    if (!hasChild) break;
                    entries++;
                    var child = children.Current;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < MaximumDepth && pending.Count + directories < MaximumDirectories)
                            pending.Push((child, depth + 1));
                    }
                    else if (Path.GetExtension(child).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        shortcuts++;
                        yield return child;
                    }
                }
            }
        }
    }

    internal static bool IsLocalPathWithoutReparsePoints(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            if (expanded.Length > 32767 || !Path.IsPathFullyQualified(expanded)
                || expanded.Length < 3 || !char.IsLetter(expanded[0]) || expanded[1] != ':'
                || expanded[2] is not ('\\' or '/')) return false;
            var path = Path.GetFullPath(expanded);
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 64) return false;
            var current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (IsExpectedMetadataFailure(exception)) { return false; }
    }

    private static bool IsExpectedMetadataFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkObject { }

    // IShellLinkW vtable prefix through GetArguments. Unused slots must remain in order.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkReader
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr identifiers);
        [PreserveSig] int SetIDList(IntPtr identifiers);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    }
}
