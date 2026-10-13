using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Explicit connection setup. Credentials stay with Git's configured helper or the selected
/// protected token store. Credential values never reach the UI.
/// </summary>
public static class GitConnectionService
{
    private const int IdentityResponseLimit = 256 * 1024;
    private static readonly Regex ScpUrl = new("^git@(?<host>[A-Za-z0-9][A-Za-z0-9.-]*):(?<path>[^\\s:]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex OrganizationName = new("^[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant);

    public static GitHostingProvider? InferProvider(string url)
    {
        try
        {
            var (_, host, path, _) = SplitTarget(GitRepositoryService.ValidateRemoteUrl(url));
            if (IsAzureHost(host)) return GitHostingProvider.AzureDevOps;
            var parts = path.Trim('/').Split('/');
            if (parts.Length >= 3 && parts[^2] == "_git")
            {
                _ = ParseTarget(GitHostingProvider.AzureDevOps, url);
                return GitHostingProvider.AzureDevOps;
            }
            if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("ssh.github.com", StringComparison.OrdinalIgnoreCase)) return GitHostingProvider.GitHub;
            // Other Enterprise hosts cannot be identified by their domain name alone.
            return null;
        }
        catch (InvalidOperationException) { return null; }
    }

    public static string ValidateUrl(GitHostingProvider provider, string url) => ParseTarget(provider, url).CloneUrl;

    public static GitConnectionTarget ParseTarget(GitHostingProvider provider, string url)
    {
        var input = url.Trim();
        if (input.Split('/').Any(part => Uri.UnescapeDataString(part) is "." or ".."))
            throw new InvalidOperationException("Use the provider's complete clone URL without ambiguous path segments.");
        var clean = GitRepositoryService.ValidateRemoteUrl(input);
        var (ssh, host, path, user) = SplitTarget(clean);
        if ((provider != GitHostingProvider.AzureDevOps || ssh)
            && (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') || host.EndsWith('.')
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enter the clone URL for the hosting domain, including its full domain name.");
        var parts = path.Trim('/').Split('/');
        if (parts.Any(part => part.Length == 0 || UnsafePathPart(part)))
            throw new InvalidOperationException("Use the provider's complete clone URL without query parameters, fragments or ambiguous path segments.");
        if (ssh && user != "git")
            throw new InvalidOperationException("Use the provider's SSH clone URL with the git SSH user.");
        string organization;
        var collectionUrl = "";
        var isHosted = false;
        if (provider == GitHostingProvider.GitHub)
        {
            if (!ssh && !new Uri(clean).IsDefaultPort)
                throw new InvalidOperationException("This setup flow supports GitHub HTTPS on its standard port. Configure other transport endpoints with Git directly.");
            if (IsAzureHost(host)) throw new InvalidOperationException("This URL belongs to Azure DevOps. Choose Azure DevOps in the previous step.");
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[A-Za-z0-9][A-Za-z0-9-]*$")
                || !Regex.IsMatch(parts[1], "^[A-Za-z0-9_.-]+$") || parts[1] is "." or ".." or ".git")
                throw new InvalidOperationException("Use a GitHub clone URL with an owner and repository, such as https://github.com/owner/repository.git. Enterprise domains are supported.");
            organization = parts[0];
        }
        else if (provider == GitHostingProvider.AzureDevOps)
        {
            if (ssh)
            {
                if (!host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase) || parts.Length != 4 || parts[0] != "v3")
                    throw new InvalidOperationException("Use the Azure DevOps SSH clone URL: git@ssh.dev.azure.com:v3/organization/project/repository.");
                organization = parts[1];
                if (!OrganizationName.IsMatch(organization)) throw new InvalidOperationException("The Azure organization name in this clone URL is invalid.");
                collectionUrl = "https://dev.azure.com/" + Uri.EscapeDataString(organization);
                isHosted = true;
            }
            else
            {
                if (parts.Length < 3 || parts[^2] != "_git" || parts.Take(parts.Length - 2).Contains("_git", StringComparer.Ordinal))
                    throw new InvalidOperationException("Use the complete Azure DevOps HTTPS clone URL ending in project/_git/repository. Azure DevOps Server collection paths and HTTPS ports are supported.");
                var project = AzureDevOpsImportService.ParseProjectUrl(clean);
                organization = project.Organization;
                collectionUrl = project.CollectionUrl;
                isHosted = project.IsHosted;
            }
        }
        else throw new InvalidOperationException("Choose GitHub or Azure DevOps before entering a clone URL.");
        return new GitConnectionTarget
        {
            Provider = provider, CloneUrl = clean, Host = host, Organization = organization,
            CollectionUrl = collectionUrl, IsHosted = isHosted, IsSsh = ssh
        };
    }

    public static Task<GitConnectionCheckResult> CheckAsync(string root, GitHostingProvider provider, string url, CancellationToken token = default) =>
        GitConnectionWarmup.RefreshAsync(root, provider, url, interactive: false, token);

    public static Task<GitConnectionCheckResult> SignInAsync(string root, GitHostingProvider provider, string url, CancellationToken token = default) =>
        GitConnectionWarmup.RefreshAsync(root, provider, url, interactive: true, token);

    internal static Task<GitConnectionCheckResult> CheckUncachedAsync(string root, GitConnectionTarget target, bool interactive, bool automatic, CancellationToken token) =>
        Task.Run(async () =>
        {
            // Connection checks do not mutate the checkout or need its mutation lease.
            // In particular, dashboard startup must never create a Git metadata lock file.
            var actualRoot = await ReadRepositoryRootAsync(root, token).ConfigureAwait(false);
            if (!string.Equals(actualRoot, NormalizeRoot(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The repository folder changed. Refresh local status before checking its connection.");
            await RequireUnchangedUrlAsync(root, target, token).ConfigureAwait(false);
            if (target.IsSsh)
            {
                var accessible = await ProbeRepositoryAsync(root, target, token).ConfigureAwait(false);
                return new GitConnectionCheckResult
                {
                    Authenticated = accessible, RepositoryAccessible = accessible,
                    Message = accessible
                        ? "SSH authentication and repository read access verified. Push permissions are checked when pushing."
                        : "SSH access could not be verified. Add your key to this provider and verify the host's fingerprint using its SSH instructions, then check again. This flow never accepts new SSH host keys or changes existing keys."
                };
            }

            if (target.Provider == GitHostingProvider.AzureDevOps
                && await AzureDevOpsTokenGitAuthentication.UsesTokenAsync(root, target.CloneUrl, token).ConfigureAwait(false))
            {
                Credential? savedToken = new("pat", AzureDevOpsTokenStore.Read(target.CloneUrl));
                bool tokenAuthenticated;
                try { tokenAuthenticated = await VerifyIdentityAsync(target, savedToken, token, personalAccessToken: true).ConfigureAwait(false); }
                finally { savedToken = null; }
                if (!tokenAuthenticated)
                    return new GitConnectionCheckResult { Message = "The saved Azure DevOps token could not be verified. Check network access, token expiry, organization, and Code permissions, then reconnect with a token." };
                var accessible = await ProbeRepositoryAsync(root, target, token, [], personalAccessToken: true).ConfigureAwait(false);
                return new GitConnectionCheckResult
                {
                    Authenticated = true, RepositoryAccessible = accessible,
                    Message = accessible
                        ? "Saved Azure DevOps token and repository read access verified. Push permissions are checked when pushing."
                        : "The saved Azure DevOps token is authenticated, but repository read access could not be verified. Check Code Read scope and repository permissions."
                };
            }

            var helperOptions = automatic ? await ReadAutomaticHelperOptionsAsync(root, target, token).ConfigureAwait(false) : [];
            if (helperOptions is null)
                return new GitConnectionCheckResult
                {
                    Message = "Automatic verification skips custom Git credential helpers. Open Config and choose Check connection to verify this repository explicitly."
                };

            if (target.Provider == GitHostingProvider.AzureDevOps && !target.IsHosted
                && await VerifyIdentityAsync(target, null, token).ConfigureAwait(false))
            {
                var accessible = await ProbeRepositoryAsync(root, target, token, helperOptions, windowsAuthentication: true).ConfigureAwait(false);
                return new GitConnectionCheckResult
                {
                    Authenticated = true, RepositoryAccessible = accessible,
                    Message = accessible
                        ? "Windows authentication and repository read access verified. Push permissions are checked when pushing."
                        : "Windows authentication is verified, but Git repository read access could not be verified. Check Git Credential Manager, repository permissions and network access."
                };
            }

            if (interactive && target.Provider == GitHostingProvider.GitHub)
            {
                var instance = new Uri(target.CloneUrl).GetLeftPart(UriPartial.Authority);
                var login = await GitRepositoryService.GitAsync(root,
                    ["credential-manager", "github", "login", "--url", instance, "--browser", "--force"], token, network: true, interactive: true).ConfigureAwait(false);
                // Provider command output is deliberately not returned: it may contain authentication material.
                if (login.ExitCode != 0)
                    return new GitConnectionCheckResult { Message = "GitHub sign-in did not complete. Install or update Git for Windows with Git Credential Manager enabled, then retry. For Enterprise, your administrator may need to configure Git Credential Manager's OAuth application." };
            }

            var providerId = target.Provider == GitHostingProvider.GitHub ? "github" : target.IsHosted ? "azure-repos" : "generic";
            var credentialArgs = new List<string>
            {
                "-c", "credential.provider=" + providerId, "-c", "credential.guiPrompt=" + (interactive ? "true" : "false"),
                "-c", "credential.allowUnsafeRemotes=false"
            };
            if (target.Provider == GitHostingProvider.AzureDevOps && !target.IsHosted)
                credentialArgs.AddRange(["-c", "credential." + target.CloneUrl + ".provider=generic",
                    "-c", "credential." + target.CloneUrl + ".useHttpPath=true"]);
            credentialArgs.AddRange(helperOptions);
            credentialArgs.AddRange(["credential", "fill"]);
            var credential = await GitRepositoryService.GitAsync(root,
                credentialArgs,
                token, network: true, interactive: interactive, standardInput: "url=" + target.CloneUrl + "\n\n").ConfigureAwait(false);
            // Never forward credential stdout, stderr, or exception detail to Activity, logs, settings or clipboard.
            var account = credential.ExitCode == 0 ? ParseCredential(credential.Output, target) : null;
            credential = new GitRepositoryService.CommandResult(credential.ExitCode, "", "");
            if (account is null)
                return new GitConnectionCheckResult
                {
                    Message = interactive
                        ? "Git sign-in did not provide a usable credential. Install Git for Windows with Git Credential Manager enabled and follow its account dialog, then retry."
                        : "Git authentication is not verified for this domain. Use the connection button above, or configure this repository's Git credential helper and check again. A public repository can be readable without signing in."
                };

            bool authenticated;
            try { authenticated = await VerifyIdentityAsync(target, account, token).ConfigureAwait(false); }
            finally { account = null; }
            if (!authenticated)
                return new GitConnectionCheckResult
                {
                    Message = "The provider could not verify Git's saved credential. Check network access, domain and account permissions, then reconnect. If Git Credential Manager keeps returning an expired credential, update that domain's account in Git Credential Manager; this flow does not explicitly clear saved accounts."
                };
            var repositoryAccessible = await ProbeRepositoryAsync(root, target, token, helperOptions).ConfigureAwait(false);
            return new GitConnectionCheckResult
            {
                Authenticated = true, RepositoryAccessible = repositoryAccessible,
                Message = repositoryAccessible
                    ? "Git authentication and repository read access verified. Push permissions are checked when pushing."
                    : "Your Git account is authenticated, but repository read access could not be verified. Check the clone URL, repository permissions and network connection, then retry."
            };
        }, token);

    internal static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.Any(char.IsControl))
            throw new ArgumentException("Use the repository's complete folder path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    // Read only connection metadata, without enumerating working files or inspecting the index.
    // Unsupported, ambiguous and untrusted repositories are left for explicit panel setup.
    internal static async Task<IReadOnlyList<ConfiguredConnection>> ReadConfiguredConnectionsAsync(string folder, CancellationToken token)
    {
        var root = await ReadRepositoryRootAsync(folder, token).ConfigureAwait(false);
        if (root is null) return [];
        var names = await GitRepositoryService.GitAsync(root, ["remote"], token).ConfigureAwait(false);
        if (names.ExitCode != 0) return [];
        var connections = new List<ConfiguredConnection>();
        foreach (var name in names.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)) continue;
            var urls = await GitRepositoryService.GitAsync(root, ["remote", "get-url", "--all", name], token).ConfigureAwait(false);
            if (urls.ExitCode != 0) continue;
            var targets = urls.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (targets.Length != 1) continue;
            var provider = InferProvider(targets[0]);
            if (provider is null)
            {
                var hint = await GitRepositoryService.GitAsync(root, ["config", "--local", "--get-all", $"remote.{name}.launcherProvider"], token).ConfigureAwait(false);
                var binding = await GitRepositoryService.GitAsync(root, ["config", "--local", "--get-all", $"remote.{name}.launcherProviderTarget"], token).ConfigureAwait(false);
                if (hint.ExitCode != 0 || binding.ExitCode != 0) continue;
                provider = hint.Output.TrimEnd('\r', '\n') switch
                {
                    "GitHub" => GitHostingProvider.GitHub,
                    "AzureDevOps" => GitHostingProvider.AzureDevOps,
                    _ => null
                };
                if (provider is null) continue;
                string normalized;
                try { normalized = ValidateUrl(provider.Value, targets[0]); }
                catch (InvalidOperationException) { continue; }
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                if (binding.Output.TrimEnd('\r', '\n') != fingerprint) continue;
            }
            try { connections.Add(new(root, ParseTarget(provider.Value, targets[0]))); }
            catch (InvalidOperationException) { }
        }
        return connections;
    }

    private static async Task<string?> ReadRepositoryRootAsync(string folder, CancellationToken token)
    {
        var result = await GitRepositoryService.GitAsync(NormalizeRoot(folder), ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        var root = result.Output.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(root) || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root)) return null;
        return NormalizeRoot(root);
    }

    internal sealed record ConfiguredConnection(string Root, GitConnectionTarget Target);

    private static Task<IReadOnlyList<string>?> ReadAutomaticHelperOptionsAsync(string root, GitConnectionTarget target, CancellationToken token) =>
        ReadAutomaticHelperOptionsAsync(root, target.CloneUrl, token);

    internal static async Task<IReadOnlyList<string>?> ReadAutomaticHelperOptionsAsync(string root, string cloneUrl, CancellationToken token)
    {
        if (await AzureDevOpsTokenGitAuthentication.UsesTokenAsync(root, cloneUrl, token).ConfigureAwait(false)) return [];
        static bool Known(string value) => value is "" or "manager" or "manager-core" or "wincred" or "cache" or "store";
        // Helper values are shell commands. A custom command may ignore Git/GCM's no-prompt flags.
        // Conservatively skip a configuration containing one, including URL-specific helpers.
        var configured = await GitRepositoryService.GitAsync(root,
            ["config", "--null", "--get-regexp", "^credential(\\..*)?\\.helper$"], token).ConfigureAwait(false);
        if (configured.ExitCode is not (0 or 1)) return null;
        foreach (var record in configured.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\n');
            if (separator < 0 || !Known(record[(separator + 1)..])) return null;
        }
        var matched = await GitRepositoryService.GitAsync(root,
            ["config", "--null", "--get-urlmatch", "credential.helper", cloneUrl], token).ConfigureAwait(false);
        if (matched.ExitCode is not (0 or 1)) return null;
        var helpers = matched.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (helpers.Length > 16 || helpers.Any(helper => !Known(helper))) return null;
        // Pin the recognized helper names for both commands, so an external config edit cannot
        // insert a custom helper between the read and the background network check.
        var options = new List<string> { "-c", "credential.helper=" };
        foreach (var helper in helpers) options.AddRange(["-c", "credential.helper=" + helper]);
        return options;
    }

    private static async Task RequireUnchangedUrlAsync(string root, GitConnectionTarget target, CancellationToken token)
    {
        var resolved = await GitRepositoryService.GitAsync(root, ["ls-remote", "--get-url", "--", target.CloneUrl], token).ConfigureAwait(false);
        if (resolved.ExitCode != 0 || !string.Equals(resolved.Output.TrimEnd('\r', '\n'), target.CloneUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("Git rewrites this clone URL or cannot resolve it. Review your URL rewrite rules with Git before connecting this domain; setup will not send credentials to a substituted destination.");
    }

    private static async Task<bool> ProbeRepositoryAsync(string root, GitConnectionTarget target, CancellationToken token,
        IReadOnlyList<string>? helperOptions = null, bool windowsAuthentication = false, bool personalAccessToken = false)
    {
        await RequireUnchangedUrlAsync(root, target, token).ConfigureAwait(false);
        var args = new List<string>
        {
            "-c", "http.followRedirects=false", "-c", "http." + target.CloneUrl + ".sslVerify=true"
        };
        if (target.Provider == GitHostingProvider.AzureDevOps && !target.IsHosted)
            args.AddRange(["-c", "credential." + target.CloneUrl + ".provider=generic",
                "-c", "credential." + target.CloneUrl + ".useHttpPath=true"]);
        if (windowsAuthentication)
            args.AddRange(["-c", "http." + target.CloneUrl + ".emptyAuth=true"]);
        if (helperOptions is not null) args.AddRange(helperOptions);
        args.AddRange(["ls-remote", "--heads", "--", target.CloneUrl]);
        var result = await GitRepositoryService.GitAsync(root, args, token, network: true, strictSsh: true,
            azureDevOpsTokenUrl: personalAccessToken ? target.CloneUrl : null).ConfigureAwait(false);
        if (result.ExitCode != 0) return false;
        // Empty repositories are valid; successful empty output does not mean a missing branch.
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).All(line =>
        {
            var fields = line.Split('\t');
            return fields.Length == 2 && fields[0].Length is 40 or 64 && fields[0].All(char.IsAsciiHexDigit)
                && fields[1].StartsWith("refs/heads/", StringComparison.Ordinal);
        });
    }

    private static Credential? ParseCredential(string output, GitConnectionTarget target)
    {
        if (output.Length > 128 * 1024) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (clean.Length == 0) continue;
            var equals = clean.IndexOf('=');
            if (equals <= 0) return null;
            var key = clean[..equals];
            if (key is not ("protocol" or "host" or "path" or "username" or "password")) continue;
            if (!fields.TryAdd(key, clean[(equals + 1)..])) return null;
        }
        var uri = new Uri(target.CloneUrl);
        if (!fields.TryGetValue("protocol", out var protocol) || protocol != "https"
            || !fields.TryGetValue("host", out var host)
            || (!host.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase)
                && !(uri.IsDefaultPort && host.Equals(uri.Authority + ":443", StringComparison.OrdinalIgnoreCase)))
            || !fields.TryGetValue("username", out var username) || username.Contains(':') || username.Any(char.IsControl)
            || !fields.TryGetValue("password", out var password) || password.Length is 0 or > 65535 || password.Any(char.IsControl)) return null;
        if (fields.TryGetValue("path", out var path) && path != Uri.UnescapeDataString(uri.AbsolutePath).Trim('/')) return null;
        return new Credential(username, password);
    }

    private static async Task<bool> VerifyIdentityAsync(GitConnectionTarget target, Credential? credential, CancellationToken token,
        bool personalAccessToken = false)
    {
        var clone = new Uri(target.CloneUrl);
        var endpoint = target.Provider == GitHostingProvider.GitHub
            ? target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new Uri("https://api.github.com/user")
                : new Uri(clone.GetLeftPart(UriPartial.Authority) + "/api/v3/user")
            : new Uri(target.CollectionUrl + "/_apis/connectionData?connectOptions=0&lastChangeId=-1&lastChangeId64=-1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // Windows authentication is limited to the reviewed Server collection. Redirects and cookies stay disabled.
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = credential is null && target.Provider == GitHostingProvider.AzureDevOps && !target.IsHosted
        };
        if (credential is not null && !personalAccessToken && target.Provider == GitHostingProvider.AzureDevOps && !target.IsHosted)
        {
            var separator = credential.Username.IndexOf('\\');
            var account = separator > 0 && separator < credential.Username.Length - 1
                ? new NetworkCredential(credential.Username[(separator + 1)..], credential.Password, credential.Username[..separator])
                : new NetworkCredential(credential.Username, credential.Password);
            var credentials = new CredentialCache();
            var collection = new Uri(target.CollectionUrl + "/");
            credentials.Add(collection, "Negotiate", account);
            credentials.Add(collection, "NTLM", account);
            handler.Credentials = credentials;
        }
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("FullStackLauncher-Git-Setup/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        // Azure's Entra credentials are JWTs; PATs and GitHub credentials use the helper's Basic pair.
        if (credential is not null)
            request.Headers.Authorization = !personalAccessToken && target.Provider == GitHostingProvider.AzureDevOps
                && target.IsHosted && credential.Password.StartsWith("eyJ", StringComparison.Ordinal)
                ? new AuthenticationHeaderValue("Bearer", credential.Password)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password)));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > IdentityResponseLimit) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (data.Length + count > IdentityResponseLimit) return false;
                await data.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            }
            using var json = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (target.Provider == GitHostingProvider.GitHub)
                return json.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && number > 0
                    && json.RootElement.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(login.GetString());
            return json.RootElement.TryGetProperty("authenticatedUser", out var identity) && IsAuthenticatedAzureIdentity(identity)
                && json.RootElement.TryGetProperty("authorizedUser", out var authorized) && IsAuthenticatedAzureIdentity(authorized);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException) { return false; }
        finally { request.Headers.Authorization = null; }
    }

    internal static bool IsAuthenticatedAzureIdentity(JsonElement identity) =>
        identity.ValueKind == JsonValueKind.Object
        && identity.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var guid) && guid != Guid.Empty
        && identity.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.True
        && identity.TryGetProperty("providerDisplayName", out var name) && name.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(name.GetString()) && !name.GetString()!.Equals("Anonymous", StringComparison.OrdinalIgnoreCase)
        && (!identity.TryGetProperty("descriptor", out var descriptor) || !descriptor.ToString().Contains("anonymous", StringComparison.OrdinalIgnoreCase));

    private static (bool Ssh, string Host, string Path, string User) SplitTarget(string clean)
    {
        var scp = ScpUrl.Match(clean);
        if (scp.Success) return (true, scp.Groups["host"].Value.ToLowerInvariant(), scp.Groups["path"].Value, "git");
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh"))
            throw new InvalidOperationException("Use the provider's HTTPS or SSH clone URL.");
        return (uri.Scheme == "ssh", uri.Host.ToLowerInvariant(), uri.AbsolutePath, uri.UserInfo);
    }

    private static bool UnsafePathPart(string part)
    {
        var decoded = Uri.UnescapeDataString(part);
        return decoded is "." or ".." || decoded.Any(char.IsControl) || decoded.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0;
    }

    private static bool IsAzureHost(string host) => host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private sealed record Credential(string Username, string Password);
}
