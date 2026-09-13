using System.IO;
using System.Security;
using System.Text;

namespace FullStackLauncher.Services;

public enum GitBranchState { Branch, Detached, NotRepository, MissingDirectory, Unavailable }

public sealed record GitBranchSnapshot(
    string? RepositoryPath,
    string DisplayText,
    string Detail,
    bool HasRepository,
    GitBranchState State);

/// <summary>
/// Reads only the nearest checkout's .git marker and HEAD, including linked worktrees and
/// submodules. It never invokes Git, reads repository configuration, or changes the checkout.
/// </summary>
public static class GitBranchReader
{
    private const int MaximumAncestors = 256;
    private const int MaximumMetadataBytes = 16 * 1024;
    private static readonly UTF8Encoding MetadataEncoding = new(false, true);

    // Even directory probing belongs off the UI thread: a saved folder may be on a slow drive.
    public static Task<GitBranchSnapshot> ReadAsync(string directory, CancellationToken token = default) =>
        Task.Run(() => Read(directory, token), token);

    private static GitBranchSnapshot Read(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directory))
            return MissingDirectory("No project folder is configured.");

        string? repositoryPath = null;
        try
        {
            var folder = Path.GetFullPath(directory);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(folder);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return MissingDirectory($"The configured folder does not exist.\nFolder: {folder}");
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
                return MissingDirectory($"The configured path is not a folder.\nFolder: {folder}");

            DirectoryInfo? current = new(folder);
            for (var depth = 0; current is not null && depth < MaximumAncestors; depth++)
            {
                token.ThrowIfCancellationRequested();
                var marker = Path.Combine(current.FullName, ".git");
                FileAttributes markerAttributes;
                try
                {
                    markerAttributes = File.GetAttributes(marker);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    current = current.Parent;
                    continue;
                }

                // Once a nearer marker exists, never fall back to an unrelated parent repository.
                repositoryPath = current.FullName;
                var gitDirectory = marker;
                if (!markerAttributes.HasFlag(FileAttributes.Directory))
                {
                    string markerText;
                    try
                    {
                        markerText = ReadMetadata(marker, token);
                    }
                    catch (InvalidDataException)
                    {
                        return Unavailable(repositoryPath, "The .git file is not readable Git directory metadata.");
                    }

                    const string prefix = "gitdir: ";
                    if (!markerText.StartsWith(prefix, StringComparison.Ordinal)
                        || markerText.Length == prefix.Length
                        || markerText.Any(char.IsControl))
                        return Unavailable(repositoryPath, "The .git file does not identify a valid Git directory.");

                    // Git resolves a relative gitdir against the directory containing the .git file.
                    gitDirectory = Path.GetFullPath(markerText[prefix.Length..], repositoryPath);
                }

                token.ThrowIfCancellationRequested();
                string head;
                try
                {
                    // A worktree owns its HEAD; its common directory's HEAD belongs to another checkout.
                    head = ReadMetadata(Path.Combine(gitDirectory, "HEAD"), token);
                }
                catch (InvalidDataException)
                {
                    return InvalidHead(repositoryPath);
                }

                const string branchPrefix = "ref: refs/heads/";
                if (head.StartsWith(branchPrefix, StringComparison.Ordinal))
                {
                    var branch = head[branchPrefix.Length..];
                    if (!IsValidBranchRef(branch)) return InvalidHead(repositoryPath);

                    // The ref need not exist yet: HEAD still names the branch of an unborn checkout.
                    return new(repositoryPath, branch, $"Active branch: {branch}\nRepository: {repositoryPath}",
                        true, GitBranchState.Branch);
                }

                if (head.Length is 40 or 64 && head.All(Uri.IsHexDigit))
                    return new(repositoryPath, $"Detached · {head[..8]}",
                        $"Detached HEAD: {head}\nRepository: {repositoryPath}", true, GitBranchState.Detached);

                return InvalidHead(repositoryPath);
            }

            return current is null
                ? new(null, "Not a Git repository", $"No .git directory or file was found in this folder or its parents.\nFolder: {folder}",
                    false, GitBranchState.NotRepository)
                : Unavailable(null, "The folder ancestry exceeds the Git lookup limit.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException)
        {
            return Unavailable(repositoryPath, repositoryPath is null
                ? "The configured folder or its Git metadata could not be accessed."
                : "The repository's Git directory or HEAD could not be accessed.");
        }
    }

    private static string ReadMetadata(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024, FileOptions.SequentialScan);
        if (!stream.CanSeek || stream.Length > MaximumMetadataBytes)
            throw new InvalidDataException("Git metadata exceeds its read limit.");

        // Read one extra byte so a file growing during the read cannot evade the size bound.
        var buffer = new byte[MaximumMetadataBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        token.ThrowIfCancellationRequested();
        if (count > MaximumMetadataBytes)
            throw new InvalidDataException("Git metadata exceeds its read limit.");

        try
        {
            return MetadataEncoding.GetString(buffer, 0, count).TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Git metadata is not valid UTF-8.");
        }
    }

    private static bool IsValidBranchRef(string branch)
    {
        if (branch.Length == 0 || branch.EndsWith('.')
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("@{", StringComparison.Ordinal)
            || branch.Any(character => char.IsControl(character) || character is ' ' or '~' or '^'
                or ':' or '?' or '*' or '[' or '\\'))
            return false;

        return branch.Split('/').All(component => component.Length > 0
            && !component.StartsWith('.') && !component.EndsWith(".lock", StringComparison.Ordinal));
    }

    private static GitBranchSnapshot MissingDirectory(string detail) =>
        new(null, "Folder missing", detail, false, GitBranchState.MissingDirectory);

    private static GitBranchSnapshot Unavailable(string? repositoryPath, string detail) =>
        new(repositoryPath, "Branch unavailable", repositoryPath is null ? detail : $"{detail}\nRepository: {repositoryPath}",
            repositoryPath is not null, GitBranchState.Unavailable);

    private static GitBranchSnapshot InvalidHead(string repositoryPath) =>
        new(repositoryPath, "Invalid Git HEAD", $"HEAD does not contain a valid local branch reference or commit ID.\nRepository: {repositoryPath}",
            true, GitBranchState.Unavailable);
}
