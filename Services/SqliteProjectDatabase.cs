using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Validates a project SQLite file reference without opening or creating the database.</summary>
public static class SqliteProjectDatabase
{
    public static string? NormalizePath(string? path, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException("Choose a local SQLite file inside the project root.");

        try
        {
            var root = Path.GetFullPath(projectRoot);
            var fullPath = Path.GetFullPath(path, root);
            var relative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relative) || relative is "." or ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Choose a SQLite file inside the project root.");
            foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith(' ') || part.EndsWith('.'))
                    throw new ArgumentException("The SQLite file path contains an invalid folder or file name.");
                var name = part.Split('.')[0].TrimEnd(' ');
                if (IsReservedName(name))
                    throw new ArgumentException("The SQLite file path contains a reserved Windows folder or file name.");
            }
            if (!IsSupportedExtension(Path.GetExtension(fullPath)))
                throw new ArgumentException("Choose a SQLite file ending in .db, .sqlite, or .sqlite3.");
            return relative;
        }
        catch (Exception ex) when (ex is NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The SQLite file path is invalid.", ex);
        }
    }

    public static string? ValidateSelection(string? path, string projectRoot)
    {
        var relative = NormalizePath(path, projectRoot);
        if (relative is null) return null;
        var fullPath = Path.GetFullPath(relative, Path.GetFullPath(projectRoot));
        if (Directory.Exists(fullPath))
            throw new ArgumentException("Choose a SQLite file, not a folder.");
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
            throw new ArgumentException("The SQLite file's parent folder does not exist. Choose an existing folder.");
        return relative;
    }

    private static bool IsSupportedExtension(string extension) =>
        extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedName(string name) =>
        name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        name.Length == 4 && name[3] is >= '1' and <= '9' &&
        (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
}
