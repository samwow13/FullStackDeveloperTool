using System.Text.Json;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    private sealed record PostPushLinkSetting(string Target, string Url);

    public static Task<string> SavePostPushUrlAsync(string root, string remote, string url, CancellationToken token = default) =>
        InRepositoryAsync(root, token, async _ =>
        {
            var destination = await RequireSingleRemoteAsync(root, remote, true, token).ConfigureAwait(false);
            var clean = PostPushLink.Validate(url);
            // One Git config replacement keeps the URL and destination binding together.
            var setting = JsonSerializer.Serialize(new PostPushLinkSetting(RemoteTargetFingerprint(destination), clean));
            EnsureSuccess(await GitAsync(root, ["config", "--local", "--replace-all", $"remote.{remote}.launcherPostPushLink", setting], token).ConfigureAwait(false));
            return clean.Length == 0 ? "Opening a link after push is disabled for this connection."
                : "After-push link saved for this connection. It opens in Chrome after a confirmed successful push.";
        });

    private static (string? Url, string? Error) ReadPostPushUrl(string output, string destination)
    {
        if (string.IsNullOrWhiteSpace(output)) return (null, null);
        try
        {
            var setting = JsonSerializer.Deserialize<PostPushLinkSetting>(output.TrimEnd('\r', '\n'));
            if (setting == null || string.IsNullOrWhiteSpace(setting.Target) || setting.Url == null)
                throw new InvalidOperationException("The saved after-push link is incomplete.");
            if (setting.Target != RemoteTargetFingerprint(ValidateRemoteUrl(destination)))
                return (null, null);
            return (PostPushLink.Validate(setting.Url), null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return ("", "The saved after-push link is invalid. Open Config and save the link again.");
        }
    }
}

public static class PostPushLink
{
    public static string Validate(string url)
    {
        if (url == null) throw new InvalidOperationException("The after-push link is missing. Save a complete link or an empty value to disable opening.");
        var clean = url.Trim();
        if (clean.Length == 0) return "";
        if (clean.Length > 2048 || clean.Any(char.IsControl) || !Uri.TryCreate(clean, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Enter a complete HTTP or HTTPS link without embedded credentials, or leave it blank to disable opening.");
        if (uri.AbsoluteUri.Length > 2048)
            throw new InvalidOperationException("The complete after-push link must be at most 2,048 characters.");
        return uri.AbsoluteUri;
    }

    public static string DefaultUrl(GitRemoteInfo remote)
    {
        if (!remote.UrlCanCopy || GitConnectionService.InferProvider(remote.PushUrl) != GitHostingProvider.AzureDevOps) return "";
        try
        {
            var target = GitConnectionService.ParseTarget(GitHostingProvider.AzureDevOps, remote.PushUrl);
            if (!target.IsSsh)
            {
                var uri = new Uri(target.CloneUrl);
                if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)) return Validate(target.CloneUrl);
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                return Validate($"https://{target.Organization}.visualstudio.com/{parts[1]}/_git/{parts[3]}");
            }
            var path = target.CloneUrl[(target.CloneUrl.IndexOf("v3/", StringComparison.Ordinal) + 3)..].Split('/');
            if (path.Length != 3) return "";
            return Validate($"https://{path[0]}.visualstudio.com/{path[1]}/_git/{path[2]}");
        }
        catch (InvalidOperationException) { return ""; }
    }

    public static string Url(GitRemoteInfo remote) => remote.PostPushUrl ?? DefaultUrl(remote);
}
