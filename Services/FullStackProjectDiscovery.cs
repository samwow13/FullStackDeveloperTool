using System.IO;
using System.Security;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FullStackLauncher.Services;

public sealed record FullStackProjectDiscoveryResult(
    IReadOnlyList<string> ApiDirectories,
    IReadOnlyList<string> FrontendDirectories,
    IReadOnlyList<string> Notes);

/// <summary>Finds likely .NET APIs and Angular workspaces without starting commands or changing project files.</summary>
public static class FullStackProjectDiscovery
{
    private const int MaximumDepth = 3;
    private const int MaximumDirectories = 600;

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".angular", ".next", ".nuxt", ".svelte-kit", ".turbo",
        "node_modules", "bin", "obj", "dist", "build", "out", "publish", "artifacts",
        "coverage", "TestResults", "vendor", "tmp", "temp", "logs",
        "test", "tests", "spec", "specs", "e2e", "cypress", "playwright", "__tests__"
    };

    /// <summary>Reads only loopback Project launch URLs; never returns environment variables or other profile data.</summary>
    public static string? TryGetApiUrl(string apiDirectory)
    {
        try
        {
            const int maximumBytes = 1024 * 1024;
            var path = Path.Combine(Path.GetFullPath(apiDirectory), "Properties", "launchSettings.json");
            using var stream = File.OpenRead(path);
            var bytes = new byte[maximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > maximumBytes) return null;

            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Object) return null;

            var urls = new List<(string Url, int ProfileIndex, int UrlIndex, bool IsHttp)>();
            var profileIndex = 0;
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.ValueKind != JsonValueKind.Object ||
                    !profile.Value.TryGetProperty("commandName", out var commandName) ||
                    commandName.ValueKind != JsonValueKind.String ||
                    !string.Equals(commandName.GetString(), "Project", StringComparison.OrdinalIgnoreCase) ||
                    !profile.Value.TryGetProperty("applicationUrl", out var applicationUrl) ||
                    applicationUrl.ValueKind != JsonValueKind.String)
                {
                    profileIndex++;
                    continue;
                }

                var urlIndex = 0;
                foreach (var raw in (applicationUrl.GetString() ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsLoopback &&
                        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                        urls.Add((uri.GetLeftPart(UriPartial.Authority), profileIndex, urlIndex,
                            uri.Scheme == "http"));
                    urlIndex++;
                }
                profileIndex++;
            }

            return urls.OrderByDescending(url => url.IsHttp)
                .ThenBy(url => url.ProfileIndex)
                .ThenBy(url => url.UrlIndex)
                .Select(url => url.Url)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static FullStackProjectDiscoveryResult Discover(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project root folder does not exist: {root}");

        var apis = new List<Candidate>();
        var frontends = new List<Candidate>();
        var notes = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;

        while (pending.Count > 0 && visited < MaximumDirectories)
        {
            var (path, depth) = pending.Dequeue();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    notes.Add($"Skipped linked folder: {path}");
                    continue;
                }
            }
            catch (Exception ex) when (IsFolderAccessError(ex))
            {
                notes.Add($"Could not inspect folder: {path}");
                continue;
            }

            visited++;
            InspectDirectory(path, depth, apis, frontends, notes);
            if (depth == MaximumDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (IsFolderAccessError(ex))
            {
                notes.Add($"Could not search subfolders: {path}");
                continue;
            }

            foreach (var child in children
                .Where(child => !SkippedNames.Contains(Path.GetFileName(child)))
                .OrderByDescending(child => TraversalRank(Path.GetFileName(child)))
                .ThenBy(child => child, StringComparer.OrdinalIgnoreCase)
                .ThenBy(child => child, StringComparer.Ordinal))
                pending.Enqueue((child, depth + 1));
        }

        if (pending.Count > 0) notes.Add($"Search stopped after {MaximumDirectories} folders. Choose folders manually if an app is missing.");
        var orderedApis = OrderCandidates(apis);
        var orderedFrontends = OrderCandidates(frontends);
        if (orderedApis.Length == 0) notes.Add("No .NET API folder found. Choose its working folder manually.");
        if (orderedFrontends.Length == 0) notes.Add("No Angular frontend folder found. Choose its working folder manually.");
        if (orderedApis.Length > 1 && orderedApis[0].Rank == orderedApis[1].Rank)
            notes.Add("Multiple API folders have the same rank. Choose the correct one.");
        if (orderedFrontends.Length > 1 && orderedFrontends[0].Rank == orderedFrontends[1].Rank)
            notes.Add("Multiple frontend folders have the same rank. Choose the correct one.");

        return new FullStackProjectDiscoveryResult(
            orderedApis.Select(candidate => candidate.Path).ToArray(),
            orderedFrontends.Select(candidate => candidate.Path).ToArray(),
            notes);
    }

    private static void InspectDirectory(string path, int depth, List<Candidate> apis,
        List<Candidate> frontends, List<string> notes)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        try
        {
            var projectFiles = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projectFiles.Length == 1)
            {
                var projectName = Path.GetFileNameWithoutExtension(projectFiles[0]);
                if (TryReadWebSdk(projectFiles[0], out var webSdk))
                {
                    var nameRank = Math.Max(ApiNameRank(folderName), ApiNameRank(projectName));
                    if (webSdk || nameRank > 0)
                        apis.Add(new(path, nameRank + (webSdk ? 30 : 0) - depth * 8));
                }
                else if (ApiNameRank(folderName) > 0 || ApiNameRank(projectName) > 0)
                    notes.Add($"Could not read API project file: {projectFiles[0]}");
            }
            else if (projectFiles.Length > 1 &&
                (ApiNameRank(folderName) > 0 || projectFiles.Any(file => ApiNameRank(Path.GetFileNameWithoutExtension(file)) > 0)))
                notes.Add($"Multiple .csproj files found in {path}. Choose an API working folder and command manually.");
        }
        catch (Exception ex) when (IsFolderAccessError(ex))
        {
            notes.Add($"Could not inspect .NET projects: {path}");
        }

        if (File.Exists(Path.Combine(path, "angular.json")) && File.Exists(Path.Combine(path, "package.json")))
            frontends.Add(new(path, FrontendNameRank(folderName) - depth * 8));
    }

    private static bool TryReadWebSdk(string projectFile, out bool webSdk)
    {
        webSdk = false;
        try
        {
            using var reader = XmlReader.Create(projectFile, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "Project") return false;
            webSdk = ContainsWebSdk(document.Root.Attribute("Sdk")?.Value) ||
                document.Root.Elements().Any(element => element.Name.LocalName == "Sdk" &&
                    ContainsWebSdk(element.Attribute("Name")?.Value));
            return true;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static bool ContainsWebSdk(string? value) => value?.Split(';', StringSplitOptions.TrimEntries)
        .Any(sdk => sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
            sdk.StartsWith("Microsoft.NET.Sdk.Web/", StringComparison.OrdinalIgnoreCase)) == true;

    private static int ApiNameRank(string name)
    {
        var normalized = NormalizeName(name);
        return normalized switch
        {
            "api" or "webapi" => 110,
            "backend" or "back" => 100,
            "server" => 90,
            _ when normalized.EndsWith("api", StringComparison.Ordinal) => 80,
            _ when normalized.EndsWith("backend", StringComparison.Ordinal) => 75,
            _ when normalized.EndsWith("server", StringComparison.Ordinal) => 65,
            _ => 0
        };
    }

    private static int FrontendNameRank(string name)
    {
        var normalized = NormalizeName(name);
        return normalized switch
        {
            "frontend" or "angular" => 110,
            "client" or "webapp" => 100,
            "web" or "ui" => 90,
            _ when normalized.EndsWith("frontend", StringComparison.Ordinal) => 80,
            _ when normalized.EndsWith("client", StringComparison.Ordinal) => 75,
            _ when normalized.EndsWith("webapp", StringComparison.Ordinal) => 70,
            _ when normalized.EndsWith("angular", StringComparison.Ordinal) => 65,
            _ => 0
        };
    }

    private static int TraversalRank(string name)
    {
        var normalized = NormalizeName(name);
        if (normalized is "src" or "apps" or "services" or "packages") return 70;
        return Math.Max(ApiNameRank(name), FrontendNameRank(name));
    }

    private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());

    private static Candidate[] OrderCandidates(IEnumerable<Candidate> candidates) => candidates
        .OrderByDescending(candidate => candidate.Rank)
        .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
        .ToArray();

    private static bool IsFolderAccessError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

    private sealed record Candidate(string Path, int Rank);
}
