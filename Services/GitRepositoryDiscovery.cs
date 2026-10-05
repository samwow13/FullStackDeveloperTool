using System.IO;
using System.Security;

namespace FullStackLauncher.Services;

public sealed record GitRepositoryDiscoveryFolder(string Label, string Directory, bool ScanImmediateChildren = false);

public sealed record GitRepositoryChoice(string Label, string Directory, bool IsRepository, GitBranchState State, string Detail)
{
    public string Display => Label;
}

public sealed record GitRepositoryResolvedFolder(string Directory, GitBranchSnapshot Snapshot);

public sealed record GitRepositoryDiscoveryResult(
    IReadOnlyList<GitRepositoryChoice> Folders,
    IReadOnlyList<GitRepositoryChoice> Repositories,
    string? Warning)
{
    public IReadOnlyList<GitRepositoryResolvedFolder> ResolvedFolders { get; init; } = [];
}

/// <summary>
/// Resolves configured folders to their nearest checkout using local metadata only. An explicit
/// project-root request also checks immediate child folders for .git markers, never descendants.
/// The conventional ORKidsDatabase child is checked before the bounded general folder scan.
/// No Git commands, repository configuration, credentials or source files are read.
/// </summary>
public static class GitRepositoryDiscovery
{
    private const int MaximumChildFolders = 128;
    private static readonly TimeSpan FolderReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly object PendingReadsLock = new();
    private static readonly Dictionary<string, Task<GitBranchSnapshot>> PendingBranchReads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<ChildScan>> PendingChildScans = new(StringComparer.OrdinalIgnoreCase);

    public static Task<GitRepositoryDiscoveryResult> DiscoverAsync(
        IReadOnlyList<GitRepositoryDiscoveryFolder> folders, CancellationToken token = default)
    {
        var captured = folders.ToArray();
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var candidates = captured.Select(folder => folder with { Directory = NormalizeDirectory(folder.Directory) })
                .GroupBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => new GitRepositoryDiscoveryFolder(
                    string.Join(" / ", group.Select(folder => folder.Label).Distinct()), group.Key,
                    group.Any(folder => folder.ScanImmediateChildren)))
                .ToList();
            var warnings = new List<string>();

            foreach (var folder in candidates.Where(folder => folder.ScanImmediateChildren).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var scan = await ReadChildrenAsync(folder.Directory, token).ConfigureAwait(false);
                warnings.AddRange(scan.Warnings);
                foreach (var child in scan.Directories)
                {
                    if (candidates.Any(candidate => string.Equals(candidate.Directory, child, StringComparison.OrdinalIgnoreCase))) continue;
                    candidates.Add(new(Path.GetFileName(child), child));
                }
            }

            var reads = new (GitRepositoryDiscoveryFolder Folder, GitBranchSnapshot Snapshot)[candidates.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, lifetime) =>
                {
                    var folder = candidates[index];
                    reads[index] = (folder, await ReadBranchAsync(folder.Directory, lifetime).ConfigureAwait(false));
                }).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            var choices = reads.GroupBy(read => NormalizeDirectory(read.Snapshot.RepositoryPath ?? read.Folder.Directory),
                StringComparer.OrdinalIgnoreCase).Select(group =>
                {
                    var repository = group.FirstOrDefault(read => read.Snapshot.HasRepository);
                    var snapshot = repository.Snapshot ?? group.First().Snapshot;
                    var labels = group.Select(read => read.Folder.Label).Distinct().ToArray();
                    // Service names identify separate checkouts more clearly than a container's root label.
                    var label = snapshot.HasRepository && labels.Length > 1
                        ? string.Join(" / ", group.Where(read => !read.Folder.ScanImmediateChildren).Select(read => read.Folder.Label).Distinct())
                        : string.Join(" / ", labels);
                    if (string.IsNullOrWhiteSpace(label)) label = Path.GetFileName(group.Key);
                    var detail = string.Join(Environment.NewLine + Environment.NewLine,
                        group.Select(read => $"{read.Folder.Label}: {read.Folder.Directory}\n{read.Snapshot.Detail}"));
                    return new GitRepositoryChoice(label, group.Key, snapshot.HasRepository, snapshot.State, detail);
                }).ToArray();

            foreach (var read in reads.Where(read => read.Snapshot.State is GitBranchState.Unavailable or GitBranchState.MissingDirectory))
                warnings.Add($"{read.Folder.Label}: {read.Folder.Directory}\n{read.Snapshot.Detail}");

            return new GitRepositoryDiscoveryResult(choices, choices.Where(choice => choice.IsRepository).ToArray(),
                warnings.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, warnings.Distinct()))
            {
                ResolvedFolders = reads.Select(read => new GitRepositoryResolvedFolder(read.Folder.Directory, read.Snapshot)).ToArray()
            };
        }, token);
    }

    private static async Task<GitBranchSnapshot> ReadBranchAsync(string directory, CancellationToken token)
    {
        Task<GitBranchSnapshot> read;
        lock (PendingReadsLock)
        {
            if (!PendingBranchReads.TryGetValue(directory, out read!))
                PendingBranchReads[directory] = read = GitBranchReader.ReadAsync(directory);
        }
        try { return await read.WaitAsync(FolderReadTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new(null, "Folder check timed out", $"The folder is taking too long to respond.\nFolder: {directory}",
                false, GitBranchState.Unavailable);
        }
        finally
        {
            lock (PendingReadsLock)
                if (read.IsCompleted && PendingBranchReads.TryGetValue(directory, out var pending) && ReferenceEquals(pending, read))
                    PendingBranchReads.Remove(directory);
        }
    }

    private static async Task<ChildScan> ReadChildrenAsync(string directory, CancellationToken token)
    {
        Task<ChildScan> scan;
        lock (PendingReadsLock)
        {
            if (!PendingChildScans.TryGetValue(directory, out scan!))
                PendingChildScans[directory] = scan = Task.Run(() => ScanChildren(directory));
        }
        try { return await scan.WaitAsync(FolderReadTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new([], [$"Child repository discovery timed out.\nFolder: {directory}"]);
        }
        finally
        {
            lock (PendingReadsLock)
                if (scan.IsCompleted && PendingChildScans.TryGetValue(directory, out var pending) && ReferenceEquals(pending, scan))
                    PendingChildScans.Remove(directory);
        }
    }

    private static ChildScan ScanChildren(string directory)
    {
        var children = new List<string>();
        var warnings = new List<string>();
        try
        {
            // A configured junction can still be read normally, but discovery must not traverse it.
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                return new([], [$"Child repository discovery skipped this linked project folder.\nFolder: {directory}"]);
            // Reserve one probe for the database checkout so unrelated folders cannot exhaust
            // the scan budget before this non-service repository is considered.
            var databaseDirectory = Path.Combine(directory, "ORKidsDatabase");
            var count = AddChildRepository(databaseDirectory, children, warnings) ? 1 : 0;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(child, databaseDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (++count > MaximumChildFolders)
                {
                    warnings.Add($"Child repository discovery reached its limit of {MaximumChildFolders} folders.\nFolder: {directory}");
                    break;
                }
                AddChildRepository(child, children, warnings);
            }
        }
        catch (Exception exception) when (IsFolderReadError(exception))
        {
            warnings.Add($"Child repository discovery could not read this folder.\nFolder: {directory}");
        }
        return new(children.Order(StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static bool AddChildRepository(string child, List<string> children, List<string> warnings)
    {
        var folderExists = false;
        try
        {
            var attributes = File.GetAttributes(child);
            folderExists = true;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"Child repository discovery skipped this linked folder.\nFolder: {child}");
                return true;
            }
            if (File.GetAttributes(Path.Combine(child, ".git")).HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"Child repository discovery skipped linked Git metadata.\nFolder: {child}");
                return true;
            }
            children.Add(NormalizeDirectory(child));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception exception) when (IsFolderReadError(exception))
        {
            warnings.Add($"Child repository discovery could not read this folder.\nFolder: {child}");
        }
        return folderExists;
    }

    private static string NormalizeDirectory(string directory)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception exception) when (IsFolderReadError(exception)) { return directory; }
    }

    private static bool IsFolderReadError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException;

    private sealed record ChildScan(IReadOnlyList<string> Directories, IReadOnlyList<string> Warnings);
}
