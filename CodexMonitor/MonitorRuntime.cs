using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FullStackLauncher.CodexMonitor;

// The tray monitor outlives the dashboard. Run an immutable copy so it cannot lock
// the dashboard's Debug/Release output or the user's published executable.
internal static class MonitorRuntime
{
    internal static ProcessStartInfo CreateStart(string[] args)
    {
        if (PrepareStart(args) is { } staged) return staged;
        var processPath = Environment.ProcessPath ?? throw new IOException("The launcher executable could not be located.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(App).Assembly.GetName().Name + ".dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    internal static ProcessStartInfo? PrepareStart(string[] args)
    {
        var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var runtimeRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher", "monitor-runtime"));
        if (sourceDirectory.StartsWith(runtimeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        var assemblyName = typeof(App).Assembly.GetName().Name!;
        var assemblyPath = Path.Combine(sourceDirectory, assemblyName + ".dll");
        var processPath = Environment.ProcessPath ?? throw new IOException("The launcher executable could not be located.");
        var dotnetHosted = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var files = new List<string>();
        if (!dotnetHosted) files.Add(processPath);
        if (File.Exists(assemblyPath))
        {
            files.AddRange(Directory.EnumerateFiles(sourceDirectory, "*.dll"));
            files.Add(Path.Combine(sourceDirectory, assemblyName + ".deps.json"));
            files.Add(Path.Combine(sourceDirectory, assemblyName + ".runtimeconfig.json"));
            var nativeDirectory = Path.Combine(sourceDirectory, "runtimes");
            if (Directory.Exists(nativeDirectory))
                files.AddRange(Directory.EnumerateFiles(nativeDirectory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }));
        }
        else if (dotnetHosted) throw new IOException("The launcher assembly could not be located.");
        // A single-file publish has no adjacent managed assembly: copy only its EXE,
        // never unrelated files beside an EXE copied to the user's Desktop.
        var payload = files.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Source: path, Relative: Path.GetRelativePath(sourceDirectory, path)))
            .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in payload)
            if (Path.IsPathRooted(file.Relative) || file.Relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new IOException("A launcher dependency is outside its application folder.");

        string Fingerprint(string? directory)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in payload)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\0"));
                using var stream = File.OpenRead(directory is null ? file.Source : Path.Combine(directory, file.Relative));
                hash.AppendData(SHA256.HashData(stream));
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        var fingerprint = Fingerprint(null);
        var destination = Path.Combine(runtimeRoot, fingerprint);
        Directory.CreateDirectory(runtimeRoot);
        if (!Directory.Exists(destination))
        {
            var staging = Path.Combine(runtimeRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in payload)
                {
                    var target = Path.Combine(staging, file.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file.Source, target);
                }
                if (Fingerprint(staging) != fingerprint)
                    throw new IOException("The launcher build changed while preparing the monitor. Finish the build and open alerts again.");
                try { Directory.Move(staging, destination); }
                catch (IOException) when (Directory.Exists(destination)) { /* Another start prepared this exact build. */ }
            }
            finally
            {
                // Only this invocation's generated staging directory can be removed.
                var stagingPath = Path.GetFullPath(staging);
                if (stagingPath.StartsWith(runtimeRoot + Path.DirectorySeparatorChar + ".staging-", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            }
        }
        if (Fingerprint(destination) != fingerprint)
            throw new IOException("The cached monitor files are incomplete. Exit the monitor and remove its runtime cache before reopening alerts.");

        var start = new ProcessStartInfo(dotnetHosted ? processPath : Path.Combine(destination, Path.GetFileName(processPath)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (dotnetHosted) start.ArgumentList.Add(Path.Combine(destination, assemblyName + ".dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }
}
