using System.IO;
using System.Security.Cryptography;
using System.Text;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private sealed record AgentGitFolder(string Directory, string? RepositoryRoot, string State);

    private sealed record AgentGitRemote(string ConnectionId, string RemoteName, string? Provider,
        string FetchUrl, string PushUrl, bool IsActive);

    private sealed record AgentGitRepository(string RepositoryId, string RepositoryRoot,
        string[] ConfiguredFolders, string? Branch, bool IsDetached, string State,
        string? SelectionSource, string? ActiveConnectionId, bool CanRecordChanges,
        string? Detail, IReadOnlyList<AgentGitRemote> Connections);

    private sealed record AgentGitDiscovery(ProjectProfile Project, string[] CapturedFolders,
        AgentGitFolder[] Folders, AgentGitRepository[] Repositories)
    {
        public string? Warning { get; init; }
    }

    private async Task<object> ListAgentGitConnectionsAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var discovery = await DiscoverAgentGitAsync(RequireProject(request.ProjectId), null, token);
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = discovery.Project.Id,
            observedUtc = DateTime.UtcNow,
            authentication = "not_checked",
            warning = discovery.Warning,
            folders = discovery.Folders,
            repositories = discovery.Repositories
        };
    }

    private async Task<object> RecordAgentGitChangesAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        _ = _agentCoordination.SessionIdentity(project.Id, request.SessionToken ?? "");
        if (request.RepositoryId is not { Length: 64 } || request.ConnectionId is not { Length: > 0 and <= 256 }
            || request.Branch is not { Length: > 0 and <= 1024 })
            throw new ArgumentException("Use the repositoryId, connectionId, and current branch returned by launcher_git_connections.");
        if (request.UpdateId is not { Length: > 0 and <= 100 }
            || request.Bullets is not { Length: >= 1 and <= 5 }
            || request.Bullets.Any(bullet => string.IsNullOrWhiteSpace(bullet) || bullet.Length > 120))
            throw new ArgumentException("Supply a stable updateId up to 100 characters and 1-5 short bullets, each at most 120 characters.");

        var discovery = await DiscoverAgentGitAsync(project, request.RepositoryId, token);
        var repository = discovery.Repositories.SingleOrDefault(item => item.RepositoryId == request.RepositoryId)
            ?? throw new InvalidOperationException("This repository is not resolved from this project's configured folders. Refresh launcher_git_connections.");
        if (!repository.CanRecordChanges || repository.ActiveConnectionId != request.ConnectionId
            || repository.Branch != request.Branch)
            throw new InvalidOperationException("The active Git connection or branch is unavailable or changed. Refresh launcher_git_connections; choose an active connection in the WPF Git workspace if needed.");
        var connection = repository.Connections.Single(item => item.IsActive);

        var current = await ReadBranchFolderAsync(repository.ConfiguredFolders[0]).WaitAsync(token);
        if (current.State != GitBranchState.Branch || current.DisplayText != request.Branch
            || !string.Equals(current.RepositoryPath, repository.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured folder or current branch changed before this report was saved. Refresh launcher_git_connections.");

        // Project/session identity is rechecked after asynchronous filesystem work. A report never
        // inherits a session from another project and never acquires service control authority.
        EnsureAgentGitProjectCurrent(discovery);
        _ = _agentCoordination.SessionIdentity(project.Id, request.SessionToken ?? "");
        token.ThrowIfCancellationRequested();
        var entry = await AgentGitChangeStore.RecordAsync(repository.RepositoryRoot, request.Branch,
            connection.RemoteName, connection.ConnectionId, request.UpdateId, request.Bullets, token);
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = project.Id,
            repositoryId = repository.RepositoryId,
            connectionId = connection.ConnectionId,
            branch = repository.Branch,
            updateId = entry.UpdateId,
            entryId = entry.Id,
            bullets = entry.Bullets,
            consumedCommitId = entry.ConsumedCommitId,
            status = entry.ConsumedCommitId is null ? "queued_for_commit_review" : "already_included_in_commit",
            gitMutationPerformed = false
        };
    }

    private async Task<AgentGitDiscovery> DiscoverAgentGitAsync(ProjectProfile project,
        string? requestedRepositoryId, CancellationToken token)
    {
        var folders = AgentGitFolders(project);
        if (folders.Length > 64)
            throw new InvalidOperationException("This project has too many configured Git folders for one bridge request. Use the WPF Git workspace.");
        using var limiter = new SemaphoreSlim(4);
        // Use the same bounded metadata discovery as the Git workspace and Next commit.
        // Non-service checkouts such as ORKidsDatabase must also remain reportable through
        // the bridge, including a fresh discovery before saving a change summary.
        var discovered = await GitRepositoryDiscovery.DiscoverAsync(folders.Select((folder, index) =>
            new GitRepositoryDiscoveryFolder(index == 0 ? "Project root" : Path.GetFileName(folder), folder,
                ScanImmediateChildren: index == 0)).ToArray(), token);
        // Preserve the original configured-folder mapping for bridge callers and the final
        // report recheck, even when several source folders resolve to the same checkout.
        var metadata = discovered.ResolvedFolders.Select(folder => new AgentGitFolder(folder.Directory,
            folder.Snapshot.RepositoryPath, folder.Snapshot.State.ToString())).ToArray();
        var groups = metadata.Where(item => item.RepositoryRoot is not null)
            .GroupBy(item => item.RepositoryRoot!, StringComparer.OrdinalIgnoreCase)
            .Where(group => requestedRepositoryId is null || AgentGitRepositoryId(group.Key) == requestedRepositoryId)
            .ToArray();
        var repositories = await Task.WhenAll(groups.Select(async group =>
        {
            await limiter.WaitAsync(token);
            try { return await ReadAgentGitRepositoryAsync(group.Key, group.Select(item => item.Directory).ToArray(), token); }
            finally { limiter.Release(); }
        }));
        var discovery = new AgentGitDiscovery(project, folders, metadata, repositories) { Warning = discovered.Warning };
        EnsureAgentGitProjectCurrent(discovery);
        return discovery;
    }

    private static async Task<AgentGitRepository> ReadAgentGitRepositoryAsync(string root,
        string[] configuredFolders, CancellationToken token)
    {
        var repositoryId = AgentGitRepositoryId(root);
        try
        {
            var snapshot = await GitRepositoryService.ReadAsync(root, token);
            if (!snapshot.IsRepository || snapshot.RepositoryRoot is null
                || !string.Equals(Path.TrimEndingDirectorySeparator(snapshot.RepositoryRoot),
                    Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configured repository changed during discovery.");
            if (snapshot.Remotes.Count > 64)
                throw new InvalidOperationException("Too many remote connections for one bridge response.");

            // ReadAsync has already withheld unsafe and multi-target URLs. Never expose or select
            // one of those placeholders as a connection, even when it is the only saved remote.
            var eligible = snapshot.Remotes.Where(remote => remote.UrlCanCopy).ToArray();
            var selection = await AgentGitChangeStore.ReadSelectionAsync(root, token);
            GitRemoteInfo? active = null;
            string? selectionSource = null;
            string? detail = null;
            if (selection is not null)
            {
                active = eligible.SingleOrDefault(remote => remote.Name == selection.RemoteName
                    && AgentGitChangeStore.ConnectionId(remote) == selection.ConnectionId);
                selectionSource = "saved_workspace_selection";
                if (active is null) detail = "The saved active connection changed or was removed. Select a connection in the WPF Git workspace.";
            }
            else
            {
                active = eligible.SingleOrDefault(remote => snapshot.Upstream?.StartsWith(remote.Name + "/", StringComparison.Ordinal) == true);
                if (active is not null) selectionSource = "current_branch_upstream";
                else if (snapshot.Upstream is null && snapshot.Remotes.Count == 1 && eligible.Length == 1)
                {
                    active = eligible[0];
                    selectionSource = "only_configured_connection";
                }
                else detail = eligible.Length == 0
                    ? "No supported credential-free connection is configured. Complete Git setup in the WPF app."
                    : snapshot.Upstream is not null
                        ? "The current branch's upstream does not identify a supported connection. Select the active connection in the WPF Git workspace."
                        : "Several connections are configured. Select the active connection in the WPF Git workspace.";
            }
            var activeId = active is null ? null : AgentGitChangeStore.ConnectionId(active);
            var connections = eligible.Select(remote => new AgentGitRemote(
                AgentGitChangeStore.ConnectionId(remote), remote.Name,
                (remote.Provider ?? GitConnectionService.InferProvider(remote.FetchUrl))?.ToString(),
                remote.FetchUrl, remote.PushUrl, remote.Name == active?.Name)).ToArray();
            if (snapshot.IsDetached) detail = "Checkout has detached HEAD. Choose a named branch in the WPF Git workspace before reporting changes.";
            return new(repositoryId, root, configuredFolders, snapshot.Branch, snapshot.IsDetached,
                "available", selectionSource, activeId, active is not null && !snapshot.IsDetached,
                detail, connections);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Command failures can contain paths and arbitrary Git output. Keep bridge failures
            // value-free; the explicit WPF workspace owns detailed repair and trust review.
            return new(repositoryId, root, configuredFolders, null, false, "unavailable", null,
                null, false, "Git connection information could not be read. Open the WPF Git workspace to review setup or repository access.", []);
        }
    }

    private string[] AgentGitFolders(ProjectProfile project) =>
        new[] { _store.ResolveRoot(project) }
            .Concat(project.Services.Select(service => _store.ResolveWorkingDirectory(project, service)))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void EnsureAgentGitProjectCurrent(AgentGitDiscovery discovery)
    {
        if (_closing || _closed || _closeRequested
            || !ReferenceEquals(RequireProject(discovery.Project.Id), discovery.Project)
            || !discovery.CapturedFolders.SequenceEqual(AgentGitFolders(discovery.Project), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured project changed during Git inspection. Refresh launcher_projects and launcher_git_connections.");
    }

    private static string AgentGitRepositoryId(string root) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
}
