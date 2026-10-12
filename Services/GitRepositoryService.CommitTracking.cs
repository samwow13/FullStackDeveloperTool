using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

/// <summary>Fresh local upstream configuration. Cached ref presence does not establish remote branch presence.</summary>
public sealed record GitCommitTracking(string Branch, string? UpstreamRemote, string? UpstreamBranch, bool UpstreamRefAvailable);

public static partial class GitRepositoryService
{
    /// <summary>Verifies tracking without refreshing the working tree, contacting a remote, or taking a mutation lease.</summary>
    public static Task<GitCommitTracking> ReadCommitTrackingAsync(string root, string reviewedBranch, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            var fullRoot = RequireFolder(root);
            var top = await GitAsync(fullRoot, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
            EnsureSuccess(top);
            if (!string.Equals(RequireFolder(top.Output.TrimEnd('\r', '\n')), fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The repository root changed. Refresh and review before continuing.");
            await ValidateBranchAsync(fullRoot, reviewedBranch, token).ConfigureAwait(false);
            var expectedRef = "refs/heads/" + reviewedBranch;
            await RequireCommitTrackingBranchAsync(fullRoot, expectedRef, token).ConfigureAwait(false);

            var firstRef = await ReadCommitTrackingRefAsync(fullRoot, expectedRef, token).ConfigureAwait(false);
            var configuration = await ReadCommitTrackingConfigurationAsync(fullRoot, reviewedBranch, token).ConfigureAwait(false);
            var finalRef = await ReadCommitTrackingRefAsync(fullRoot, expectedRef, token).ConfigureAwait(false);
            var finalConfiguration = await ReadCommitTrackingConfigurationAsync(fullRoot, reviewedBranch, token).ConfigureAwait(false);
            await RequireCommitTrackingBranchAsync(fullRoot, expectedRef, token).ConfigureAwait(false);
            if (firstRef != finalRef || configuration != finalConfiguration)
                throw new InvalidOperationException("Local branch tracking changed while being verified. Close this review and refresh Git status.");

            if (configuration.Remote is null && configuration.MergeRef is null)
            {
                if (finalRef is { Upstream: not "" })
                    throw new InvalidOperationException("Local branch tracking could not be verified. Close this review and refresh Git status.");
                return new GitCommitTracking(reviewedBranch, null, null, false);
            }
            if (configuration.Remote is null || configuration.MergeRef is null
                || !configuration.MergeRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new InvalidOperationException("Local branch tracking is incomplete or unsupported. Review the branch's upstream settings with Git.");
            var upstreamBranch = configuration.MergeRef["refs/heads/".Length..];
            await ValidateBranchAsync(fullRoot, upstreamBranch, token).ConfigureAwait(false);
            if (configuration.Remote != ".") ValidateRemoteName(configuration.Remote);

            if (finalRef is null)
            {
                await RequireCommitTrackingBranchAsync(fullRoot, expectedRef, token).ConfigureAwait(false);
                return new GitCommitTracking(reviewedBranch, configuration.Remote, upstreamBranch, false);
            }
            if (finalRef.Remote != configuration.Remote || finalRef.MergeRef != configuration.MergeRef || finalRef.Upstream.Length == 0)
                throw new InvalidOperationException("Local upstream configuration could not be resolved. Review the branch's remote and fetch mapping with Git.");
            var available = await GitAsync(fullRoot, ["show-ref", "--verify", "--quiet", finalRef.Upstream], token).ConfigureAwait(false);
            if (available.ExitCode is not (0 or 1)) ThrowCommandFailure(available);
            await RequireCommitTrackingBranchAsync(fullRoot, expectedRef, token).ConfigureAwait(false);
            return new GitCommitTracking(reviewedBranch, configuration.Remote, upstreamBranch, available.ExitCode == 0);
        }, token);

    private static async Task RequireCommitTrackingBranchAsync(string root, string expectedRef, CancellationToken token)
    {
        var head = await GitAsync(root, ["symbolic-ref", "--quiet", "HEAD"], token).ConfigureAwait(false);
        if (head.ExitCode is not (0 or 1)) ThrowCommandFailure(head);
        if (head.ExitCode != 0 || head.Output.TrimEnd('\r', '\n') != expectedRef)
            throw new InvalidOperationException("The current branch changed. Close this review and refresh Git status.");
    }

    private sealed record CommitTrackingRef(string Upstream, string Remote, string MergeRef);

    private static async Task<CommitTrackingRef?> ReadCommitTrackingRefAsync(string root, string expectedRef, CancellationToken token)
    {
        var refs = await GitAsync(root,
            ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(upstream:remotename)%00%(upstream:remoteref)", expectedRef], token).ConfigureAwait(false);
        EnsureSuccess(refs);
        CommitTrackingRef? match = null;
        foreach (var line in refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length != 4 || fields.Any(field => field.Any(char.IsControl)))
                throw new InvalidOperationException("Git returned incomplete upstream information. Local branch tracking is unknown.");
            if (fields[0] != expectedRef) continue;
            if (match is not null)
                throw new InvalidOperationException("Git returned ambiguous upstream information. Local branch tracking is unknown.");
            match = new CommitTrackingRef(fields[1], fields[2], fields[3]);
        }
        return match;
    }

    private static async Task<(string? Remote, string? MergeRef)> ReadCommitTrackingConfigurationAsync(string root, string branch, CancellationToken token)
    {
        var prefix = "branch." + branch + ".";
        var pattern = "^" + Regex.Escape(prefix) + "(remote|merge)$";
        var result = await GitAsync(root, ["config", "--null", "--get-regexp", pattern], token).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.Output.Length == 0) return (null, null);
        EnsureSuccess(result);
        if (result.Output.Length == 0 || !result.Output.EndsWith('\0'))
            throw new InvalidOperationException("Git returned incomplete upstream settings. Local branch tracking is unknown.");
        string? remote = null;
        string? mergeRef = null;
        foreach (var entry in result.Output[..^1].Split('\0'))
        {
            var separator = entry.IndexOf('\n');
            if (separator <= 0)
                throw new InvalidOperationException("Git returned incomplete upstream settings. Local branch tracking is unknown.");
            var key = entry[..separator];
            var value = entry[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new InvalidOperationException("Local upstream settings contain an invalid value. Review the branch's upstream settings with Git.");
            if (key == prefix + "remote" && remote is null) remote = value;
            else if (key == prefix + "merge" && mergeRef is null) mergeRef = value;
            else throw new InvalidOperationException("Local upstream settings are ambiguous. Review the branch's upstream settings with Git.");
        }
        return (remote, mergeRef);
    }
}
