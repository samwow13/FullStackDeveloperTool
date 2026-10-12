using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Finds an installed native CLI without executing or reading package-manager shims.</summary>
internal static class CodexNativeExecutable
{
    internal static string Find()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => FullyQualifiedDirectory(directory.Trim().Trim('"')))
            .Where(directory => directory is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Honor PATH ordering, including a CLI installed through npm before a
        // desktop native binary. Prefer the direct native file within each folder.
        foreach (var directory in pathDirectories)
        {
            if (ExistingFile(directory, "codex.exe") is { } executable)
                return executable;
            // Resolve only official package layouts; never parse or execute shims.
            if (NpmExecutableIn(directory) is { } npmExecutable)
                return npmExecutable;
        }

        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        if (ExistingFile(bin, "codex.exe") is { } direct)
            return direct;
        try
        {
            if (Directory.Exists(bin))
                foreach (var directory in new DirectoryInfo(bin).EnumerateDirectories()
                    .OrderByDescending(item => item.LastWriteTimeUtc))
                    if (ExistingFile(directory.FullName, "codex.exe") is { } executable)
                        return executable;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { }
        throw new InvalidOperationException(CodexAppServerConnection.NotFoundMessage);
    }

    internal static void AddBundledToolsToPath(ProcessStartInfo startInfo)
    {
        // Older npm wrappers add vendor/<target>/path to the native child's PATH.
        // Preserve that bounded tool lookup when starting the native CLI directly.
        try
        {
            var binaryDirectory = new FileInfo(startInfo.FileName).Directory;
            if (binaryDirectory is null || binaryDirectory.Name is not ("codex" or "bin"))
                return;
            var targetDirectory = binaryDirectory.Parent;
            if (targetDirectory is null || targetDirectory.Parent?.Name != "vendor"
                || targetDirectory.Name is not ("x86_64-pc-windows-msvc" or "aarch64-pc-windows-msvc"))
                return;
            var tools = Path.Combine(targetDirectory.FullName, "path");
            if (!Directory.Exists(tools)) return;
            startInfo.Environment.TryGetValue("PATH", out var existingPath);
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
                ? tools : tools + Path.PathSeparator + existingPath;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { }
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
                        if (ExistingFile(root, "vendor", target, leaf, "codex.exe") is { } executable)
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
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    private static bool IsPathFailure(Exception exception) => exception is
        ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException
        or System.Security.SecurityException;
}
