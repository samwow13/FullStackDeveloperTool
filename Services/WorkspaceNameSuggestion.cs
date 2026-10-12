using System.Globalization;
using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Suggests an unused workspace child name without creating folders or reserving the destination.</summary>
public static class WorkspaceNameSuggestion
{
    private const int EntryLimit = 10_000;
    private const int CandidateLimit = 99_999;

    public static string FindAvailable(string parentFolder, IReadOnlyList<string> savedWorkspaceFolders,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(savedWorkspaceFolders);
        token.ThrowIfCancellationRequested();
        var parent = NormalizeFolder(parentFolder,
            "The workspace location is invalid. Choose an existing local folder.");
        if (parent.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a local folder to suggest a workspace name.");

        if (savedWorkspaceFolders.Count > EntryLimit)
            throw new InvalidOperationException("There are too many saved workspaces to suggest a name. Enter a workspace name manually.");
        var savedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var savedFolder in savedWorkspaceFolders)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(savedFolder)) continue;
            savedRoots.Add(NormalizeFolder(savedFolder,
                "A saved workspace location is invalid. Correct its location before suggesting a workspace name."));
        }

        var occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };
            var entryCount = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (++entryCount > EntryLimit)
                    throw new InvalidOperationException("The selected location contains too many entries to suggest a workspace name. Enter a name manually or choose another location.");
                occupiedNames.Add(Path.GetFileName(entry));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("The workspace location could not be inspected. Check that it exists and is readable, or choose another local folder.", exception);
        }

        for (var number = 1; number <= CandidateLimit; number++)
        {
            token.ThrowIfCancellationRequested();
            var name = "feature-Workspace" + number.ToString("D2", CultureInfo.InvariantCulture);
            if (!occupiedNames.Contains(name) && !savedRoots.Contains(Path.Combine(parent, name))) return name;
        }
        throw new InvalidOperationException("No available default workspace name was found. Enter a different workspace name.");
    }

    private static string NormalizeFolder(string folder, string errorMessage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.Any(char.IsControl) || !Path.IsPathFullyQualified(folder))
                throw new ArgumentException();
            var normalized = Path.GetFullPath(folder);
            if (normalized.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                normalized = "\\\\" + normalized[8..];
            else if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) && normalized.Length > 5 && normalized[5] == ':')
                normalized = normalized[4..];
            return Path.TrimEndingDirectorySeparator(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException(errorMessage, exception);
        }
    }
}
