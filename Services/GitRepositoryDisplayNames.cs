using System.IO;

namespace FullStackLauncher.Services;

public static class GitRepositoryDisplayNames
{
    public static IReadOnlyDictionary<string, string> Create(string projectRoot, IEnumerable<string> repositoryRoots)
    {
        var roots = repositoryRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicateNames = roots.GroupBy(RepositoryName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roots.ToDictionary(root => root, root =>
        {
            var name = RepositoryName(root);
            if (!duplicateNames.Contains(name)) return name;
            var relative = Path.GetRelativePath(projectRoot, root);
            return relative == "." ? name + " (root)" : relative;
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static string RepositoryName(string root)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.IsNullOrEmpty(name) ? root : name;
    }
}
