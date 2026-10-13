using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

/// <summary>Remembers credential-free Azure DevOps project URLs and recently picked repository IDs.</summary>
public static class AzureDevOpsImportPreferences
{
    private const int Version = 2;
    private const int LegacyFileLimit = 8192;
    private const int FileLimit = 256 * 1024;
    private const int ProjectLimit = 32;
    private const int RepositoryLimit = 10;
    private static readonly object SyncRoot = new();

    public static string? LoadLastProjectUrl() => LoadPreferences().LastProjectUrl;

    public static IReadOnlyList<string> LoadRecentRepositoryIds(string projectUrl)
    {
        var normalizedUrl = NormalizeProjectUrl(projectUrl);
        return LoadPreferences().RecentProjects
            .FirstOrDefault(project => project.ProjectUrl.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))?
            .RepositoryIds.ToArray() ?? [];
    }

    // Call only after the current URL has completed a successful provider connection.
    public static void SaveLastProjectUrl(string projectUrl)
    {
        var normalizedUrl = NormalizeProjectUrl(projectUrl);
        SavePreferences(preferences => preferences.LastProjectUrl = normalizedUrl);
    }

    // Call only after selecting a repository from successfully loaded provider metadata.
    public static void RememberRepository(string projectUrl, string repositoryId)
    {
        var normalizedUrl = NormalizeProjectUrl(projectUrl);
        if (!Guid.TryParse(repositoryId, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Choose a repository from the current Azure DevOps project list.");
        var normalizedId = id.ToString("D");
        SavePreferences(preferences =>
        {
            preferences.LastProjectUrl ??= normalizedUrl;
            var project = preferences.RecentProjects.FirstOrDefault(candidate =>
                candidate.ProjectUrl.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase));
            if (project is null) project = new RecentProject { ProjectUrl = normalizedUrl };
            else preferences.RecentProjects.Remove(project);
            project.RepositoryIds.RemoveAll(candidate => candidate == normalizedId);
            project.RepositoryIds.Insert(0, normalizedId);
            if (project.RepositoryIds.Count > RepositoryLimit)
                project.RepositoryIds.RemoveRange(RepositoryLimit, project.RepositoryIds.Count - RepositoryLimit);
            preferences.RecentProjects.Insert(0, project);
            if (preferences.RecentProjects.Count > ProjectLimit)
                preferences.RecentProjects.RemoveRange(ProjectLimit, preferences.RecentProjects.Count - ProjectLimit);
        });
    }

    private static Preferences LoadPreferences()
    {
        lock (SyncRoot)
        {
            var path = GetPreferencesPath();
            try { return ReadPreferences(path).Preferences; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    "The saved Azure DevOps preferences could not be read. You can still connect and choose a repository; the saved file remains unchanged.");
            }
        }
    }

    private static void SavePreferences(Action<Preferences> update)
    {
        lock (SyncRoot)
        {
            var path = GetPreferencesPath();
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Serialize saves across launcher processes without creating another data file.
                var mutexName = "Local\\FullStackLauncher.AzureDevOpsImport." +
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                using var mutex = new Mutex(false, mutexName);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new InvalidOperationException(
                            "Azure DevOps preferences are being saved by another launcher. Your selection is ready; retry to remember it.");

                    // Never replace an unreadable, malformed, or unsupported existing preference.
                    var existing = ReadPreferences(path);
                    update(existing.Preferences);
                    var content = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        version = Version,
                        lastProjectUrl = existing.Preferences.LastProjectUrl,
                        recentRepositories = existing.Preferences.RecentProjects.Select(project => new
                        {
                            projectUrl = project.ProjectUrl,
                            repositoryIds = project.RepositoryIds
                        })
                    });
                    if (content.Length > FileLimit) throw InvalidSavedPreferences();
                    if (existing.Content is not null && existing.Content.AsSpan().SequenceEqual(content)) return;
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(content);
                        stream.Flush(flushToDisk: true);
                    }
                    var current = ReadPreferences(path);
                    if ((existing.Content is null) != (current.Content is null) ||
                        (existing.Content is not null && !existing.Content.AsSpan().SequenceEqual(current.Content)))
                        throw new InvalidOperationException(
                            "Saved Azure DevOps preferences changed during this selection. Your selection is ready; retry to remember it.");
                    if (existing.Content is null) File.Move(temporaryPath, path);
                    else File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    "Azure DevOps preferences could not be saved. Your selection is ready; check local folder access, then retry to remember it.");
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static string GetPreferencesPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException(
                "The Windows user data folder is unavailable. You can still connect and choose a repository; preferences cannot be remembered until the user profile is available.");
        return Path.Combine(localAppData, "FullStackLauncher", "azure-devops-import.json");
    }

    private static SavedPreferences ReadPreferences(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new(new Preferences(), null); }
        catch (DirectoryNotFoundException) { return new(new Preferences(), null); }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw InvalidSavedPreferences();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > FileLimit) throw InvalidSavedPreferences();
        var content = new byte[FileLimit + 1];
        var count = 0;
        while (count < content.Length)
        {
            var read = stream.Read(content, count, content.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count is 0 or > FileLimit) throw InvalidSavedPreferences();

        try
        {
            using var document = JsonDocument.Parse(content.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            RequireProperties(root, "version", "lastProjectUrl", "recentRepositories");
            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version) ||
                version is not (1 or Version) || !root.TryGetProperty("lastProjectUrl", out var urlElement))
                throw InvalidSavedPreferences();
            var preferences = new Preferences { LastProjectUrl = ReadProjectUrl(urlElement) };
            var hasRecentRepositories = root.TryGetProperty("recentRepositories", out var recentElement);
            if (version == 1)
            {
                if (hasRecentRepositories || count > LegacyFileLimit) throw InvalidSavedPreferences();
            }
            else
            {
                if (!hasRecentRepositories || recentElement.ValueKind != JsonValueKind.Array ||
                    recentElement.GetArrayLength() > ProjectLimit) throw InvalidSavedPreferences();
                var projectUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var projectElement in recentElement.EnumerateArray())
                {
                    RequireProperties(projectElement, "projectUrl", "repositoryIds");
                    if (!projectElement.TryGetProperty("projectUrl", out var projectUrlElement) ||
                        !projectElement.TryGetProperty("repositoryIds", out var idsElement) ||
                        idsElement.ValueKind != JsonValueKind.Array || idsElement.GetArrayLength() is 0 or > RepositoryLimit)
                        throw InvalidSavedPreferences();
                    var project = new RecentProject { ProjectUrl = ReadProjectUrl(projectUrlElement) };
                    if (!projectUrls.Add(project.ProjectUrl)) throw InvalidSavedPreferences();
                    var repositoryIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var idElement in idsElement.EnumerateArray())
                    {
                        if (idElement.ValueKind != JsonValueKind.String) throw InvalidSavedPreferences();
                        var value = idElement.GetString();
                        if (value is null || !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty ||
                            value != id.ToString("D") || !repositoryIds.Add(value)) throw InvalidSavedPreferences();
                        project.RepositoryIds.Add(value);
                    }
                    preferences.RecentProjects.Add(project);
                }
            }
            return new(preferences, content[..count]);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw InvalidSavedPreferences();
        }
    }

    private static void RequireProperties(JsonElement element, params string[] allowedNames)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidSavedPreferences();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowedNames.Contains(property.Name, StringComparer.Ordinal) || !seenNames.Add(property.Name))
                throw InvalidSavedPreferences();
    }

    private static string ReadProjectUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw InvalidSavedPreferences();
        var value = element.GetString();
        if (value is null || AzureDevOpsImportService.ParseProjectUrl(value).ProjectUrl != value)
            throw InvalidSavedPreferences();
        return value;
    }

    private static string NormalizeProjectUrl(string projectUrl)
    {
        var normalizedUrl = AzureDevOpsImportService.ParseProjectUrl(projectUrl).ProjectUrl;
        // Reject a canonical URL that would exceed the parser's own saved-value bound.
        _ = AzureDevOpsImportService.ParseProjectUrl(normalizedUrl);
        return normalizedUrl;
    }

    private static InvalidOperationException InvalidSavedPreferences() => new(
        "The saved Azure DevOps preferences file is invalid or unsupported. You can still connect and choose a repository; the saved file remains unchanged. Move the file aside before remembering new preferences.");

    private sealed class Preferences
    {
        public string? LastProjectUrl { get; set; }
        public List<RecentProject> RecentProjects { get; } = [];
    }

    private sealed class RecentProject
    {
        public string ProjectUrl { get; init; } = "";
        public List<string> RepositoryIds { get; } = [];
    }

    private sealed record SavedPreferences(Preferences Preferences, byte[]? Content);
}
