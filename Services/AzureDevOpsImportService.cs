using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Reads Azure DevOps Services metadata using a transient Git Credential Manager credential.</summary>
public static class AzureDevOpsImportService
{
    private const int ResponseLimit = 8 * 1024 * 1024;
    private const int RepositoryLimit = 10000;
    private const int BranchLimit = 50000;
    private const int PageLimit = 100;
    private static readonly Regex OrganizationName = new("^[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyHost = new("^([A-Za-z0-9][A-Za-z0-9-]*)\\.visualstudio\\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static AzureDevOpsProjectTarget ParseProjectUrl(string projectUrl)
    {
        var input = projectUrl?.Trim() ?? "";
        if (input.Length is 0 or > 4000 || input.Any(char.IsControl) || input.Contains('\\') ||
            !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw InvalidProjectUrl();

        // Inspect the original path as well: Uri normalization can conceal dot segments.
        var authorityEnd = input.IndexOf('/', input.IndexOf("://", StringComparison.Ordinal) + 3);
        var path = authorityEnd < 0 ? "" : input[authorityEnd..];
        if (path.EndsWith('/')) path = path[..^1];
        if (!path.StartsWith('/') || path.Length < 2) throw InvalidProjectUrl();
        var parts = path[1..].Split('/').Select(DecodePathPart).ToArray();
        string organization;
        string project;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 2 && !(parts.Length == 4 && parts[2] == "_git")) throw InvalidProjectUrl();
            organization = parts[0];
            project = parts[1];
        }
        else
        {
            var legacy = LegacyHost.Match(uri.Host);
            if (!legacy.Success) throw InvalidProjectUrl();
            organization = legacy.Groups[1].Value;
            if (parts.Length > 1 && parts[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
                parts = parts[1..];
            if (parts.Length != 1 && !(parts.Length == 3 && parts[1] == "_git")) throw InvalidProjectUrl();
            project = parts[0];
        }
        if (!OrganizationName.IsMatch(organization) ||
            (uri.UserInfo.Length != 0 && (!parts.Contains("_git", StringComparer.Ordinal) ||
             !uri.UserInfo.Equals(organization, StringComparison.OrdinalIgnoreCase)))) throw InvalidProjectUrl();
        return new AzureDevOpsProjectTarget
        {
            Organization = organization,
            Project = project,
            ProjectUrl = "https://dev.azure.com/" + Uri.EscapeDataString(organization) + "/" + Uri.EscapeDataString(project)
        };
    }

    public static Task<IReadOnlyList<AzureDevOpsImportRepository>> ListRepositoriesAsync(string projectUrl,
        bool interactive, CancellationToken token = default)
    {
        var target = ParseProjectUrl(projectUrl);
        return WithAuthenticationAsync<IReadOnlyList<AzureDevOpsImportRepository>>(target, interactive, async (client, deadline) =>
        {
            // The repository-list API returns the complete project list; it does not define pagination.
            using var page = await ReadPageAsync(client,
                target.ProjectUrl + "/_apis/git/repositories?includeLinks=false&includeHidden=false&api-version=7.1", deadline).ConfigureAwait(false);
            if (page.ContinuationToken.Length != 0)
                throw new InvalidOperationException("Azure DevOps returned an incomplete repository list. Open Azure DevOps or retry before importing.");
            var values = ReadValues(page.Document);
            if (values.GetArrayLength() > RepositoryLimit)
                throw new InvalidOperationException("This Azure DevOps project has too many repositories for guided import. Clone with Git, then add its folder.");
            var repositories = new List<AzureDevOpsImportRepository>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in values.EnumerateArray())
            {
                deadline.ThrowIfCancellationRequested();
                if (entry.ValueKind != JsonValueKind.Object ||
                    !Guid.TryParse(ReadRequiredString(entry, "id"), out var id) || id == Guid.Empty || !ids.Add(id.ToString("D")))
                    throw InvalidMetadata();
                if (entry.TryGetProperty("isDisabled", out var disabled))
                {
                    if (disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidMetadata();
                    if (disabled.GetBoolean()) continue;
                }
                var name = ReadRequiredString(entry, "name");
                if (!IsSafePathPart(name)) throw InvalidMetadata();
                if (!entry.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object)
                    throw InvalidMetadata();
                var projectName = ReadRequiredString(project, "name");
                if (!IsSafePathPart(projectName) || !Guid.TryParse(ReadRequiredString(project, "id"), out var projectId) ||
                    projectId == Guid.Empty || (!target.Project.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                    (!Guid.TryParse(target.Project, out var requestedProjectId) || requestedProjectId != projectId)))
                    throw InvalidMetadata();
                var defaultBranch = "";
                if (entry.TryGetProperty("defaultBranch", out var defaultValue) && defaultValue.ValueKind != JsonValueKind.Null)
                {
                    if (defaultValue.ValueKind != JsonValueKind.String) throw InvalidMetadata();
                    var reference = defaultValue.GetString() ?? "";
                    if (reference.Length > 0)
                    {
                        if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal) || !IsSafeBranchName(reference[11..]))
                            throw InvalidMetadata();
                        defaultBranch = reference[11..];
                    }
                }
                // Require the API's actual clone URL to resolve to the reviewed organization and project.
                // Never send authentication or clone commands to an unvalidated response URL.
                var cloneUrl = ReadCloneUrl(ReadRequiredString(entry, "remoteUrl"), target, projectName, projectId, name);
                repositories.Add(new AzureDevOpsImportRepository
                {
                    Id = id.ToString("D"), Name = name, CloneUrl = cloneUrl, DefaultBranch = defaultBranch
                });
            }
            return repositories.OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }, token);
    }

    public static Task<IReadOnlyList<string>> ListBranchesAsync(string projectUrl, string repositoryId,
        bool interactive, CancellationToken token = default)
    {
        var target = ParseProjectUrl(projectUrl);
        if (!Guid.TryParse(repositoryId, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Choose a repository from the current Azure DevOps project list.");
        return WithAuthenticationAsync<IReadOnlyList<string>>(target, interactive, async (client, deadline) =>
        {
            var branches = new HashSet<string>(StringComparer.Ordinal);
            var continuations = new HashSet<string>(StringComparer.Ordinal);
            var continuation = "";
            for (var pageNumber = 0; pageNumber < PageLimit; pageNumber++)
            {
                var endpoint = target.ProjectUrl + "/_apis/git/repositories/" + id.ToString("D") +
                    "/refs?filter=heads%2F&%24top=1000&includeLinks=false&includeStatuses=false&api-version=7.1";
                if (continuation.Length != 0) endpoint += "&continuationToken=" + Uri.EscapeDataString(continuation);
                using var page = await ReadPageAsync(client, endpoint, deadline).ConfigureAwait(false);
                foreach (var entry in ReadValues(page.Document).EnumerateArray())
                {
                    deadline.ThrowIfCancellationRequested();
                    var reference = ReadRequiredString(entry, "name");
                    if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal) || !IsSafeBranchName(reference[11..]) ||
                        !branches.Add(reference[11..])) throw InvalidMetadata();
                    if (branches.Count > BranchLimit)
                        throw new InvalidOperationException("This repository has too many branches for guided import. Clone with Git, then add its folder.");
                }
                continuation = page.ContinuationToken;
                if (continuation.Length == 0) return branches.OrderBy(branch => branch, StringComparer.OrdinalIgnoreCase).ToArray();
                if (!continuations.Add(continuation)) throw InvalidMetadata();
            }
            throw new InvalidOperationException("Azure DevOps returned too many branch pages. Clone with Git, then add its folder.");
        }, token);
    }

    private static Task<T> WithAuthenticationAsync<T>(AzureDevOpsProjectTarget target, bool interactive,
        Func<HttpClient, CancellationToken, Task<T>> read, CancellationToken token) =>
        Task.Run(() => WithAuthenticationCoreAsync(target, interactive, read, token), token);

    private static async Task<T> WithAuthenticationCoreAsync<T>(AzureDevOpsProjectTarget target, bool interactive,
        Func<HttpClient, CancellationToken, Task<T>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("The Windows user data folder is unavailable. Restart the launcher with a valid Windows user profile.");
        var folder = Path.Combine(localAppData, "FullStackLauncher", "git-auth");
        try { Directory.CreateDirectory(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The launcher could not prepare its Git sign-in folder. Check local folder access, then retry."); }

        var credentialManager = await GitCredentialManagerSupport.ResolveAsync(folder, token).ConfigureAwait(false);
        var credentialManagerDirectory = Path.GetDirectoryName(credentialManager)!;

        GitRepositoryService.CommandResult credential;
        try
        {
            // Invoke GCM directly so custom credential helper scripts are never used for discovery.
            // All OAuth preferences apply only to this command; no launcher settings are changed.
            credential = await GitRepositoryService.GitAsync(folder,
                ["--exec-path=" + credentialManagerDirectory,
                 "-c", "credential.provider=azure-repos", "-c", "credential.azreposCredentialType=oauth",
                 "-c", "credential.azreposUseMicrosoftSharedCache=true",
                 "-c", "credential.guiPrompt=" + (interactive ? "true" : "false"),
                 "-c", "credential.allowUnsafeRemotes=false", "credential-manager", "get"],
                token, network: true, interactive: interactive,
                standardInput: "capability[]=authtype\nprotocol=https\nhost=dev.azure.com\npath=" + target.Organization + "\n\n").ConfigureAwait(false);
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Microsoft sign-in timed out. Choose Sign in & load and retry."); }
        catch (InvalidOperationException)
        { throw new InvalidOperationException("Git Credential Manager could not start. Install or update Git for Windows with Git Credential Manager, then retry."); }

        // Helper stdout/stderr can contain secrets. Never expose either, even on failure.
        var parsedCredential = credential.ExitCode == 0 ? ParseCredential(credential.Output) : null;
        credential = new GitRepositoryService.CommandResult(credential.ExitCode, "", "");
        if (parsedCredential is null)
            throw new InvalidOperationException(interactive
                ? "Microsoft sign-in did not provide an Azure DevOps credential. Check Git Credential Manager and your organization account, then retry."
                : "No usable Microsoft sign-in is available. Choose Sign in & load to sign in with Git Credential Manager.");
        AuthenticationHeaderValue? authorization = parsedCredential.Authorization;
        var accountProtocol = parsedCredential.AccountProtocol;
        parsedCredential = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = authorization;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FullStackLauncher-Azure-Import/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        T result;
        try { result = await read(client, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("Azure DevOps discovery timed out. Check network access, then retry."); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("Azure DevOps could not be reached. Check network access and TLS trust, then retry."); }
        catch (IOException)
        { throw new InvalidOperationException("Azure DevOps returned an unreadable response. Check network access, then retry."); }
        catch (JsonException) { throw InvalidMetadata(); }
        finally
        {
            client.DefaultRequestHeaders.Authorization = null;
            authorization = null;
        }

        if (interactive)
        {
            // GCM's get operation caches OAuth tokens but does not associate the account with the
            // organization. Store only after the credential successfully read the reviewed project,
            // so subsequent noninteractive branch discovery can resolve the same account. GCM owns
            // this association; credentials are never saved in launcher settings or displayed.
            if (accountProtocol is null)
                throw new InvalidOperationException("Git Credential Manager did not identify the signed-in account. Update Git Credential Manager, then choose Sign in & load again.");
            await RememberAccountAsync(folder, target, accountProtocol, credentialManager, token).ConfigureAwait(false);
        }
        accountProtocol = null;
        return result;
    }

    private static async Task RememberAccountAsync(string folder, AzureDevOpsProjectTarget target,
        string accountProtocol, string credentialManager, CancellationToken token)
    {
        GitRepositoryService.CommandResult stored;
        try
        {
            stored = await GitRepositoryService.GitAsync(folder,
                ["--exec-path=" + Path.GetDirectoryName(credentialManager)!,
                 "-c", "credential.provider=azure-repos", "-c", "credential.azreposCredentialType=oauth",
                 "-c", "credential.azreposUseMicrosoftSharedCache=true", "-c", "credential.guiPrompt=false",
                 "-c", "credential.allowUnsafeRemotes=false", "credential-manager", "store"],
                token, standardInput: "capability[]=authtype\nprotocol=https\nhost=dev.azure.com\npath="
                    + target.Organization + "\n" + accountProtocol + "\n").ConfigureAwait(false);
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Git Credential Manager timed out while remembering the selected account. Choose Sign in & load and retry before selecting a repository."); }
        catch (InvalidOperationException)
        { throw new InvalidOperationException("Git Credential Manager could not remember the selected account. Check Git Credential Manager and local user configuration access, then choose Sign in & load again."); }

        var exitCode = stored.ExitCode;
        stored = new GitRepositoryService.CommandResult(exitCode, "", "");
        if (exitCode != 0)
            throw new InvalidOperationException("Git Credential Manager could not remember the selected account. Check Git Credential Manager and local user configuration access, then choose Sign in & load again.");
    }

    private static DiscoveryCredential? ParseCredential(string output)
    {
        if (output.Length > 128 * 1024) return null;
        string? username = null;
        string? password = null;
        string? authenticationType = null;
        string? bearerCredential = null;
        foreach (var line in output.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (clean.Length == 0) continue;
            var separator = clean.IndexOf('=');
            if (separator <= 0) return null;
            var key = clean[..separator];
            var value = clean[(separator + 1)..];
            if (key == "username") { if (username != null) return null; username = value; }
            else if (key == "password") { if (password != null) return null; password = value; }
            else if (key == "authtype") { if (authenticationType != null) return null; authenticationType = value; }
            else if (key == "credential") { if (bearerCredential != null) return null; bearerCredential = value; }
        }
        if (username is not null && (username.Length is 0 or > 1024 || username.Contains(':') || username.Any(char.IsControl)))
            return null;
        if (authenticationType != null || bearerCredential != null)
        {
            if (authenticationType?.Equals("Bearer", StringComparison.OrdinalIgnoreCase) != true ||
                bearerCredential is not { Length: > 0 and <= 65535 } || bearerCredential.Any(char.IsControl)) return null;
            return new DiscoveryCredential(new AuthenticationHeaderValue("Bearer", bearerCredential),
                username is null ? null : "username=" + username + "\nauthtype=Bearer\ncredential=" + bearerCredential + "\n");
        }
        if (username is null ||
            password is null || password.Length is 0 or > 65535 || password.Any(char.IsControl)) return null;
        var authorization = password.StartsWith("eyJ", StringComparison.Ordinal)
            ? new AuthenticationHeaderValue("Bearer", password)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        return new DiscoveryCredential(authorization, "username=" + username + "\npassword=" + password + "\n");
    }

    private sealed record DiscoveryCredential(AuthenticationHeaderValue Authorization, string? AccountProtocol);

    private static async Task<ResponsePage> ReadPageAsync(HttpClient client, string endpoint, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Azure DevOps authentication failed. Choose Sign in & load and check the organization account.",
                HttpStatusCode.Forbidden => "Your Microsoft account cannot read this Azure DevOps project. Check project and repository permissions.",
                HttpStatusCode.NotFound => "Azure DevOps could not find this project or repository for your account. Check the project URL and permissions.",
                HttpStatusCode.TooManyRequests => "Azure DevOps rate limited this request. Wait before retrying.",
                _ when (int)response.StatusCode is >= 300 and < 400 => "Azure DevOps redirected this request. Use the current HTTPS project URL at dev.azure.com.",
                _ => "Azure DevOps discovery failed (HTTP " + (int)response.StatusCode + "). Retry when the service is available."
            });
        if (response.Content.Headers.ContentLength > ResponseLimit ||
            !(response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.OrdinalIgnoreCase) ?? false))
            throw InvalidMetadata();
        var continuation = "";
        if (response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens))
        {
            var values = tokens.ToArray();
            if (values.Length != 1 || values[0].Length > 4096 || values[0].Any(char.IsControl)) throw InvalidMetadata();
            continuation = values[0];
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (body.Length + count > ResponseLimit) throw InvalidMetadata();
            await body.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return new ResponsePage(document, continuation);
    }

    private static JsonElement ReadValues(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata();
        if (root.TryGetProperty("count", out var count) && (count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var number) || number != values.GetArrayLength()))
            throw InvalidMetadata();
        return values;
    }

    private static string ReadRequiredString(JsonElement entry, string property)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())) throw InvalidMetadata();
        return value.GetString()!;
    }

    private static string DecodePathPart(string part)
    {
        var decoded = Uri.UnescapeDataString(part);
        if (!IsSafePathPart(decoded)) throw InvalidProjectUrl();
        return decoded;
    }

    private static string ReadCloneUrl(string remoteUrl, AzureDevOpsProjectTarget target, string projectName,
        Guid projectId, string repositoryName)
    {
        try
        {
            var clean = GitRepositoryService.ValidateRemoteUrl(remoteUrl);
            var remote = new Uri(clean);
            if (remote.Scheme != "https" || !remote.IsDefaultPort) throw InvalidMetadata();
            var parts = remote.AbsolutePath.Trim('/').Split('/').Select(DecodePathPart).ToArray();
            var legacy = LegacyHost.Match(remote.Host);
            // Azure can return a default-project clone URL without a project path. Make that
            // project explicit using the already-validated API project identity and repository name.
            var shorthand = remote.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                ? parts.Length == 3 && parts[0].Equals(target.Organization, StringComparison.OrdinalIgnoreCase) && parts[1] == "_git"
                : legacy.Success && legacy.Groups[1].Value.Equals(target.Organization, StringComparison.OrdinalIgnoreCase) &&
                    (parts.Length == 2 && parts[0] == "_git" || parts.Length == 3 &&
                     parts[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) && parts[1] == "_git");
            if (shorthand)
            {
                if (!parts[^1].Equals(repositoryName, StringComparison.Ordinal)) throw InvalidMetadata();
                clean = "https://dev.azure.com/" + Uri.EscapeDataString(target.Organization) + "/" +
                    Uri.EscapeDataString(projectName) + "/_git/" + Uri.EscapeDataString(repositoryName);
            }
            clean = GitConnectionService.ValidateUrl(GitHostingProvider.AzureDevOps, clean);
            var cloneProject = ParseProjectUrl(clean);
            if (!cloneProject.Organization.Equals(target.Organization, StringComparison.OrdinalIgnoreCase) ||
                (!cloneProject.Project.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                 (!Guid.TryParse(cloneProject.Project, out var cloneProjectId) || cloneProjectId != projectId)))
                throw InvalidMetadata();
            var cloneRepository = Uri.UnescapeDataString(new Uri(clean).Segments[^1]);
            if (!cloneRepository.Equals(repositoryName, StringComparison.Ordinal)) throw InvalidMetadata();
            return clean;
        }
        catch (InvalidOperationException) { throw InvalidMetadata(); }
    }

    private static bool IsSafePathPart(string part) => part.Length is > 0 and <= 256 && part == part.Trim() &&
        part is not ("." or "..") && !part.Any(char.IsControl) && part.IndexOfAny(['/', '\\', '?', '#', '@', '%', ':']) < 0;

    private static bool IsSafeBranchName(string branch) => branch.Length is > 0 and <= 1024 &&
        !branch.StartsWith('-') && branch != "@" && !branch.EndsWith('.') &&
        !branch.Any(char.IsControl) && branch.IndexOfAny([' ', '~', '^', ':', '?', '*', '[', '\\']) < 0 &&
        !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal) &&
        branch.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    private static InvalidOperationException InvalidProjectUrl() => new(
        "Use https://dev.azure.com/organization/project or its complete HTTPS clone URL. Azure DevOps Services only; remove credentials, query parameters and fragments.");

    private static InvalidOperationException InvalidMetadata() => new(
        "Azure DevOps returned incomplete or unsupported metadata. Refresh the project or repository list before importing.");

    private sealed record ResponsePage(JsonDocument Document, string ContinuationToken) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}
