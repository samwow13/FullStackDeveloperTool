using System.IO;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    private static async Task<GitReleaseBranchSuggestion> ReadReleaseBranchSuggestionAsync(string root, string remote,
        GitRepositorySnapshot snapshot, CancellationToken token)
    {
        ValidateRemoteName(remote);
        var prefix = remote + "/";
        var names = snapshot.Branches.Where(branch => branch.IsRemote && branch.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(branch => branch.Name[prefix.Length..]).ToArray();
        try
        {
            var symbolic = await GitAsync(root, ["symbolic-ref", "--quiet", $"refs/remotes/{remote}/HEAD"], token).ConfigureAwait(false);
            if (symbolic.ExitCode is not (0 or 1)) ThrowCommandFailure(symbolic);
            var refPrefix = $"refs/remotes/{remote}/";
            var target = symbolic.ExitCode == 0 ? symbolic.Output.TrimEnd('\r', '\n') : "";
            var defaultBranch = target.StartsWith(refPrefix, StringComparison.Ordinal) ? target[refPrefix.Length..] : null;
            if (defaultBranch is not null && !names.Contains(defaultBranch, StringComparer.Ordinal)) defaultBranch = null;
            var suggestion = GitReleaseBranchDetection.Detect(names, defaultBranch);
            return suggestion with
            {
                RemoteDefaultBranch = defaultBranch,
                Detail = "Based on locally cached remote refs. " + suggestion.Detail
            };
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // An optional suggestion must not prevent ordinary Git work. Keep the
            // diagnostic available while the chooser can read and confirm live names.
            return new(null, "Release source auto-detection is unavailable. Choose the source in Config.\n"
                + SensitiveDataProtection.Redact(exception.Message));
        }
    }
}
