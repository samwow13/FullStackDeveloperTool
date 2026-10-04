using System.IO;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace FullStackLauncher.Services;

public enum DetectedProjectType { FullStack, Console, FlutterDart }

public sealed record ProjectTypeDiscoveryResult(
    DetectedProjectType? SelectedType,
    IReadOnlyList<string> Notes,
    FullStackProjectDiscoveryResult FullStack,
    DartProjectDiscoveryResult Dart,
    IReadOnlyList<string> ConsoleDirectories);

/// <summary>Infers an initial setup choice from existing manifests without running commands or changing files.</summary>
public static class ProjectTypeDiscovery
{
    private const int MaximumDepth = 3;
    private const int MaximumDirectories = 600;
    private const int MaximumEntries = 2400;
    private const int MaximumProjectCharacters = 256 * 1024;

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".angular", ".dart_tool", ".pub-cache", ".symlinks", ".plugin_symlinks",
        ".plugins", "node_modules", "bin", "obj", "dist", "build", "out", "publish", "artifacts",
        "coverage", "TestResults", "vendor", "tmp", "temp", "logs", "Pods", "DerivedData",
        "ephemeral", "test", "tests", "spec", "specs", "e2e", "cypress", "playwright", "__tests__",
        "example", "examples", "sample", "samples", "demo", "demos"
    };

    public static ProjectTypeDiscoveryResult Discover(string rootPath)
    {
        string root;
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return EmptyResult("Choose an existing project folder to detect its type.");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            if (!Directory.Exists(root))
                return EmptyResult("Project folder is missing or unavailable. Choose the app type manually.");
            if (IsLinked(root))
                return EmptyResult("Linked folders are excluded from detection. Choose the app type manually.");
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return EmptyResult("Could not inspect the project folder. Choose the app type manually.");
        }

        var notes = new List<string>();
        FullStackProjectDiscoveryResult fullStack;
        DartProjectDiscoveryResult dart;
        try { fullStack = FullStackProjectDiscovery.Discover(root); }
        catch (Exception ex) when (IsAccessError(ex))
        {
            fullStack = new([], [], []);
            notes.Add("Could not inspect Angular or API folders. Choose their working folders manually.");
        }
        try { dart = DartProjectDiscovery.Discover(root); }
        catch (Exception ex) when (IsAccessError(ex))
        {
            dart = new([], [], []);
            notes.Add("Could not inspect Flutter or Dart packages. Choose their working folders manually.");
        }

        // Detection shares the existing folder discoveries so the UI can reuse their candidates.
        // Missing another family's manifests is expected; keep only actionable scan diagnostics.
        notes.AddRange(fullStack.Notes.Where(note => !note.StartsWith("No .NET API folder", StringComparison.Ordinal) &&
            !note.StartsWith("No Angular frontend folder", StringComparison.Ordinal)));
        notes.AddRange(dart.Notes.Where(note => !note.StartsWith("No recognizable Flutter or Dart", StringComparison.Ordinal)));

        var candidates = new List<Candidate>();
        foreach (var path in fullStack.FrontendDirectories)
            if (IsIncludedCandidate(root, path) && HasUnlinkedAngularManifests(path))
                candidates.Add(new(DetectedProjectType.FullStack, path));
        foreach (var path in dart.FlutterDirectories.Concat(dart.DartDirectories))
            if (IsIncludedCandidate(root, path))
                candidates.Add(new(DetectedProjectType.FlutterDart, path));
        InspectDotNetProjects(root, candidates, notes);
        var consoles = candidates.Where(candidate => candidate.Type == DetectedProjectType.Console)
            .Select(candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetRelativePath(root, path).Count(character => character == Path.DirectorySeparatorChar))
            .ThenBy(path => SamePath(root, path) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal).ToArray();

        // A manifest in the selected folder describes that app more directly than nested tools
        // or examples. Otherwise framework-specific apps take precedence over console helpers.
        var rootTypes = candidates.Where(candidate => SamePath(root, candidate.Path))
            .Select(candidate => candidate.Type).Distinct().ToArray();
        var specificTypes = candidates.Where(candidate => candidate.Type != DetectedProjectType.Console)
            .Select(candidate => candidate.Type).Distinct().ToArray();
        var types = rootTypes.Length > 0 ? rootTypes : specificTypes.Length > 0 ? specificTypes :
            candidates.Select(candidate => candidate.Type).Distinct().ToArray();
        var selected = types.Length == 1 ? types[0] : (DetectedProjectType?)null;
        if (types.Length > 1)
            notes.Insert(0, "Multiple app types found. Choose the app type for this project.");
        else if (types.Length == 0)
            notes.Insert(0, "No recognizable app type found. Choose the app type manually.");
        else
            notes.Insert(0, selected switch
            {
                DetectedProjectType.FullStack => "Detected Angular or .NET API project.",
                DetectedProjectType.Console => "Detected .NET console project.",
                _ => "Detected Flutter or Dart package."
            });
        if (selected == DetectedProjectType.Console && consoles.Length > 1)
            notes.Add("Multiple console folders found. Choose the correct working folder.");

        return new(selected, notes.Distinct(StringComparer.Ordinal).ToArray(), fullStack, dart, consoles);
    }

    private static void InspectDotNetProjects(string root, List<Candidate> candidates, List<string> notes)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        var entries = 0;
        var limited = false;
        while (pending.Count > 0 && visited < MaximumDirectories && !limited)
        {
            var (path, depth) = pending.Dequeue();
            visited++;
            try
            {
                if (IsLinked(path)) continue;
                var projects = new List<string>();
                var children = new List<string>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
                {
                    if (++entries > MaximumEntries) { limited = true; break; }
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        if (Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                            projects.Add(entry);
                        continue;
                    }
                    if (depth == MaximumDepth || SkippedNames.Contains(Path.GetFileName(entry))) continue;
                    if (visited + pending.Count + children.Count >= MaximumDirectories)
                    {
                        limited = true;
                        break;
                    }
                    children.Add(entry);
                }
                foreach (var project in projects)
                {
                    var type = ReadDotNetType(project, out var note);
                    if (type.HasValue && (type != DetectedProjectType.Console || projects.Count == 1 && !limited))
                        candidates.Add(new(type.Value, path));
                    if (note is not null) notes.Add(note);
                }
                if (projects.Count > 1)
                    notes.Add($"Multiple .NET project files found in {path}. Choose the working folder and command manually.");
                foreach (var child in children.OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(child => child, StringComparer.Ordinal))
                    pending.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                notes.Add($"Could not inspect .NET projects: {path}");
            }
        }
        if (limited || pending.Count > 0)
            notes.Add("Project type search reached its limit. Choose the app type manually if needed.");
    }

    private static DetectedProjectType? ReadDotNetType(string path, out string? note)
    {
        note = null;
        try
        {
            if (IsLinked(path)) return null;
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumProjectCharacters
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "Project") return null;
            var properties = document.Root.Elements().Where(element => element.Name.LocalName == "PropertyGroup")
                .SelectMany(group => group.Elements()).ToArray();
            bool Enabled(string name) => properties.Any(property => property.Name.LocalName == name &&
                property.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            if (Enabled("IsTestProject") || document.Root.Descendants().Any(element =>
                element.Name.LocalName == "PackageReference" &&
                (element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? "")
                    .Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))) return null;

            var sdks = new[] { document.Root.Attribute("Sdk")?.Value }
                .Concat(document.Root.Elements().Where(element => element.Name.LocalName == "Sdk")
                    .Select(element => element.Attribute("Name")?.Value))
                .Where(value => value is not null)
                .SelectMany(value => value!.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (sdks.Any(sdk => sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                sdk.StartsWith("Microsoft.NET.Sdk.Web/", StringComparison.OrdinalIgnoreCase)))
                return DetectedProjectType.FullStack;

            if (Enabled("UseWPF") || Enabled("UseWindowsForms") || Enabled("UseMaui")) return null;
            var outputs = properties.Where(property => property.Name.LocalName == "OutputType")
                .Select(property => property.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return outputs.Length == 1 && outputs[0].Equals("Exe", StringComparison.OrdinalIgnoreCase)
                ? DetectedProjectType.Console : null;
        }
        catch (Exception ex) when (IsAccessError(ex) || ex is XmlException)
        {
            note = $"Could not read .NET project type: {path}";
            return null;
        }
    }

    private static bool HasUnlinkedAngularManifests(string directory)
    {
        try
        {
            return !IsLinked(Path.Combine(directory, "angular.json")) &&
                !IsLinked(Path.Combine(directory, "package.json"));
        }
        catch (Exception ex) when (IsAccessError(ex)) { return false; }
    }

    private static bool IsIncludedCandidate(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return true;
        return !Path.IsPathRooted(relative) && !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment == ".." || SkippedNames.Contains(segment));
    }

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsLinked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsAccessError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;

    private static ProjectTypeDiscoveryResult EmptyResult(string note) => new(null, [note], new([], [], []), new([], [], []), []);

    private sealed record Candidate(DetectedProjectType Type, string Path);
}
