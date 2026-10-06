using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Finds a supported installed GCM without changing Git configuration or installing tools.</summary>
internal static class GitCredentialManagerSupport
{
    internal static Task<string> ResolveAsync(string folder, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        var lookup = await GitRepositoryService.GitAsync(folder, ["--exec-path"], token).ConfigureAwait(false);
        if (lookup.ExitCode != 0)
            throw Unavailable();
        var execPath = lookup.Output.TrimEnd('\r', '\n');
        if (!TryNormalizeLocalPath(execPath, out var directory)) throw Unavailable();
        var candidates = new List<string> { Path.Combine(directory, "git-credential-manager.exe") };
        // Git for Windows can contain the retired GCM in libexec/git-core and modern GCM in mingw64/bin.
        var distributionDirectory = Directory.GetParent(directory)?.Parent?.FullName;
        if (distributionDirectory != null)
            candidates.Add(Path.Combine(distributionDirectory, "bin", "git-credential-manager.exe"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (TryNormalizeLocalPath(programFiles, out var programDirectory))
            candidates.Add(Path.Combine(programDirectory, "Git Credential Manager", "git-credential-manager.exe"));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (TrySupportedExecutable(candidate, out var executable)) return executable;
        }
        throw Unavailable();
    }, token);

    internal static async Task<IReadOnlyList<string>> CloneOptionsAsync(string folder, CancellationToken token = default) =>
        CloneOptions(await ResolveAsync(folder, token).ConfigureAwait(false));

    internal static IReadOnlyList<string> CloneOptions(string resolvedExe)
    {
        if (!TrySupportedExecutable(resolvedExe, out var executable)) throw Unavailable();
        // Git treats ! helpers as shell commands. Quote the entire native path for Git's POSIX shell;
        // single quotes in a valid Windows path must close/reopen the shell quote explicitly.
        var quoted = "'" + executable.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return
        [
            "-c", "credential.helper=", "-c", "credential.helper=!" + quoted,
            "-c", "credential.provider=azure-repos", "-c", "credential.azreposCredentialType=oauth",
            "-c", "credential.azreposUseMicrosoftSharedCache=true", "-c", "credential.allowUnsafeRemotes=false"
        ];
    }

    private static bool TrySupportedExecutable(string path, out string executable)
    {
        executable = "";
        if (!TryNormalizeLocalPath(path, out var fullPath) || !File.Exists(fullPath) ||
            !Path.GetFileName(fullPath).Equals("git-credential-manager.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(fullPath);
            if (version.FileMajorPart < 2 || version.FileMajorPart == 2 && version.FileMinorPart < 4) return false;
            executable = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        { return false; }
    }

    private static bool TryNormalizeLocalPath(string path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Length > 32767 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal)) return false;
        try { fullPath = Path.GetFullPath(path); return true; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static InvalidOperationException Unavailable() => new(
        "Git Credential Manager 2.4 or newer is required for Microsoft sign-in. Install or update Git for Windows with modern Git Credential Manager, then retry.");
}
