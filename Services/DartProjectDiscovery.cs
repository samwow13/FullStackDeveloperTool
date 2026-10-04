using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

public sealed record DartProjectDiscoveryResult(
    IReadOnlyList<string> FlutterDirectories,
    IReadOnlyList<string> DartDirectories,
    IReadOnlyList<string> Notes);

/// <summary>Finds existing Dart packages without running tools or modifying files.</summary>
public static class DartProjectDiscovery
{
    private const int MaximumDepth = 3;
    private const int MaximumDirectories = 600;
    private const int MaximumDirectoryEntries = 2400;
    private const int MaximumManifestBytes = 256 * 1024;

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".dart_tool", ".pub-cache", ".symlinks", ".plugin_symlinks",
        ".plugins", "node_modules", "build", "bin", "obj", "dist", "out", "publish",
        "artifacts", "coverage", "vendor", "ephemeral", "Pods", "DerivedData"
    };

    private static readonly HashSet<string> PlatformNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "android", "ios", "linux", "macos", "windows", "web"
    };

    public static DartProjectDiscoveryResult Discover(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project root folder does not exist: {root}");

        var flutter = new List<string>();
        var dart = new List<string>();
        var notes = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        var entries = 0;
        var limited = false;

        while (pending.Count > 0 && visited < MaximumDirectories)
        {
            var (path, depth) = pending.Dequeue();
            visited++;
            try
            {
                if (IsLinked(path))
                {
                    notes.Add($"Skipped linked folder: {path}");
                    continue;
                }

                var kind = ReadProjectKind(path, out var manifestNote);
                if (kind == ProjectKind.Flutter) flutter.Add(path);
                else if (kind == ProjectKind.Dart) dart.Add(path);
                if (manifestNote is not null) notes.Add(manifestNote);
                if (depth == MaximumDepth) continue;

                // Bound both queued folders and enumeration, including excluded entries.
                // Platform children belong to the discovered Flutter package, not another app.
                var children = new List<string>();
                foreach (var child in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
                {
                    if (++entries > MaximumDirectoryEntries)
                    {
                        limited = true;
                        break;
                    }
                    var name = Path.GetFileName(child);
                    if (SkippedNames.Contains(name) || kind == ProjectKind.Flutter && PlatformNames.Contains(name))
                        continue;
                    if (visited + pending.Count + children.Count >= MaximumDirectories)
                    {
                        limited = true;
                        break;
                    }
                    children.Add(child);
                }
                foreach (var child in children.OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(child => child, StringComparer.Ordinal))
                    pending.Enqueue((child, depth + 1));
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                notes.Add($"Could not inspect folder: {path}");
            }
        }

        if (limited || pending.Count > 0)
            notes.Add("Folder search reached its limit. Choose a working folder manually if an app is missing.");
        if (flutter.Count == 0 && dart.Count == 0)
            notes.Add("No recognizable Flutter or Dart pubspec.yaml found.");
        return new DartProjectDiscoveryResult(flutter.ToArray(), dart.ToArray(), notes.ToArray());
    }

    public static bool IsFlutterProject(string directory) => IsProjectKind(directory, ProjectKind.Flutter);

    public static bool IsDartProject(string directory) => IsProjectKind(directory, ProjectKind.Dart);

    private static bool IsProjectKind(string directory, ProjectKind expected)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var path = Path.GetFullPath(directory);
            return Directory.Exists(path) && !IsLinked(path) && ReadProjectKind(path, out _) == expected;
        }
        catch (Exception ex) when (IsAccessError(ex)) { return false; }
    }

    private static ProjectKind ReadProjectKind(string directory, out string? note)
    {
        note = null;
        var manifest = Path.Combine(directory, "pubspec.yaml");
        try
        {
            // File.Exists hides access failures, so open directly and distinguish missing files.
            if (IsLinked(manifest))
            {
                note = $"Skipped linked pubspec.yaml: {directory}";
                return ProjectKind.None;
            }
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaximumManifestBytes)
            {
                note = $"pubspec.yaml exceeds the discovery size limit: {directory}";
                return ProjectKind.None;
            }
            var bytes = new byte[MaximumManifestBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumManifestBytes)
            {
                note = $"pubspec.yaml exceeds the discovery size limit: {directory}";
                return ProjectKind.None;
            }
            var text = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF');
            if (!ManifestParser.TryParse(text, out var manifestMap) ||
                !manifestMap.TryGetValue("name", out var name) || name.Scalar is null ||
                !Regex.IsMatch(name.Scalar, "^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant) ||
                name.Scalar is "true" or "false" or "null")
            {
                note = $"pubspec.yaml is invalid or uses unsupported YAML syntax; review the manifest: {directory}";
                return ProjectKind.None;
            }

            var isFlutter = false;
            foreach (var section in new[] { "dependencies", "dev_dependencies", "dependency_overrides" })
            {
                if (!manifestMap.TryGetValue(section, out var dependencies)) continue;
                if (dependencies.Map is null && dependencies.Scalar is not null)
                    return Unrecognized(directory, out note);
                if (dependencies.IsSequence) return Unrecognized(directory, out note);
                if (dependencies.Map is null) continue;
                foreach (var dependency in dependencies.Map.Values)
                {
                    if (dependency.IsSequence) return Unrecognized(directory, out note);
                    if (dependency.Map is not null &&
                        new[] { "sdk", "hosted", "git", "path" }.Count(dependency.Map.ContainsKey) > 1)
                        return Unrecognized(directory, out note);
                    if (dependency.Map?.TryGetValue("sdk", out var sdk) == true)
                    {
                        // Flutter is the SDK dependency source supported by pub. Do not
                        // reinterpret an invalid/unknown SDK source as a plain Dart package.
                        if (sdk.Scalar != "flutter") return Unrecognized(directory, out note);
                        isFlutter = true;
                    }
                }
            }
            return isFlutter ? ProjectKind.Flutter : ProjectKind.Dart;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProjectKind.None;
        }
        catch (Exception ex) when (IsAccessError(ex) || ex is DecoderFallbackException)
        {
            note = $"Could not read pubspec.yaml: {directory}";
            return ProjectKind.None;
        }
    }

    private static ProjectKind Unrecognized(string directory, out string? note)
    {
        note = $"pubspec.yaml dependencies are invalid or unsupported; review the manifest: {directory}";
        return ProjectKind.None;
    }

    private static bool IsLinked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsAccessError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;

    private enum ProjectKind { None, Flutter, Dart }

    private sealed record YamlValue(string? Scalar = null, Dictionary<string, YamlValue>? Map = null,
        bool IsSequence = false);

    /// <summary>
    /// A deliberately conservative YAML subset for ordinary pubspec files. Unsupported YAML
    /// constructs (including aliases, tags and multiline flow values) require manual review;
    /// they never establish a Flutter dependency. Duplicate keys and malformed structure fail.
    /// </summary>
    private static class ManifestParser
    {
        private sealed record Line(int Indent, string Text);

        public static bool TryParse(string text, out Dictionary<string, YamlValue> map)
        {
            map = new(StringComparer.Ordinal);
            var lines = new List<Line>();
            var blockIndent = -1;
            var ended = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var indent = line.TakeWhile(character => character == ' ').Count();
                if (line.Length == indent) continue;
                if (blockIndent >= 0 && indent > blockIndent) continue;
                blockIndent = -1;
                if (indent > 128 || line[indent] == '\t' || line.Contains('\0')) return false;
                if (!TryStripComment(line[indent..], out var content)) return false;
                content = content.TrimEnd();
                if (content.Length == 0) continue;
                if (content == "---" && indent == 0 && lines.Count == 0 && !ended) continue;
                if (content == "..." && indent == 0 && lines.Count > 0 && !ended)
                {
                    ended = true;
                    continue;
                }
                if (ended || content.StartsWith('%')) return false;

                // Expand compact sequence maps to a sequence item followed by its map.
                if (content.StartsWith("- ", StringComparison.Ordinal) &&
                    TrySplitMapping(content[2..].TrimStart(), out _, out _))
                {
                    lines.Add(new(indent, "-"));
                    content = content[2..].TrimStart();
                    indent += 2;
                }
                if (TrySplitMapping(content, out var key, out var value) &&
                    Regex.IsMatch(value, "^[|>](?:[+-]?[1-9]?|[1-9][+-]?)$", RegexOptions.CultureInvariant))
                {
                    blockIndent = indent;
                    content = key + ": \"\"";
                }
                lines.Add(new(indent, content));
                if (lines.Count > 10000) return false;
            }
            if (lines.Count == 0 || lines[0].Indent != 0) return false;
            var position = 0;
            if (!TryBlock(lines, ref position, 0, 0, out var root) || position != lines.Count || root.Map is null)
                return false;
            map = root.Map;
            return true;
        }

        private static bool TryBlock(List<Line> lines, ref int position, int indent, int depth, out YamlValue node)
        {
            node = new();
            if (depth > 32) return false;
            var sequence = IsSequenceItem(lines[position].Text);
            var map = sequence ? null : new Dictionary<string, YamlValue>(StringComparer.Ordinal);
            while (position < lines.Count && lines[position].Indent == indent)
            {
                var content = lines[position].Text;
                if (sequence != IsSequenceItem(content)) return false;
                string? key = null;
                string value;
                if (sequence) value = content.Length == 1 ? "" : content[2..].TrimStart();
                else
                {
                    if (!TrySplitMapping(content, out var rawKey, out value) ||
                        !TryScalar(rawKey.Trim(), out key) || string.IsNullOrEmpty(key) || map!.ContainsKey(key))
                        return false;
                }
                position++;
                YamlValue child;
                if (value.Length == 0 && position < lines.Count && lines[position].Indent > indent)
                {
                    if (!TryBlock(lines, ref position, lines[position].Indent, depth + 1, out child)) return false;
                }
                else
                {
                    if (!TryInline(value, depth + 1, out child)) return false;
                    if (position < lines.Count && lines[position].Indent > indent) return false;
                }
                if (!sequence) map!.Add(key!, child);
            }
            node = new(Map: map, IsSequence: sequence);
            return position == lines.Count || lines[position].Indent < indent;
        }

        private static bool TryInline(string text, int depth, out YamlValue node)
        {
            node = new();
            text = text.Trim();
            if (depth > 32) return false;
            if (text.Length == 0 || text is "null" or "~") return true;
            if (text[0] is '{' or '[')
            {
                var isMap = text[0] == '{';
                if (text[^1] != (isMap ? '}' : ']') || !TryFlowParts(text[1..^1], out var parts)) return false;
                var map = isMap ? new Dictionary<string, YamlValue>(StringComparer.Ordinal) : null;
                foreach (var part in parts)
                {
                    if (isMap)
                    {
                        if (!TrySplitMapping(part, out var rawKey, out var value) ||
                            !TryScalar(rawKey.Trim(), out var key) || string.IsNullOrEmpty(key) ||
                            !TryInline(value, depth + 1, out var child) || !map!.TryAdd(key, child)) return false;
                    }
                    else if (!TryInline(part, depth + 1, out _)) return false;
                }
                node = new(Map: map, IsSequence: !isMap);
                return true;
            }
            if (!TryScalar(text, out var scalar)) return false;
            node = new(Scalar: scalar);
            return true;
        }

        private static bool TryScalar(string text, out string? value)
        {
            value = null;
            if (text.Length == 0) return false;
            if (text[0] == '"')
            {
                try { value = JsonSerializer.Deserialize<string>(text); return value is not null; }
                catch (JsonException) { return false; }
            }
            if (text[0] == '\'')
            {
                if (text.Length < 2 || text[^1] != '\'') return false;
                var inner = text[1..^1];
                for (var index = 0; index < inner.Length; index++)
                    if (inner[index] == '\'' && (++index == inner.Length || inner[index] != '\'')) return false;
                value = inner.Replace("''", "'", StringComparison.Ordinal);
                return true;
            }
            if ("&*!|>%@`{},[]?#".Contains(text[0]) || text == "---" || text == "..." ||
                IsSequenceItem(text) || TrySplitMapping(text, out _, out _)) return false;
            value = text;
            return true;
        }

        private static bool TrySplitMapping(string text, out string key, out string value)
        {
            key = value = "";
            var quote = '\0';
            var nesting = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (UpdateQuote(text, ref index, ref quote)) continue;
                if (quote != '\0') continue;
                if (character is '{' or '[') nesting++;
                else if (character is '}' or ']') nesting--;
                else if (character == ':' && nesting == 0 &&
                         (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1])))
                {
                    key = text[..index].TrimEnd();
                    value = text[(index + 1)..].Trim();
                    return true;
                }
            }
            return false;
        }

        private static bool TryStripComment(string text, out string content)
        {
            content = text;
            var quote = '\0';
            for (var index = 0; index < text.Length; index++)
            {
                if (UpdateQuote(text, ref index, ref quote)) continue;
                if (quote == '\0' && text[index] == '#' && (index == 0 || char.IsWhiteSpace(text[index - 1])))
                {
                    content = text[..index];
                    break;
                }
            }
            return quote == '\0';
        }

        private static bool TryFlowParts(string text, out List<string> parts)
        {
            parts = new();
            var quote = '\0';
            var closing = new Stack<char>();
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (UpdateQuote(text, ref index, ref quote)) continue;
                if (quote != '\0') continue;
                var character = text[index];
                if (character is '{' or '[') closing.Push(character == '{' ? '}' : ']');
                else if (character is '}' or ']')
                {
                    if (closing.Count == 0 || closing.Pop() != character) return false;
                }
                else if (character == ',' && closing.Count == 0)
                {
                    var part = text[start..index].Trim();
                    if (part.Length == 0) return false;
                    parts.Add(part);
                    start = index + 1;
                }
            }
            if (quote != '\0' || closing.Count > 0) return false;
            var last = text[start..].Trim();
            if (last.Length > 0) parts.Add(last);
            return true;
        }

        private static bool UpdateQuote(string text, ref int index, ref char quote)
        {
            var character = text[index];
            if (quote == '"' && character == '\\') { index++; return true; }
            if (quote == '\'' && character == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
            { index++; return true; }
            if (quote != '\0' && character == quote) { quote = '\0'; return true; }
            if (quote == '\0' && character is '\'' or '"' &&
                (index == 0 || char.IsWhiteSpace(text[index - 1]) || "[{,:".Contains(text[index - 1])))
            { quote = character; return true; }
            return false;
        }

        private static bool IsSequenceItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
    }
}
