using System.IO;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CancellationTokenSource _branchLifetime = new();
    private readonly HashSet<ProjectViewModel> _refreshingBranchProjects = [];
    private readonly Dictionary<string, Task<GitBranchSnapshot>> _branchReads = new(StringComparer.OrdinalIgnoreCase);

    public ProjectViewModel? SelectedProjectItem =>
        ProjectItems.FirstOrDefault(item => ReferenceEquals(item.Profile, SelectedProject));

    // Each project refreshes independently of process/HTTP checks and other projects.
    private async Task RefreshProjectBranchesAsync()
    {
        if (_closing || _closed) return;
        await Task.WhenAll(ProjectItems.ToArray().Select(RefreshProjectBranchAsync));
    }

    private async Task RefreshProjectBranchAsync(ProjectViewModel project)
    {
        if (!_refreshingBranchProjects.Add(project)) return;
        try
        {
            var folders = BranchFolders(project);
            var results = await Task.WhenAll(folders.Select(async folder =>
                (Folder: folder, Snapshot: await ReadBranchFolderAsync(folder.Directory))));

            // Profile replacement/removal or an inline rename can happen while reading.
            if (_closing || _closed || !ProjectItems.Contains(project) || !folders.SequenceEqual(BranchFolders(project))) return;

            if (results.All(result => result.Snapshot.State == GitBranchState.NotRepository))
            {
                project.UpdateBranches([new("Not a Git repository", string.Join(Environment.NewLine + Environment.NewLine,
                    results.Select(result => $"{result.Folder.Label}: {result.Folder.Directory}\n{result.Snapshot.Detail}")))]);
                return;
            }

            // A container folder commonly holds separate API/frontend repositories.
            // Omit only that ordinary non-repository root, retaining missing/unreadable folders.
            var relevant = results.Where(result => !(result.Folder.IsRoot &&
                result.Snapshot.State == GitBranchState.NotRepository && results.Any(other => other.Snapshot.HasRepository)));
            var repositories = relevant.GroupBy(result => result.Snapshot.RepositoryPath ?? result.Folder.Directory,
                StringComparer.OrdinalIgnoreCase).ToArray();
            var branches = repositories.Select(repository =>
            {
                var snapshot = repository.First().Snapshot;
                if (repository.Select(result => (result.Snapshot.State, result.Snapshot.DisplayText)).Distinct().Skip(1).Any())
                    snapshot = new(snapshot.RepositoryPath, "Branch changing…",
                        "Git information changed during this check. The next refresh will read it again.", snapshot.HasRepository, GitBranchState.Unavailable);
                var label = string.Join(" / ", repository.Select(result => result.Folder.Label).Distinct());
                var branch = snapshot.State == GitBranchState.Branch ? $"Branch: {snapshot.DisplayText}" : snapshot.DisplayText;
                var display = repositories.Length == 1 ? branch : $"{label} · {branch}";
                var detail = string.Join(Environment.NewLine, repository.Select(result => $"{result.Folder.Label}: {result.Folder.Directory}"))
                    + Environment.NewLine + Environment.NewLine + snapshot.Detail;
                return new ProjectBranchViewModel(display, detail);
            }).ToArray();
            project.UpdateBranches(branches);
        }
        catch (OperationCanceledException) when (_branchLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closing && !_closed && ProjectItems.Contains(project))
                project.UpdateBranches([new("Branch unavailable", "The configured folders could not be read. Check their paths and access permissions, then refresh.")]);
        }
        finally { _refreshingBranchProjects.Remove(project); }
    }

    private async Task<GitBranchSnapshot> ReadBranchFolderAsync(string directory)
    {
        // Native filesystem probes cannot always be canceled (for example a disconnected
        // drive). Retain one pending read per folder so timed-out refreshes never pile up
        // duplicate workers; other folders remain free to refresh.
        if (!_branchReads.TryGetValue(directory, out var read))
        {
            _branchReads[directory] = read = GitBranchReader.ReadAsync(directory, _branchLifetime.Token);
        }
        try
        {
            return await read.WaitAsync(TimeSpan.FromSeconds(2), _branchLifetime.Token);
        }
        catch (TimeoutException)
        {
            return new(null, "Branch check timed out", "The folder is taking too long to respond. Branch information will refresh when it becomes available.",
                false, GitBranchState.Unavailable);
        }
        finally
        {
            if (read.IsCompleted && _branchReads.TryGetValue(directory, out var current) && ReferenceEquals(current, read))
                _branchReads.Remove(directory);
        }
    }

    private BranchFolder[] BranchFolders(ProjectViewModel project) =>
        new[] { new BranchFolder("Project root", Path.TrimEndingDirectorySeparator(_store.ResolveRoot(project.Profile)), true) }
            .Concat(project.Profile.Services.Select(service => new BranchFolder(service.Name,
                Path.TrimEndingDirectorySeparator(_store.ResolveWorkingDirectory(project.Profile, service)), false)))
            .ToArray();

    private sealed record BranchFolder(string Label, string Directory, bool IsRoot);
}
