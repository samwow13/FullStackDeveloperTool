using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Supplies a saved PAT only to an explicitly bound repository and reviewed HTTPS URL.</summary>
internal static class AzureDevOpsTokenGitAuthentication
{
    private const string Mode = "azure-devops-token";

    internal sealed class Credential
    {
        internal string CloneUrl { get; }
        internal string Token { get; }
        internal string Encoded { get; }
        internal string Header => "Authorization: Basic " + Encoded;

        internal Credential(string cloneUrl, string token)
        {
            CloneUrl = cloneUrl;
            Token = token;
            Encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("pat:" + token));
        }

        internal string Redact(string value) => value.Replace(Token, "[REDACTED]", StringComparison.Ordinal)
            .Replace(Encoded, "[REDACTED]", StringComparison.Ordinal);
    }

    internal static async Task<Credential?> PrepareAsync(string root, IReadOnlyList<string> arguments,
        string? explicitCloneUrl, CancellationToken token)
    {
        var verb = arguments.FirstOrDefault(value => value is "clone" or "fetch" or "pull" or "push" or "ls-remote");
        if (verb is null) return null;
        if (verb == "clone" && explicitCloneUrl is null) return null;
        var separator = arguments.ToList().IndexOf("--");
        if (separator < 0 || separator + 1 >= arguments.Count)
            throw new InvalidOperationException("The Git network target is missing. Refresh before retrying.");
        var operand = arguments[separator + 1];
        string? cloneUrl;
        if (explicitCloneUrl is not null)
        {
            cloneUrl = ValidateTokenUrl(explicitCloneUrl);
            if (!string.Equals(operand, cloneUrl, StringComparison.Ordinal)) throw BindingChanged();
        }
        else cloneUrl = await FindBoundTargetAsync(root, operand, verb == "push", token).ConfigureAwait(false);
        if (cloneUrl is null) return null;

        // Inspect Git's effective rewrite before handing it any token. Runtime headers remain
        // URL-scoped and redirects are forbidden even if configuration changes after this read.
        var resolved = await GitRepositoryService.GitAsync(root,
            ["ls-remote", "--get-url", "--", cloneUrl], token).ConfigureAwait(false);
        if (resolved.ExitCode != 0 || resolved.Output.TrimEnd('\r', '\n') != cloneUrl) throw BindingChanged();
        return new Credential(cloneUrl, AzureDevOpsTokenStore.Read(cloneUrl));
    }

    internal static async Task<bool> UsesTokenAsync(string root, string cloneUrl, CancellationToken token) =>
        await FindBoundTargetAsync(root, cloneUrl, false, token).ConfigureAwait(false) is not null;

    internal static async Task BindOriginAsync(string root, string cloneUrl, CancellationToken token)
    {
        var clean = ValidateTokenUrl(cloneUrl);
        var binding = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--get-all", "remote.origin.launcherProviderTarget"], token).ConfigureAwait(false);
        if (binding.ExitCode != 0 || binding.Output.TrimEnd('\r', '\n') != Fingerprint(clean)) throw BindingChanged();
        var target = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--replace-all", "remote.origin.launcherAuthenticationTarget", Fingerprint(clean)], token).ConfigureAwait(false);
        if (target.ExitCode != 0) throw BindingChanged();
        var result = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--replace-all", "remote.origin.launcherAuthentication", Mode], token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Repository cloned, but its saved token connection could not be recorded. Keep the cloned folder and reconnect before remote Git operations.");
    }

    internal static async Task ClearChangedBindingAsync(string root, string remoteName, string cloneUrl, CancellationToken token)
    {
        var prefix = "remote." + remoteName + ".";
        var mode = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--get-all", prefix + "launcherAuthentication"], token).ConfigureAwait(false);
        if (mode.ExitCode is not (0 or 1)) throw BindingCleanupFailed();
        var modeValue = mode.Output.TrimEnd('\r', '\n');
        if (modeValue.Length != 0 && modeValue != Mode) return;
        var binding = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--get-all", prefix + "launcherAuthenticationTarget"], token).ConfigureAwait(false);
        if (binding.ExitCode is not (0 or 1)) throw BindingCleanupFailed();
        if (binding.ExitCode == 0 && binding.Output.TrimEnd('\r', '\n') == Fingerprint(cloneUrl)) return;
        if (mode.ExitCode == 1 && binding.ExitCode == 1) return;

        // An explicit remote URL save can return to the ordinary Git credential flow, but
        // must never transfer a saved token binding to that new destination. Remove the mode
        // first so an interrupted cleanup cannot leave an active binding without its target.
        foreach (var key in new[] { "launcherAuthentication", "launcherAuthenticationTarget" })
        {
            var removed = await GitRepositoryService.GitAsync(root,
                ["config", "--local", "--unset-all", prefix + key], token).ConfigureAwait(false);
            if (removed.ExitCode is not (0 or 5)) throw BindingCleanupFailed();
        }
        foreach (var key in new[] { "launcherAuthentication", "launcherAuthenticationTarget" })
        {
            var remaining = await GitRepositoryService.GitAsync(root,
                ["config", "--local", "--get-all", prefix + key], token).ConfigureAwait(false);
            if (remaining.ExitCode != 1) throw BindingCleanupFailed();
        }
        GitConnectionWarmup.Invalidate(root);
    }

    private static async Task<string?> FindBoundTargetAsync(string root, string operand, bool push, CancellationToken token)
    {
        var remoteName = Regex.IsMatch(operand, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
        var configuration = await GitRepositoryService.GitAsync(root,
            ["config", "--local", "--null", "--get-regexp", "^remote\\..*\\.launcherauthentication$"], token).ConfigureAwait(false);
        if (configuration.ExitCode == 1) return null;
        if (configuration.ExitCode != 0) throw BindingChanged();
        foreach (var entry in configuration.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('\n');
            if (split < 0 || entry[(split + 1)..] != Mode) continue;
            var key = entry[..split];
            const string suffix = ".launcherAuthentication";
            if (!key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) throw BindingChanged();
            var name = key[7..^suffix.Length];
            if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)) throw BindingChanged();
            if (remoteName && name != operand) continue;
            var original = await GitRepositoryService.GitAsync(root,
                ["config", "--local", "--get-all", "remote." + name + ".url"], token).ConfigureAwait(false);
            var originals = original.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (original.ExitCode != 0 || originals.Length != 1) throw BindingChanged();
            if (!remoteName && originals[0] != operand) continue;
            var clean = ValidateTokenUrl(originals[0]);
            var fingerprint = await GitRepositoryService.GitAsync(root,
                ["config", "--local", "--get-all", "remote." + name + ".launcherAuthenticationTarget"], token).ConfigureAwait(false);
            if (fingerprint.ExitCode != 0 || fingerprint.Output.TrimEnd('\r', '\n') != Fingerprint(clean)) throw BindingChanged();
            var args = new List<string> { "remote", "get-url" };
            if (push) args.Add("--push");
            args.AddRange(["--all", name]);
            var effective = await GitRepositoryService.GitAsync(root, args, token).ConfigureAwait(false);
            var urls = effective.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (effective.ExitCode != 0 || urls.Length != 1 || urls[0] != clean) throw BindingChanged();
            return clean;
        }
        return null;
    }

    private static string ValidateTokenUrl(string url)
    {
        var target = GitConnectionService.ParseTarget(GitHostingProvider.AzureDevOps, url);
        if (target.IsSsh) throw new InvalidOperationException("Azure DevOps tokens require the repository's HTTPS clone URL.");
        _ = AzureDevOpsImportService.ParseProjectUrl(target.CloneUrl);
        return target.CloneUrl;
    }

    private static string Fingerprint(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    private static InvalidOperationException BindingCleanupFailed() => new(
        "The previous token connection could not be cleared completely. Refresh Config, check the saved remote URL, and save it again to finish reconnecting.");
    private static InvalidOperationException BindingChanged() => new(
        "The saved token connection no longer matches this repository's exact remote URL, or Git rewrites that URL. Review the remote before reconnecting; no token was sent.");
}
