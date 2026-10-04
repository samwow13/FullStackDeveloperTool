using System.IO;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Reads the Git workspace's cached comparison for the dashboard and delegates remote checks
/// to its existing committed-history simulation. Scheduling and agent exclusion belong to the caller.
/// </summary>
public static class DashboardGitComparisonService
{
    public static async Task<DashboardGitComparison> ReadAsync(DashboardGitComparisonScope scope,
        CancellationToken token = default)
    {
        try
        {
            var snapshot = await GitRepositoryService.ReadAsync(scope.RepositoryRoot, token).ConfigureAwait(false);
            await RequireScopeAsync(scope, snapshot, token).ConfigureAwait(false);
            GitRemoteComparison? comparison = null;
            GitPowerDetails? power = null;
            string? comparisonError = null;
            string? powerError = null;
            await GitRepositoryService.RunWithSnapshotAsync(snapshot, async currentToken =>
            {
                try
                {
                    comparison = await GitRepositoryService.ReadComparisonAsync(scope.RepositoryRoot,
                        scope.RemoteName, scope.Branch, currentToken).ConfigureAwait(false);
                }
                catch (GitCommandExitUnconfirmedException) { throw; }
                catch (OperationCanceledException) when (currentToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsReadFailure(exception)) { comparisonError = FailureMessage(exception); }
                try
                {
                    power = await GitRepositoryService.ReadPowerDetailsAsync(scope.RepositoryRoot,
                        scope.RemoteName, currentToken).ConfigureAwait(false);
                }
                catch (GitCommandExitUnconfirmedException) { throw; }
                catch (OperationCanceledException) when (currentToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsReadFailure(exception)) { powerError = FailureMessage(exception); }
                return "";
            }, token).ConfigureAwait(false);

            var current = await GitRepositoryService.ReadAsync(scope.RepositoryRoot, token).ConfigureAwait(false);
            await RequireScopeAsync(scope, current, token).ConfigureAwait(false);
            RequireSameSnapshot(snapshot, current);
            token.ThrowIfCancellationRequested();
            return new DashboardGitComparison
            {
                Scope = scope, Snapshot = snapshot, Comparison = comparison,
                UncommittedFiles = snapshot.Changes.Count,
                ReleaseBranch = power?.ReleaseBranch,
                UnavailableReason = comparisonError ?? comparison?.UnavailableReason,
                ConflictCheckUnavailableReason = powerError ?? ConflictCheckAvailability(snapshot, power),
                ReadAt = DateTimeOffset.UtcNow
            };
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            var message = FailureMessage(exception);
            return new DashboardGitComparison
            {
                Scope = scope, UnavailableReason = message, ConflictCheckUnavailableReason = message,
                ReadAt = DateTimeOffset.UtcNow
            };
        }
    }

    public static async Task<DashboardGitConflictCheck> CheckAsync(DashboardGitComparison prepared,
        CancellationToken token = default, IProgress<string>? progress = null,
        Func<CancellationToken, Task>? ensureCurrentContext = null)
    {
        var scope = prepared.Scope;
        if (!prepared.CanCheckConflicts || prepared.Snapshot is not { } reviewed
            || prepared.ReleaseBranch is not { Length: > 0 } source)
            return new DashboardGitConflictCheck
            {
                Scope = scope,
                UnavailableReason = prepared.ConflictCheckUnavailableReason ?? prepared.UnavailableReason
                    ?? "Choose a release branch in Git before running the conflict test."
            };

        try
        {
            if (ensureCurrentContext is not null) await ensureCurrentContext(token).ConfigureAwait(false);
            var current = await GitRepositoryService.ReadAsync(scope.RepositoryRoot, token).ConfigureAwait(false);
            await RequireScopeAsync(scope, current, token).ConfigureAwait(false);
            RequireSameSnapshot(reviewed, current);
            GitMergeCheckResult? result = null;
            await GitRepositoryService.RunWithSnapshotAsync(reviewed, async currentToken =>
            {
                var power = await GitRepositoryService.ReadPowerDetailsAsync(scope.RepositoryRoot,
                    scope.RemoteName, currentToken).ConfigureAwait(false);
                if (!string.Equals(power.ReleaseBranch, source, StringComparison.Ordinal))
                    throw new InvalidOperationException("The saved release branch changed. Refresh Git comparison before running the conflict test.");
                if (ConflictCheckAvailability(current, power) is { } unavailable)
                    throw new InvalidOperationException(unavailable);
                async Task EnsureReviewedScopeAsync(CancellationToken checkToken)
                {
                    if (ensureCurrentContext is not null) await ensureCurrentContext(checkToken).ConfigureAwait(false);
                    // The active connection ledger lives outside the Git snapshot fingerprint.
                    // Read it again after context validation, before each core effect. The core
                    // already owns its repository lease and validates the actual Git identity.
                    await RequireScopeAsync(scope, reviewed, checkToken).ConfigureAwait(false);
                    checkToken.ThrowIfCancellationRequested();
                }
                result = await GitRepositoryService.CheckReleaseMergeAsync(scope.RepositoryRoot,
                    scope.RemoteName, source, currentToken, progress, EnsureReviewedScopeAsync).ConfigureAwait(false);
                return "";
            }, token).ConfigureAwait(false);

            var final = await GitRepositoryService.ReadAsync(scope.RepositoryRoot, token).ConfigureAwait(false);
            await RequireScopeAsync(scope, final, token).ConfigureAwait(false);
            RequireSameSnapshot(reviewed, final);
            token.ThrowIfCancellationRequested();
            if (result is null || result.CurrentBranch != scope.Branch || result.CurrentCommit != reviewed.HeadCommit
                || result.Remote != scope.RemoteName || result.SourceBranch != source)
                throw new InvalidOperationException("The conflict-test identity could not be verified. No result is available.");
            return new DashboardGitConflictCheck { Scope = scope, Result = result };
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new DashboardGitConflictCheck { Scope = scope, UnavailableReason = FailureMessage(exception) };
        }
    }

    private static async Task RequireScopeAsync(DashboardGitComparisonScope scope, GitRepositorySnapshot snapshot,
        CancellationToken token)
    {
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null
            || !SameRoot(scope.RepositoryRoot, snapshot.RepositoryRoot))
            throw new InvalidOperationException("The selected repository changed or is unavailable. Open Git to review its folder.");
        if (snapshot.IsDetached || !string.Equals(snapshot.Branch, scope.Branch, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected branch changed or has detached HEAD. Refresh Git comparison before continuing.");
        var remote = snapshot.Remotes.SingleOrDefault(item => item.Name == scope.RemoteName && item.UrlCanCopy);
        if (remote is null || AgentGitChangeStore.ConnectionId(remote) != scope.ConnectionId)
            throw new InvalidOperationException("The selected Git connection changed or is unavailable. Open Git to review its connection.");

        // Match the preview's saved-selection / upstream / sole-remote rules. A stale dashboard
        // choice must not perform a remote check for a newly selected connection.
        var selection = await AgentGitChangeStore.ReadSelectionAsync(scope.RepositoryRoot, token).ConfigureAwait(false);
        if (selection is not null)
        {
            if (selection.RemoteName != scope.RemoteName || selection.ConnectionId != scope.ConnectionId)
                throw new InvalidOperationException("The active Git connection changed. Refresh Git comparison before continuing.");
            return;
        }
        var eligible = snapshot.Remotes.Where(item => item.UrlCanCopy).ToArray();
        var active = eligible.SingleOrDefault(item => snapshot.Upstream?.StartsWith(item.Name + "/", StringComparison.Ordinal) == true);
        if (active is null && snapshot.Upstream is null && snapshot.Remotes.Count == 1 && eligible.Length == 1)
            active = eligible[0];
        if (active?.Name != scope.RemoteName)
            throw new InvalidOperationException("The active Git connection is unavailable or changed. Select its connection in Git.");
    }

    private static string? ConflictCheckAvailability(GitRepositorySnapshot snapshot, GitPowerDetails? power) =>
        snapshot.IsDetached || snapshot.IsUnborn || snapshot.HeadCommit.Length == 0
            ? "Create or select a named local branch with an existing commit before running the conflict test."
        : snapshot.OperationState is not null
            ? "Finish the active Git operation before running the conflict test."
        : snapshot.Changes.Any(change => change.IsConflict)
            ? "Resolve the current merge conflicts before running the conflict test."
        : power is null
            ? "Git release settings could not be read. Open Git to review setup."
        : power.PendingSync is not null || !string.IsNullOrEmpty(power.RecoveryMessage)
            ? "A release merge is tracked. Open Git to review it before running the conflict test."
        : power.ReleaseBranch is not { Length: > 0 }
            ? "Choose a release branch in Git before running the conflict test."
        : null;

    private static bool SameRoot(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void RequireSameSnapshot(GitRepositorySnapshot expected, GitRepositorySnapshot current)
    {
        if (expected.StateFingerprint.Length == 0 || expected.StateFingerprint != current.StateFingerprint)
            throw new InvalidOperationException("The repository branch, files, or remote settings changed during Git inspection. Refresh before continuing.");
    }

    private static bool IsReadFailure(Exception exception) => exception is InvalidOperationException or IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException;

    private static string FailureMessage(Exception exception) => exception is OperationCanceledException or TimeoutException
        ? "Git inspection timed out. No conflict result was confirmed. Run the check manually after reviewing Git access."
        : SensitiveDataProtection.Redact(exception.Message);
}
