using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Configured launcher identity and its root/service folders.</summary>
public sealed record CodexActivityProjectScope(string Id, string Name, IReadOnlyList<string> Folders);

/// <summary>Passive identity evidence for one member of a Codex chat family.</summary>
public sealed record CodexActivityProjectMember(string Id, string ProjectPath)
{
    // Supply structured, executed-tool working folders newest first. Never use
    // chat titles, prompt text, or arbitrary path mentions as ownership evidence.
    public IReadOnlyList<string> WorkingFolders { get; init; } = [];
}

/// <summary>
/// Assigns a complete chat family to at most one configured project. The root
/// chat remains authoritative; children provide fallback only when it has no
/// ownership evidence. Repository inspection reads only .git and commondir.
/// Create one resolver per projection so filesystem metadata can refresh.
/// </summary>
public sealed class CodexActivityProjectResolver
{
    private readonly ScopedProject[] _projects;
    private readonly Dictionary<string, ScopedProject> _projectsById;
    private readonly Dictionary<string, RepositoryIdentity?> _repositories = new(StringComparer.OrdinalIgnoreCase);

    public CodexActivityProjectResolver(IReadOnlyList<CodexActivityProjectScope> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects.Where(project => !string.IsNullOrWhiteSpace(project.Id))
            .GroupBy(project => project.Id, StringComparer.Ordinal).Select(group => group.First())
            .Select((project, order) => new ScopedProject(project, order,
                project.Folders.Select(NormalizePath).Where(path => path is not null)
                    .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
        _projectsById = _projects.ToDictionary(project => project.Scope.Id, StringComparer.Ordinal);
    }

    public CodexActivityProjectScope? Resolve(CodexActivityProjectMember root,
        IReadOnlyList<CodexActivityProjectMember> members,
        IReadOnlyDictionary<string, string>? explicitProjectIds = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(members);
        if (ExplicitProject(root.Id, explicitProjectIds) is { } explicitRoot) return explicitRoot.Scope;
        if (MatchPath(root.ProjectPath) is { } rootFolder) return rootFolder.Project.Scope;
        foreach (var folder in root.WorkingFolders)
            if (MatchPath(folder) is { } rootWork) return rootWork.Project.Scope;

        var children = members.Where(member => member.Id != root.Id)
            .GroupBy(member => member.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var explicitChildren = children.Select(member => ExplicitProject(member.Id, explicitProjectIds))
            .Where(project => project is not null).Select(project => project!)
            .DistinctBy(project => project.Scope.Id, StringComparer.Ordinal).ToArray();
        if (explicitChildren.Length == 1) return explicitChildren[0].Scope;
        // Conflicting explicit child ownership cannot safely select a project.
        if (explicitChildren.Length > 1) return null;

        var childFolders = children.Select(member => MatchPath(member.ProjectPath))
            .Where(match => match is not null).Select(match => match!).ToArray();
        if (BestMatch(childFolders) is { } childFolder) return childFolder.Project.Scope;

        // Each member supplies its newest matching executed working folder.
        var childWork = children.Select(member => member.WorkingFolders.Select(MatchPath)
                .FirstOrDefault(match => match is not null))
            .Where(match => match is not null).Select(match => match!).ToArray();
        return BestMatch(childWork)?.Project.Scope;
    }

    private ScopedProject? ExplicitProject(string id, IReadOnlyDictionary<string, string>? mappings) =>
        mappings is not null && mappings.TryGetValue(id, out var projectId) && !string.IsNullOrWhiteSpace(projectId)
            ? _projectsById.GetValueOrDefault(projectId) : null;

    private ProjectMatch? MatchPath(string? candidate)
    {
        var path = NormalizePath(candidate);
        if (path is null) return null;
        var direct = _projects.SelectMany(project => project.Folders.Where(folder => IsWithin(path, folder))
            .Select(folder => new ProjectMatch(project, 0, folder.Length))).ToArray();
        if (BestMatch(direct) is { } directMatch) return directMatch;

        var repository = RepositoryFor(path);
        if (repository is null) return null;
        var relative = RelativePath(path, repository.WorktreeRoot);
        var linked = new List<ProjectMatch>();
        foreach (var project in _projects)
        foreach (var folder in project.Folders)
        {
            var configured = RepositoryFor(folder);
            if (configured is null || !string.Equals(repository.CommonDirectory,
                    configured.CommonDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve nested scopes within the same repository. A worktree root
            // does not belong to every child project in that repository.
            var configuredRelative = RelativePath(folder, configured.WorktreeRoot);
            if (IsWithinRelative(relative, configuredRelative))
                linked.Add(new ProjectMatch(project, 1, configuredRelative.Length));
        }
        return BestMatch(linked);
    }

    private static ProjectMatch? BestMatch(IEnumerable<ProjectMatch> matches) =>
        matches.OrderBy(match => match.Kind).ThenByDescending(match => match.Specificity)
            .ThenBy(match => match.Project.Order).ThenBy(match => match.Project.Scope.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private RepositoryIdentity? RepositoryFor(string path)
    {
        if (_repositories.TryGetValue(path, out var cached)) return cached;
        RepositoryIdentity? repository = null;
        try
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath))
                {
                    repository = new(NormalizePath(gitPath)!, NormalizePath(directory.FullName)!);
                    break;
                }
                if (!File.Exists(gitPath)) continue;
                var pointer = ReadSmallPointer(gitPath);
                if (pointer is null || !pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) break;
                var target = NormalizePath(Path.GetFullPath(pointer[7..].Trim(), directory.FullName));
                if (target is null || !Directory.Exists(target)) break;
                var commonPath = Path.Combine(target, "commondir");
                var common = ReadSmallPointer(commonPath);
                if (File.Exists(commonPath) && string.IsNullOrWhiteSpace(common)) break;
                var commonDirectory = common is null ? target : NormalizePath(Path.GetFullPath(common, target));
                if (commonDirectory is not null && Directory.Exists(commonDirectory))
                    repository = new(commonDirectory, NormalizePath(directory.FullName)!);
                break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException or System.Security.SecurityException) { }
        _repositories[path] = repository;
        return repository;
    }

    private static string? ReadSmallPointer(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 8192) return null;
        using var reader = new StreamReader(stream);
        var buffer = new char[8193];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return count <= 8192 ? new string(buffer, 0, count).Trim() : null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            if (!Path.IsPathFullyQualified(path)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static bool IsWithin(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string RelativePath(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? "" :
            path[(root.Length + (Path.EndsInDirectorySeparator(root) ? 0 : 1))..];

    private static bool IsWithinRelative(string candidate, string parent) => parent.Length == 0 || IsWithin(candidate, parent);

    private sealed record ScopedProject(CodexActivityProjectScope Scope, int Order, string[] Folders);
    private sealed record ProjectMatch(ScopedProject Project, int Kind, int Specificity);
    private sealed record RepositoryIdentity(string CommonDirectory, string WorktreeRoot);
}
