using System.IO;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async Task<object> ReadAgentGitStatusAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        if (request.RepositoryId is not { Length: 64 })
            throw new ArgumentException("Use a repositoryId returned by launcher_git_connections for this project.");

        // Discovery establishes membership; recheck folder identity around the fresh read so a
        // stale cache or a changed configuration cannot redirect inspection to another checkout.
        var discovery = await DiscoverAgentGitAsync(project, request.RepositoryId, token);
        var repository = discovery.Repositories.SingleOrDefault(item => item.RepositoryId == request.RepositoryId)
            ?? throw new InvalidOperationException("This repository is not resolved from this project's configured folders. Refresh launcher_git_connections.");
        await RequireAgentGitFoldersCurrentAsync(repository.RepositoryRoot, repository.ConfiguredFolders,
            discovery.CapturedFolders, token);

        Models.GitRepositorySnapshot snapshot;
        try
        {
            // The shared Git reader disables optional Git locks and never contacts a remote.
            snapshot = await GitRepositoryService.ReadAsync(repository.RepositoryRoot, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Do not return arbitrary command output or repository configuration values.
            throw new InvalidOperationException("Git status could not be read. Open the Git workspace to review repository access.");
        }
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(snapshot.RepositoryRoot),
                Path.TrimEndingDirectorySeparator(repository.RepositoryRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The repository changed during Git inspection. Refresh launcher_git_connections.");
        await RequireAgentGitFoldersCurrentAsync(repository.RepositoryRoot, repository.ConfiguredFolders,
            discovery.CapturedFolders, token);
        EnsureAgentGitProjectCurrent(discovery);

        var untracked = snapshot.Changes.Where(change => change.IndexStatus == "?" && change.WorkTreeStatus == "?").ToArray();
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = project.Id,
            repositoryId = repository.RepositoryId,
            repositoryRoot = repository.RepositoryRoot,
            observedUtc = DateTime.UtcNow,
            branch = snapshot.Branch,
            snapshot.IsDetached,
            snapshot.IsUnborn,
            snapshot.HeadCommit,
            snapshot.Upstream,
            snapshot.TrackingAvailable,
            ahead = snapshot.TrackingAvailable ? (int?)snapshot.Ahead : null,
            behind = snapshot.TrackingAvailable ? (int?)snapshot.Behind : null,
            stagedCount = snapshot.Changes.Count(change => change.HasStaged),
            unstagedCount = snapshot.Changes.Count(change => change.HasUnstaged && !(change.IndexStatus == "?" && change.WorkTreeStatus == "?")),
            untrackedCount = untracked.Length,
            conflictCount = snapshot.Changes.Count(change => change.IsConflict),
            operationState = snapshot.OperationState,
            trackingSource = "local cached upstream; no fetch performed"
        };
    }
}
