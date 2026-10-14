using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    public static Task<string> SelectActiveConnectionAsync(string root, GitRemoteInfo reviewedRemote,
        CancellationToken token = default) => InRepositoryAsync(root, token, async snapshot =>
    {
        // Selection changes only the Launcher preference. Keep the tracked remote
        // selectable during recovery; push/merge retain their own operation guards.
        var current = snapshot.Remotes.SingleOrDefault(remote => remote.Name == reviewedRemote.Name);
        if (current is not { UrlCanCopy: true }
            || AgentGitChangeStore.ConnectionId(current) != AgentGitChangeStore.ConnectionId(reviewedRemote))
            throw new InvalidOperationException("This connection changed since it was displayed. Refresh Git Config and review its destinations before selecting it.");

        await AgentGitChangeStore.SelectConnectionAsync(root, current.Name,
            AgentGitChangeStore.ConnectionId(current), token).ConfigureAwait(false);
        return $"{current.Name} is now the active connection for Launcher fetch, pull, push, and change summaries in this checkout.";
    });
}
