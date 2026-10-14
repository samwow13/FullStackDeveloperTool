using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Reads local routing evidence only. Never reads credential values or verifies remote access.</summary>
internal static class GitActiveConnectionService
{
    internal static GitActiveConnectionInfo NoRemote(GitRepositoryState state)
    {
        GitActiveConnectionInfo connection;
        if (state.Error is not null || state.Snapshot is null)
            connection = new() { Detail = state.Error ?? "Repository information could not be read. Refresh Git status." };
        else if (!state.Snapshot.IsRepository)
            connection = new()
            {
                Label = "No Git repository",
                HeaderLabel = "Git · No repository",
                Kind = GitActiveConnectionKind.NoRepository,
                Detail = "This folder is not a Git repository. Open Git to set one up."
            };
        else if (state.Snapshot.Remotes.Count == 0)
            connection = new()
            {
                Label = "Local Git · no remotes",
                HeaderLabel = "Git · Local only",
                Kind = GitActiveConnectionKind.NoRemote,
                Detail = "This is a local Git repository with no remotes. Open Git Config to choose a hosting provider and destination."
                    + (state.LedgerError is null ? "" : "\n" + state.LedgerError)
            };
        else if (state.LedgerError is not null)
            connection = new() { Detail = state.LedgerError };
        else
            connection = new()
            {
                Label = "Choose an active connection",
                HeaderLabel = "Git · Choose connection",
                Kind = GitActiveConnectionKind.SelectionRequired,
                Detail = state.SelectionError ?? "Remotes are configured. Choose the active connection in Git Config."
            };
        return WithRepositoryStatus(connection, state);
    }

    internal static async Task<GitActiveConnectionInfo> ReadAsync(GitRepositoryState state, CancellationToken token)
    {
        if (state.ActiveRemote is not { } remote) return NoRemote(state);
        var fetchProvider = remote.Provider ?? GitConnectionService.InferProvider(remote.FetchUrl);
        var pushProvider = remote.FetchUrl == remote.PushUrl ? fetchProvider
            : GitConnectionService.InferProvider(remote.PushUrl);
        var destinations = $"Remote: {remote.Name}\nFetch/pull: {remote.FetchUrl}\nPush: {remote.PushUrl}";
        try
        {
            var fetchTarget = await AzureDevOpsTokenGitAuthentication.ReadRemoteTokenTargetAsync(
                state.RepositoryRoot, remote.Name, false, remote.FetchUrl, token).ConfigureAwait(false);
            var fetch = DescribeRoute(remote.FetchUrl, fetchProvider, fetchTarget);
            var pushTarget = await AzureDevOpsTokenGitAuthentication.ReadRemoteTokenTargetAsync(
                state.RepositoryRoot, remote.Name, true, remote.PushUrl, token).ConfigureAwait(false);
            var push = remote.FetchUrl == remote.PushUrl && fetchTarget == pushTarget ? fetch
                : DescribeRoute(remote.PushUrl, pushProvider, pushTarget);
            return WithRepositoryStatus(new()
            {
                Label = (fetch.Label == push.Label ? fetch.Label : "Different fetch/push connections") + " · " + remote.Name,
                HeaderLabel = fetch.Label == push.Label ? fetch.HeaderLabel : "Git · Mixed connections",
                Kind = fetch.Label == push.Label ? fetch.Kind : GitActiveConnectionKind.Mixed,
                Detail = destinations + "\n\n" + (fetch.Label == push.Label && fetch.Detail == push.Detail
                    ? fetch.Detail : $"Fetch/pull: {fetch.Detail}\nPush: {push.Detail}")
                    + "\nLocal commits use the configured Git author. Remote operations verify authentication and repository permissions."
            }, state);
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Git configuration can contain secrets. Return fixed, value-free diagnostics only.
            var reason = exception switch
            {
                TimeoutException or OperationCanceledException => "The local Git connection read timed out. Refresh Git status to retry.",
                InvalidOperationException => "The selected remote's token binding, URL rewrite, or saved token store could not be matched. Review Git Config and reconnect.",
                _ => "Local Git connection routing could not be read. Refresh Git status and review Git Config."
            };
            return WithRepositoryStatus(new()
            {
                Label = (fetchProvider == GitHostingProvider.AzureDevOps ? "DevOps connection unavailable"
                    : "Connection unavailable") + " · " + remote.Name,
                HeaderLabel = fetchProvider == GitHostingProvider.AzureDevOps
                    ? "Git · DevOps · Unavailable" : "Git · Connection unavailable",
                Detail = destinations + "\n\n" + reason
            }, state);
        }
    }

    private static GitActiveConnectionInfo WithRepositoryStatus(GitActiveConnectionInfo connection, GitRepositoryState state)
    {
        if (state.Error is not null || state.Snapshot is null)
            return connection with { RepositoryDetail = state.Error ?? connection.RepositoryDetail };
        var snapshot = state.Snapshot;
        if (!snapshot.IsRepository)
            return connection with
            {
                RepositoryStatus = "No Git repository",
                RepositoryDetail = $"Folder: {snapshot.Folder}\nNo Git repository was found.",
                BranchLabel = "No branch"
            };

        var branch = snapshot.IsDetached ? "Detached HEAD"
            : string.IsNullOrWhiteSpace(snapshot.Branch) ? "No named branch" : $"Branch: {snapshot.Branch}";
        if (snapshot.IsDetached && snapshot.HeadCommit.Length > 0)
            branch += " · " + snapshot.HeadCommit[..Math.Min(8, snapshot.HeadCommit.Length)];
        if (snapshot.IsUnborn) branch += " · no commits yet";
        var remote = snapshot.Remotes.Count == 0 ? "no remotes"
            : state.ActiveRemote is { } active ? $"Remote: {active.Name}"
            : $"{snapshot.Remotes.Count} remote{(snapshot.Remotes.Count == 1 ? "" : "s")} · no active connection";
        var detail = $"Repository: {snapshot.RepositoryRoot ?? state.RepositoryRoot}\n{branch}\n"
            + (snapshot.IsUnborn ? "No commits have been created."
                : snapshot.HeadCommit.Length > 0 ? $"HEAD: {snapshot.HeadCommit}" : "HEAD commit unavailable.")
            + $"\nRemotes: {snapshot.Remotes.Count}"
            + (snapshot.Upstream is { Length: > 0 } upstream ? $"\nUpstream: {upstream}" : "\nNo upstream branch configured.");
        return connection with
        {
            BranchLabel = branch,
            RepositoryStatus = branch + " · " + remote,
            RepositoryDetail = detail + "\n\n" + connection.Detail
        };
    }

    private static GitActiveConnectionInfo DescribeRoute(string url, GitHostingProvider? provider, string? tokenTarget)
    {
        if (tokenTarget is not null)
        {
            var project = AzureDevOpsImportService.ParseProjectUrl(tokenTarget);
            var scope = project.IsHosted ? $"Azure DevOps organization: {project.Organization}."
                : $"Azure DevOps Server collection: {project.CollectionUrl}.";
            var hasToken = AzureDevOpsTokenStore.HasSavedToken(tokenTarget);
            return new()
            {
                Label = hasToken ? "DevOps token" : "DevOps token missing",
                HeaderLabel = hasToken ? "Git · DevOps · Token" : "Git · DevOps · Token missing",
                Kind = hasToken ? GitActiveConnectionKind.DevOpsToken : GitActiveConnectionKind.MissingToken,
                Detail = hasToken
                    ? $"Saved Launcher personal access token is selected for this HTTPS connection. {scope}"
                    : $"This remote requires a Launcher personal access token, but none is saved for its scope. {scope} Open Connect with Azure DevOps Token to reconnect."
            };
        }
        var ssh = url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || !url.Contains("://", StringComparison.Ordinal);
        var providerName = provider switch
        {
            GitHostingProvider.GitHub => "GitHub",
            GitHostingProvider.AzureDevOps => "Azure DevOps",
            _ => "Unknown provider"
        };
        var headerProvider = provider switch
        {
            GitHostingProvider.GitHub => "GitHub",
            GitHostingProvider.AzureDevOps => "DevOps",
            _ => "Other"
        };
        return new()
        {
            Label = ssh ? $"{providerName} · SSH" : $"{providerName} · Git credentials",
            HeaderLabel = $"Git · {headerProvider} · " + (ssh ? "SSH" : "Git credentials"),
            Kind = ssh ? GitActiveConnectionKind.Ssh : provider switch
            {
                GitHostingProvider.GitHub => GitActiveConnectionKind.GitHubCredentials,
                GitHostingProvider.AzureDevOps => GitActiveConnectionKind.DevOpsCredentials,
                _ => GitActiveConnectionKind.OtherCredentials
            },
            Detail = ssh ? $"{providerName} connection uses Git's SSH configuration and keys."
                : $"{providerName} connection uses Git's configured HTTPS credentials. No Launcher DevOps token is selected."
        };
    }
}
