using System.IO;
using System.Runtime.InteropServices;
using FullStackLauncher.Models;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    /// <summary>Local-only validation for the UI's explicit clone review. No network request or mutation.</summary>
    public static Task<GitCloneReview> PrepareCloneAsync(string folder, GitHostingProvider provider, string url,
        CancellationToken token = default) => Task.Run(async () =>
        {
            var fullFolder = RequireFolder(folder);
            var cleanUrl = GitConnectionService.ValidateUrl(provider, url);
            using var directory = OpenCloneDirectory(fullFolder, out var identity);
            await RequireCloneDestinationAsync(fullFolder, cleanUrl, token).ConfigureAwait(false);
            return new GitCloneReview(fullFolder, provider, cleanUrl, identity);
        }, token);

    /// <summary>Clones only into the exact empty folder reviewed by the user; never stages, commits, or pushes.</summary>
    public static Task<string> CloneAsync(GitCloneReview review, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(review);
            var fullFolder = RequireFolder(review.Folder);
            var cleanUrl = GitConnectionService.ValidateUrl(review.Provider, review.CloneUrl);
            if (!string.Equals(cleanUrl, review.CloneUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("The repository URL changed. Review it again before cloning.");
            var gate = RepositoryGates.GetOrAdd(fullFolder, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await using var setupLease = await AcquireGitSetupOperationLeaseAsync(fullFolder, token).ConfigureAwait(false);
                // Keep this directory's Windows identity and deny renames/deletion until Git ends.
                using var directory = OpenCloneDirectory(fullFolder, out var identity);
                if (!string.Equals(identity, review.DirectoryIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException("This folder changed. Review the empty folder again before cloning.");
                await RequireCloneDestinationAsync(fullFolder, cleanUrl, token).ConfigureAwait(false);
                var hooksFolder = Path.Combine(Path.GetDirectoryName(setupLease.Name)!, "disabled-hooks");
                Directory.CreateDirectory(hooksFolder);
                if (Directory.EnumerateFileSystemEntries(hooksFolder).Any())
                    throw new InvalidOperationException("The Git setup hooks folder is not empty. Clone with Git directly, then refresh.");
                // No templates, submodules, checkout hooks, external filters, or new SSH trust.
                var clone = await GitAsync(fullFolder,
                    ["-c", "core.hooksPath=" + hooksFolder, "-c", "core.fsmonitor=false",
                     "-c", "core.protectNTFS=true", "-c", "core.protectHFS=true",
                     "-c", "http.followRedirects=false", "-c", "http." + cleanUrl + ".sslVerify=true",
                     "clone", "--quiet", "--no-recurse-submodules", "--origin", "origin", "--template=", "--",
                     cleanUrl, fullFolder], token, network: true, interactive: true, strictSsh: true).ConfigureAwait(false);
                if (clone.ExitCode != 0)
                {
                    try { ThrowCommandFailure(clone); }
                    catch (InvalidOperationException exception)
                    {
                        throw new InvalidOperationException("Clone did not complete. Inspect this folder before trying again.\n"
                            + exception.Message, exception);
                    }
                }
                var success = Result(clone, "Repository cloned.");
                try
                {
                    await using var repositoryLease = await AcquireRepositoryOperationLeaseAsync(fullFolder, token).ConfigureAwait(false);
                    var snapshot = await ReadCoreAsync(fullFolder, token).ConfigureAwait(false);
                    RequireExactRoot(fullFolder, snapshot);
                    var fetchUrl = await RequireSingleRemoteAsync(fullFolder, "origin", false, token).ConfigureAwait(false);
                    var pushUrl = await RequireSingleRemoteAsync(fullFolder, "origin", true, token).ConfigureAwait(false);
                    if (!string.Equals(cleanUrl, fetchUrl, StringComparison.Ordinal)
                        || !string.Equals(cleanUrl, pushUrl, StringComparison.Ordinal))
                        throw new InvalidOperationException("The cloned remote no longer matches the reviewed URL.");
                    EnsureSuccess(await GitAsync(fullFolder,
                        ["config", "--local", "--replace-all", "remote.origin.launcherProvider", review.Provider.ToString()], token).ConfigureAwait(false));
                    EnsureSuccess(await GitAsync(fullFolder,
                        ["config", "--local", "--replace-all", "remote.origin.launcherProviderTarget", RemoteTargetFingerprint(cleanUrl)], token).ConfigureAwait(false));
                    return success;
                }
                catch (GitCommandExitUnconfirmedException) { throw; }
                catch (Exception exception) when (exception is InvalidOperationException or IOException
                    or UnauthorizedAccessException or OperationCanceledException)
                {
                    // Clone's zero exit is confirmed. A later refresh/provider save must not erase it.
                    return success + "\nRefresh or provider setup was not confirmed. Refresh before another Git action.\n"
                        + Sanitize(exception.Message);
                }
            }
            finally { gate.Release(); }
        }, token);

    private static async Task RequireCloneDestinationAsync(string folder, string cleanUrl, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireUnlinkedCloneAncestry(folder);
        var metadata = await GitBranchReader.ReadAsync(folder, token).ConfigureAwait(false);
        var existing = await GitAsync(folder, ["rev-parse", "--git-dir"], token).ConfigureAwait(false);
        if (existing.ExitCode == 0 || metadata.State != GitBranchState.NotRepository)
            throw new InvalidOperationException("This folder is already inside a repository, or its Git metadata cannot be verified. Refresh before connecting it.");
        if (!existing.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            ThrowCommandFailure(existing);
        if (Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("This folder contains files. Choose New repository, or select an empty configured folder.");
        var filters = await GitAsync(folder,
            ["config", "--null", "--get-regexp", "^filter\\..*\\.(smudge|process)$"], token).ConfigureAwait(false);
        if (filters.ExitCode is not (0 or 1)) ThrowCommandFailure(filters);
        foreach (var entry in filters.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\n');
            if (separator < 0 || !string.IsNullOrWhiteSpace(entry[(separator + 1)..]))
                throw new InvalidOperationException("External checkout filters are configured. Clone with Git directly, then refresh this folder.");
        }
        // --get-url resolves Git rewrites locally without contacting the destination.
        var resolved = await GitAsync(folder, ["ls-remote", "--get-url", "--", cleanUrl], token).ConfigureAwait(false);
        if (resolved.ExitCode != 0 || !string.Equals(resolved.Output.TrimEnd('\r', '\n'), cleanUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("Git rewrites this clone URL or cannot resolve it. Review URL rewrite rules with Git before connecting.");
        token.ThrowIfCancellationRequested();
        if (Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("This folder contains files. Choose New repository, or select an empty configured folder.");
    }

    /// <summary>Shared setup lease lives outside the reviewed folder so the destination stays empty.</summary>
    internal static Task<FileStream> AcquireGitSetupOperationLeaseAsync(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("The Git setup lock folder is unavailable. Check this Windows user's local data folder.");
        var directory = Path.Combine(local, "FullStackLauncher", "git-setup", "locks");
        var key = RemoteTargetFingerprint(RequireFolder(folder).ToUpperInvariant());
        try
        {
            Directory.CreateDirectory(directory);
            return Task.FromResult(new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException)
        { throw new InvalidOperationException("Another Git setup operation is using this folder, or its setup lock is unavailable. Wait and retry."); }
        catch (UnauthorizedAccessException)
        { throw new InvalidOperationException("The Git setup lock folder is not writable. Check this Windows user's local data permissions."); }
    }

    private static SafeFileHandle OpenCloneDirectory(string folder, out string identity)
    {
        RequireUnlinkedCloneAncestry(folder);
        // FILE_READ_ATTRIBUTES, read/write sharing (without delete), OPEN_EXISTING, directory/reparse flags.
        var handle = OpenCloneDirectoryHandle(folder, 0x80, 0x3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !ReadCloneDirectoryInformation(handle, out var information))
        {
            handle.Dispose();
            throw new InvalidOperationException("The selected empty folder cannot be verified or locked. Check its permissions before cloning.");
        }
        if ((information.Attributes & 0x10) == 0 || (information.FileIndexHigh == 0 && information.FileIndexLow == 0))
        {
            handle.Dispose();
            throw new InvalidOperationException("The selected folder's identity cannot be verified. Refresh before cloning.");
        }
        identity = $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
        return handle;
    }

    private static void RequireUnlinkedCloneAncestry(string folder)
    {
        DirectoryInfo? current = new(folder);
        for (var depth = 0; current is not null && depth < 256; depth++)
        {
            // OneDrive placeholders may be reparse points; only actual links/junctions are rejected.
            if (current.LinkTarget is not null)
                throw new InvalidOperationException("Choose a configured folder without a directory link or junction before cloning.");
            current = current.Parent;
        }
        if (current is not null)
            throw new InvalidOperationException("The folder ancestry exceeds the Git setup lookup limit.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloneDirectoryInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenCloneDirectoryHandle(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadCloneDirectoryInformation(SafeFileHandle handle, out CloneDirectoryInformation information);
}
