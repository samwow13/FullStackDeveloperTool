using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Synchronizes local Angular proxy targets with an inline API port edit.</summary>
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
                service.ApiConfiguration is not null || service.Kind.Equals(".NET", StringComparison.OrdinalIgnoreCase))
            .Select(service => new ApiChange(service,
                new Uri(service.Url), new Uri(candidate.Services.Single(next => next.Id == service.Id).Url)))
            .Where(change => change.Before.Port != change.After.Port).ToArray();
        if (apis.Length == 0) return result;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frontend in original.Services)
        {
            var directory = store.ResolveWorkingDirectory(original, frontend);
            if (!File.Exists(Path.Combine(directory, "angular.json"))
                && !frontend.Kind.Equals("Angular", StringComparison.OrdinalIgnoreCase)) continue;
            // Custom CLI overrides can select a different proxy or production configuration.
            // Do not silently edit the default file when it would not affect this launch.
            if (Regex.IsMatch(frontend.StartCommand, @"(?:^|\s)(?:--configuration|--proxy-config|--proxyConfig|--project|-c)(?:[=\s]|$)"))
                throw new InvalidOperationException($"{frontend.Name}: automatic API port sync requires the default Angular serve configuration without command-line configuration/proxy overrides.");
            foreach (var path in FindProxyFiles(directory)) files.Add(path);
        }
        if (files.Count == 0) return result; // Projects without Angular remain supported.

        var matched = new HashSet<string>();
        foreach (var path in files)
        {
            var before = File.ReadAllBytes(path);
            var after = RewriteTargets(before, apis, matched, path);
            if (!before.AsSpan().SequenceEqual(after)) result._changes.Add(new(path, before, after));
        }
        foreach (var api in apis)
            if (!matched.Contains(api.Profile.Id))
                throw new InvalidOperationException($"No local Angular proxy target matches {api.Profile.Name}'s saved port {api.Before.Port}. Correct the development proxy target before saving the new port.");
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

    private static byte[] RewriteTargets(byte[] content, ApiChange[] apis, HashSet<string> matched, string path)
    {
        var offset = content.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        using var document = JsonDocument.Parse(content.AsMemory(offset), JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{path}: use a JSON object mapping routes to proxy options for automatic API port sync.");
        var reader = new Utf8JsonReader(content.AsSpan(offset), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        using var output = new MemoryStream();
        var copied = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2
                || !reader.ValueTextEquals("target")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
            var value = reader.GetString()!;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var target) || !target.IsLoopback
                || target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0) continue;
            // Match against the original ports so simultaneous edits cannot cascade.
            var api = apis.SingleOrDefault(change => change.Before.Port == target.Port && change.Before.Scheme == target.Scheme);
            if (api is null) continue;
            matched.Add(api.Profile.Id);
            // Replace only the authority's port; preserve host spelling, path, query and slash style.
            var authorityEnd = value.IndexOfAny(['/', '?', '#'], value.IndexOf("://", StringComparison.Ordinal) + 3);
            if (authorityEnd < 0) authorityEnd = value.Length;
            var authority = value[..authorityEnd];
            var hostStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
            var colon = authority.LastIndexOf(':');
            var hasPort = colon >= hostStart && (authority[hostStart] != '[' || colon > authority.IndexOf(']'));
            var updated = (hasPort ? authority[..colon] : authority) + ":" + api.After.Port + value[authorityEnd..];
            var start = checked(offset + (int)reader.TokenStartIndex);
            output.Write(content, copied, start - copied);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(updated));
            copied = checked(offset + (int)reader.BytesConsumed);
        }
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
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
