using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Synchronizes local Angular development proxies with saved API targets.</summary>
public sealed class AngularDevProxyConfiguration
{
    private readonly List<FileChange> _changes = [];
    public int UpdatedFileCount => _changes.Count;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    public static AngularDevProxyConfiguration Prepare(ProjectProfile original, ProjectProfile candidate, SettingsStore store)
    {
        var result = new AngularDevProxyConfiguration();
        var apis = original.Services.Where(service =>
                (service.IsCommandApi || service.ApiConfiguration is not null || service.Kind.Equals(".NET", StringComparison.OrdinalIgnoreCase))
                && candidate.Services.Any(next => next.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(service => new ApiChange(service,
                new Uri(service.Url), new Uri(candidate.Services.Single(next =>
                    next.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase)).Url)))
            .Where(change => change.Before.GetLeftPart(UriPartial.Authority) !=
                change.After.GetLeftPart(UriPartial.Authority)).ToArray();
        var linkedApiIds = candidate.Services.Where(service => service.ApiTargetServiceId is not null)
            .Select(service => service.ApiTargetServiceId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyApis = apis.Where(api => api.Before.Port != api.After.Port &&
            !linkedApiIds.Contains(api.Profile.Id)).ToArray();
        var staged = new Dictionary<string, (byte[] Before, byte[] After)>(StringComparer.OrdinalIgnoreCase);
        var legacyMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legacyFilesSeen = false;
        foreach (var frontend in candidate.Services)
        {
            var linkedId = frontend.ApiTargetServiceId;
            var previousFrontend = original.Services.FirstOrDefault(service =>
                service.Id.Equals(frontend.Id, StringComparison.OrdinalIgnoreCase));
            var previousLink = previousFrontend?.ApiTargetServiceId;
            var directory = store.ResolveWorkingDirectory(candidate, frontend);
            var settingUpLink = linkedId is not null &&
                (!string.Equals(linkedId, previousLink, StringComparison.OrdinalIgnoreCase) ||
                 previousFrontend is not null && !directory.Equals(
                     store.ResolveWorkingDirectory(original, previousFrontend), StringComparison.OrdinalIgnoreCase));
            var relevantApis = linkedId is null
                ? frontend.DisableLegacyApiPortSync ? [] : legacyApis
                : apis.Where(api => api.Profile.Id.Equals(linkedId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!settingUpLink && relevantApis.Length == 0) continue;

            if (!File.Exists(Path.Combine(directory, "angular.json"))
                && !frontend.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase)) continue;
            // Custom CLI overrides can select a different proxy or production configuration.
            // Do not silently edit the default file when it would not affect this launch.
            if (Regex.IsMatch(frontend.StartCommand, @"(?:^|\s)(?:--configuration|--proxy-config|--proxyConfig|--project|-c)(?:[=\s]|$)"))
                throw new InvalidOperationException($"{frontend.Name}: automatic API port sync requires the default Angular serve configuration without command-line configuration/proxy overrides.");
            var paths = FindProxyFiles(directory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (linkedId is null && paths.Length > 0) legacyFilesSeen = true;
            if (settingUpLink && paths.Length != 1)
                throw new InvalidOperationException($"{frontend.Name}: link setup requires one default Angular development proxy file.");
            var matched = linkedId is null ? legacyMatched : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var before = staged.TryGetValue(path, out var existing) ? existing.Before : File.ReadAllBytes(path);
                var current = staged.TryGetValue(path, out existing) ? existing.After : before;
                var setupApi = settingUpLink
                    ? candidate.Services.Single(service => service.Id.Equals(linkedId, StringComparison.OrdinalIgnoreCase)) : null;
                var after = RewriteTargets(current, relevantApis, matched, path, setupApi, linkedId is not null);
                staged[path] = (before, after);
            }
            if (linkedId is not null && !settingUpLink && !matched.Contains(linkedId))
                throw new InvalidOperationException($"{frontend.Name}: no local Angular proxy target matches the linked API's saved port. Review the development proxy before saving the new port.");
        }
        // Preserve the older port-match behavior for APIs without an explicit link.
        // An explicitly linked API never searches unrelated Angular folders.
        foreach (var api in legacyApis)
            if (legacyFilesSeen && !legacyMatched.Contains(api.Profile.Id))
                throw new InvalidOperationException($"No local Angular proxy target matches {api.Profile.Name}'s saved port {api.Before.Port}. Correct the development proxy target before saving the new port.");
        foreach (var (path, change) in staged)
            if (!change.Before.AsSpan().SequenceEqual(change.After))
                result._changes.Add(new(path, change.Before, change.After));
        return result;
    }

    private static IEnumerable<string> FindProxyFiles(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "angular.json")), JsonOptions);
        var found = false;
        foreach (var project in document.RootElement.GetProperty("projects").EnumerateObject())
        {
            if (!project.Value.TryGetProperty("architect", out var targets)
                && !project.Value.TryGetProperty("targets", out targets)) continue;
            if (!targets.TryGetProperty("serve", out var serve)) continue;
            string? proxy = null;
            if (serve.TryGetProperty("options", out var options) && options.TryGetProperty("proxyConfig", out var setting))
                proxy = setting.GetString();
            if (serve.TryGetProperty("defaultConfiguration", out var defaultSetting)
                && defaultSetting.GetString() is { Length: > 0 } configuration)
            {
                if (configuration != "development")
                    throw new InvalidOperationException("Automatic API port sync requires Angular's default serve configuration to be development.");
                if (serve.TryGetProperty("configurations", out var configurations)
                    && configurations.TryGetProperty(configuration, out var selected)
                    && selected.TryGetProperty("proxyConfig", out setting)) proxy = setting.GetString();
            }
            if (string.IsNullOrWhiteSpace(proxy) || !proxy.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Set a local JSON proxyConfig in angular.json before saving an API port change. JavaScript proxies and environment files require manual configuration.");
            var path = Path.GetFullPath(proxy, directory);
            var relative = Path.GetRelativePath(directory, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidOperationException("The Angular development proxy must be inside its service folder.");
            found = true;
            yield return path;
        }
        if (!found) throw new InvalidOperationException("No Angular serve target was found for automatic API port synchronization.");
    }

    private static byte[] RewriteTargets(byte[] content, ApiChange[] apis, HashSet<string> matched,
        string path, ServiceProfile? setupApi, bool linked)
    {
        var offset = content.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        using var document = JsonDocument.Parse(content.AsMemory(offset), JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{path}: use a JSON object mapping routes to proxy options for automatic API port sync.");
        // An explicit link owns one API route. Other local routes can share the old
        // port without belonging to this frontend/API relationship.
        var linkedRoute = linked ? FindLinkedApiRoute(document.RootElement, path) : null;
        var setupTarget = setupApi is null ? null : new Uri(setupApi.Url);
        var reader = new Utf8JsonReader(content.AsSpan(offset), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        using var output = new MemoryStream();
        var copied = 0;
        string? route = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                route = reader.GetString();
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2
                || !reader.ValueTextEquals("target")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
            var value = reader.GetString()!;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var target) || !target.IsLoopback
                || target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0) continue;
            if (linked && route != linkedRoute) continue;
            ApiChange? api = null;
            if (setupApi is null)
            {
                // Match against original ports so simultaneous edits cannot cascade.
                api = apis.SingleOrDefault(change => change.Before.Port == target.Port && change.Before.Scheme == target.Scheme);
                if (api is null) continue;
                matched.Add(api.Profile.Id);
            }
            // Replace only the authority's port; preserve host spelling, path, query and slash style.
            var authorityEnd = value.IndexOfAny(['/', '?', '#'], value.IndexOf("://", StringComparison.Ordinal) + 3);
            if (authorityEnd < 0) authorityEnd = value.Length;
            var authority = value[..authorityEnd];
            var hostStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
            var colon = authority.LastIndexOf(':');
            var hasPort = colon >= hostStart && (authority[hostStart] != '[' || colon > authority.IndexOf(']'));
            var updated = setupTarget is not null
                ? setupTarget.GetLeftPart(UriPartial.Authority) + value[authorityEnd..]
                : linked ? api!.After.GetLeftPart(UriPartial.Authority) + value[authorityEnd..]
                : (hasPort ? authority[..colon] : authority) + ":" + api!.After.Port + value[authorityEnd..];
            var start = checked(offset + (int)reader.TokenStartIndex);
            output.Write(content, copied, start - copied);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(updated));
            copied = checked(offset + (int)reader.BytesConsumed);
        }
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    private static string FindLinkedApiRoute(JsonElement root, string path)
    {
        var routes = root.EnumerateObject().Where(property =>
            property.Value.ValueKind == JsonValueKind.Object &&
            property.Value.TryGetProperty("target", out var value) && value.ValueKind == JsonValueKind.String)
            .Select(property => property.Name).ToArray();
        var apiRoutes = routes.Where(route => route.Equals("/api", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = apiRoutes.Length == 1 ? apiRoutes : routes;
        if (selected.Length != 1)
            throw new InvalidOperationException($"{path}: choose a proxy with one local API target before linking this Angular service.");
        var value = root.GetProperty(selected[0]).GetProperty("target").GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var target) || !target.IsLoopback ||
            target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0)
            throw new InvalidOperationException($"{path}: the linked API route must have a local HTTP or HTTPS target.");
        return selected[0];
    }

    public void SaveWithSettings(Action saveSettings)
    {
        var applied = new List<FileChange>();
        try
        {
            foreach (var change in _changes)
            {
                File.WriteAllBytes(change.TemporaryPath, change.After);
                if (!File.ReadAllBytes(change.Path).AsSpan().SequenceEqual(change.Before))
                    throw new InvalidOperationException($"{change.Path} changed while saving. Review the file and retry.");
                File.Replace(change.TemporaryPath, change.Path, change.BackupPath);
                applied.Add(change);
            }
            saveSettings();
        }
        catch (Exception saveError)
        {
            var failedRecovery = new List<string>();
            foreach (var change in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.ReadAllBytes(change.Path).AsSpan().SequenceEqual(change.After))
                        throw new IOException("The proxy changed after replacement.");
                    File.Replace(change.BackupPath, change.Path, null);
                }
                catch { failedRecovery.Add(change.BackupPath); }
            }
            if (failedRecovery.Count > 0)
                throw new InvalidOperationException("Saving failed and some proxy edits could not be restored. Review the recovery copies before retrying: "
                    + string.Join(", ", failedRecovery), saveError);
            throw;
        }
        finally
        {
            foreach (var change in _changes) TryDelete(change.TemporaryPath);
        }
        // Cleanup must never turn a completed save into a reported failure.
        foreach (var change in applied) TryDelete(change.BackupPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ApiChange(ServiceProfile Profile, Uri Before, Uri After);
    private sealed record FileChange(string Path, byte[] Before, byte[] After)
    {
        public string TemporaryPath { get; } = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        public string BackupPath { get; } = Path + "." + Guid.NewGuid().ToString("N") + ".bak";
    }
}
