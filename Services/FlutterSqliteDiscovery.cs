using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

public sealed record FlutterSqliteObservation(string Path, string Name, string Evidence);

/// <summary>
/// Finds existing local SQLite files correlated with a Flutter app's source. This is
/// file discovery, not proof that the app currently has a database connection open.
/// Call from a worker thread; discovery never invokes Flutter or a SQLite provider.
/// </summary>
public static class FlutterSqliteDiscovery
{
    private const int MaximumTextBytes = 256 * 1024;
    private const int MaximumSourceBytes = 4 * 1024 * 1024;
    private const int MaximumFiles = 192;
    private const int MaximumEntries = 2400;
    private const int MaximumDirectories = 128;
    private const int MaximumCandidates = 128;
    private static readonly TimeSpan MaximumWorkTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".dart_tool", ".pub-cache", ".symlinks", ".plugin_symlinks",
        "node_modules", "build", "bin", "obj", "dist", "out", "publish", "artifacts",
        "coverage", "vendor", "ephemeral", "Pods", "DerivedData", "test", "tests",
        "integration_test", "examples", "example", "assets"
    };
    private static readonly HashSet<string> TemplateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "test", "tests", "integration_test", "examples", "example"
    };
    private static readonly Regex Assignments = Pattern(
        @"\b(?:final|const|var|String|Directory|File)\s+(?:(?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*[?]?\s+)?(?<name>[A-Za-z_]\w*)\s*=(?<value>[^;]{1,2048});");
    private static readonly Regex JoinCalls = Pattern(@"\b(?:(?:[A-Za-z_]\w*)\.)?join\s*\(");
    private static readonly Regex SimpleReference = Pattern(@"^(?<name>[A-Za-z_]\w*)(?:\.path)?$");
    private static readonly Regex WindowsBranch = Pattern(@"^Platform\.isWindows\s*\?\s*");
    private static readonly Regex EnvironmentPath = Pattern(
        "^Platform\\.environment\\s*\\[\\s*['\"](?<name>LOCALAPPDATA|APPDATA)['\"]\\s*\\]");
    private static readonly Regex PathCall = Pattern(
        @"^(?:(?:[A-Za-z_]\w*)\.)?(?<name>join|Directory|File|getDatabasesPath|getApplicationSupportDirectory|getApplicationCacheDirectory|getApplicationDocumentsDirectory)\s*\(");

    public static IReadOnlyList<FlutterSqliteObservation> Discover(string workingDirectory,
        string projectRoot, string? configuredSqlitePath = null, CancellationToken cancellationToken = default)
    {
        var budget = new WorkBudget(cancellationToken);
        var observations = new Dictionary<string, FlutterSqliteObservation>(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var working = LocalPath(workingDirectory);
            var root = LocalPath(projectRoot);
            if (working is null || root is null || !IsUnlinkedPath(working) || !Directory.Exists(working))
                return [];

            void Inspect(string? candidate, string evidence, bool configuredPath = false)
            {
                if (budget.Expired || candidate is null || candidates.Count >= MaximumCandidates) return;
                var path = LocalPath(candidate);
                if (path is null || !configuredPath && IsProjectTemplatePath(working, path) ||
                    !candidates.Add(path) || !IsAvailable(path)) return;
                observations[path] = new(path, Path.GetFileName(path), evidence);
            }

            if (!string.IsNullOrWhiteSpace(configuredSqlitePath))
            {
                var configured = LocalPath(configuredSqlitePath, root);
                if (configured is not null && IsWithin(root, configured))
                    Inspect(configured, "Configured project file; SQLite header verified.", configuredPath: true);
            }

            // Existing settings can outlive the package. Do not infer Flutter from a
            // filename or inspect unrelated user storage when no Flutter manifest exists.
            if (budget.Expired || !DartProjectDiscovery.IsFlutterProject(working))
                return observations.Values.ToArray();

            var windowsIdentity = ReadWindowsIdentity(working);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ffi = false;
            var sourceBytes = 0;
            var sourceFiles = 0;
            foreach (var path in EnumerateBounded(Path.Combine(working, "lib"), 8, budget))
            {
                if (budget.Expired || sourceFiles >= MaximumFiles || sourceBytes >= MaximumSourceBytes) break;
                if (!Path.GetExtension(path).Equals(".dart", StringComparison.OrdinalIgnoreCase)) continue;
                var text = ReadText(path, Math.Min(MaximumTextBytes, MaximumSourceBytes - sourceBytes));
                if (text is null) continue;
                sourceFiles++;
                sourceBytes += Encoding.UTF8.GetByteCount(text);
                var source = Lex(text);
                ffi |= source.Literals.Any(literal =>
                    literal.Value?.StartsWith("package:sqflite_common_ffi/", StringComparison.Ordinal) == true);
                foreach (var literal in source.Literals)
                {
                    if (budget.Expired) break;
                    if (!IsDatabasePath(literal.Value)) continue;
                    var value = literal.Value!;
                    var candidate = LocalPath(value, working);
                    if (candidate is null || IsProjectTemplatePath(working, candidate)) continue;
                    if (fileNames.Count < MaximumCandidates)
                        fileNames.Add(Path.GetFileName(value.Replace('/', Path.DirectorySeparatorChar)));
                    Inspect(candidate, "File referenced by app source; SQLite header verified.");
                }

                // Resolve only a small, deterministic Dart path subset. Unsupported
                // expressions stay unknown; no code or dynamic filesystem APIs run.
                var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
                var assignments = Assignments.Matches(source.Mask).Cast<Match>().Take(256).ToArray();
                for (var pass = 0; pass < 3 && !budget.Expired; pass++)
                {
                    foreach (var assignment in assignments)
                    {
                        if (budget.Expired) break;
                        var group = assignment.Groups["value"];
                        var value = ResolvePath(source.Text.Substring(group.Index, group.Length), symbols,
                            working, windowsIdentity, ffi, 0);
                        if (value is not null) symbols[assignment.Groups["name"].Value] = value;
                    }
                }
                foreach (Match call in JoinCalls.Matches(source.Mask))
                {
                    if (budget.Expired) break;
                    var close = ClosingParenthesis(source.Mask, call.Index + call.Length - 1);
                    if (close < 0 || close - call.Index > 2048) continue;
                    var value = ResolvePath(source.Text[call.Index..(close + 1)], symbols,
                        working, windowsIdentity, ffi, 0);
                    if (IsDatabasePath(value))
                        Inspect(LocalPath(value!, working), "App source path; SQLite header verified.");
                }
                foreach (var value in symbols.Values.Where(IsDatabasePath))
                    Inspect(LocalPath(value, working), "App source path; SQLite header verified.");
            }

            if (fileNames.Count > 0 && !budget.Expired)
            {
                // sqflite_common_ffi uses this project-scoped directory on Windows.
                // Do not enumerate the rest of .dart_tool or any AppData parent.
                if (ffi)
                    foreach (var name in fileNames)
                        Inspect(Path.Combine(working, ".dart_tool", "sqflite_common_ffi", "databases", name),
                            "sqflite app file; SQLite header verified.");
                foreach (var path in EnumerateBounded(working, 4, budget))
                {
                    if (budget.Expired) break;
                    if (fileNames.Contains(Path.GetFileName(path)))
                        Inspect(path, "Project file referenced by app source; SQLite header verified.");
                }
            }
        }
        catch (Exception ex) when (IsAccessError(ex) || ex is RegexMatchTimeoutException)
        {
            // A folder becoming unavailable must not erase already verified files.
        }
        return observations.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Checks file availability read-only, without creating a file, WAL, lock, or connection.</summary>
    public static bool IsAvailable(string path)
    {
        try
        {
            var local = LocalPath(path);
            if (local is null || !IsUnlinkedPath(local)) return false;
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, Header.Length, FileOptions.None);
            Span<byte> header = stackalloc byte[16];
            var length = 0;
            while (length < header.Length)
            {
                var read = stream.Read(header[length..]);
                if (read == 0) return false;
                length += read;
            }
            return header.SequenceEqual(Header);
        }
        catch (Exception ex) when (IsAccessError(ex)) { return false; }
    }

    private static string? ResolvePath(string expression, IReadOnlyDictionary<string, string> symbols,
        string working, WindowsIdentity identity, bool ffi, int depth)
    {
        if (depth > 8 || expression.Length > 2048) return null;
        expression = expression.Trim();
        if (expression.StartsWith("await ", StringComparison.Ordinal)) expression = expression[6..].TrimStart();
        var branch = WindowsBranch.Match(expression);
        if (branch.Success)
        {
            var branches = SplitArguments(expression[branch.Length..], ':');
            if (branches.Count != 2) return null;
            return ResolvePath(branches[0], symbols, working, identity, ffi, depth + 1);
        }
        var literal = Lex(expression);
        if (literal.Literals.Count == 1 && literal.Literals[0].Start == 0 &&
            literal.Literals[0].End == expression.Length) return literal.Literals[0].Value;
        var reference = SimpleReference.Match(expression);
        if (reference.Success)
            return symbols.GetValueOrDefault(reference.Groups["name"].Value);
        var environment = EnvironmentPath.Match(expression);
        if (environment.Success)
        {
            var remainder = expression[environment.Length..].TrimStart();
            if (remainder.Length > 0 && !remainder.StartsWith("??", StringComparison.Ordinal)) return null;
            return Environment.GetFolderPath(environment.Groups["name"].Value == "LOCALAPPDATA"
                ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData);
        }
        var call = PathCall.Match(expression);
        if (!call.Success) return null;
        var close = ClosingParenthesis(literal.Mask, call.Length - 1);
        if (close != expression.Length - 1) return null;
        var name = call.Groups["name"].Value;
        var arguments = SplitArguments(expression[call.Length..close], ',');
        if (name is "getApplicationSupportDirectory" or "getApplicationCacheDirectory")
            return arguments.Count == 0 ? identity.Path(name == "getApplicationCacheDirectory") : null;
        if (name == "getApplicationDocumentsDirectory")
            return arguments.Count == 0 ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : null;
        if (name == "getDatabasesPath")
            return ffi && arguments.Count == 0
                ? Path.Combine(working, ".dart_tool", "sqflite_common_ffi", "databases") : null;
        if (name is "Directory" or "File")
            return arguments.Count == 1 ? ResolvePath(arguments[0], symbols, working, identity, ffi, depth + 1) : null;
        if (arguments.Count is < 1 or > 8) return null;
        var parts = arguments.Select(argument => ResolvePath(argument, symbols, working, identity, ffi, depth + 1)).ToArray();
        if (parts.Any(part => string.IsNullOrWhiteSpace(part))) return null;
        return Path.Combine(parts.Select(part => part!).ToArray());
    }

    private static WindowsIdentity ReadWindowsIdentity(string working)
    {
        var text = ReadText(Path.Combine(working, "windows", "runner", "Runner.rc"), MaximumTextBytes);
        if (text is null) return new(null, null);
        text = Lex(text).Text;
        string? Value(string name)
        {
            var match = Regex.Match(text, "\\bVALUE\\s+\"" + name + "\"\\s*,\\s*\"(?<value>[^\"\\\\]{1,128})\"",
                RegexOptions.CultureInvariant, RegexTimeout);
            var value = match.Success ? match.Groups["value"].Value : null;
            return IsDirectoryComponent(value) ? value : null;
        }
        return new(Value("CompanyName"), Value("ProductName"));
    }

    private sealed record WindowsIdentity(string? Company, string? Product)
    {
        public string? Path(bool cache)
        {
            if (Product is null) return null;
            var root = Environment.GetFolderPath(cache ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root)) return null;
            return Company is null ? System.IO.Path.Combine(root, Product) : System.IO.Path.Combine(root, Company, Product);
        }
    }

    private static IEnumerable<string> EnumerateBounded(string root, int maximumDepth, WorkBudget budget)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var directories = 0;
        var entries = 0;
        while (pending.Count > 0 && directories++ < MaximumDirectories && !budget.Expired)
        {
            var (path, depth) = pending.Dequeue();
            if (!IsUnlinkedPath(path)) continue;
            IEnumerator<string>? enumerator = null;
            try { enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); }
            catch (Exception ex) when (IsAccessError(ex)) { }
            if (enumerator is null) continue;
            using (enumerator)
            {
                while (!budget.Expired)
                {
                    string child;
                    FileAttributes attributes;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        if (++entries > MaximumEntries) yield break;
                        child = enumerator.Current;
                        attributes = File.GetAttributes(child);
                    }
                    catch (Exception ex) when (IsAccessError(ex)) { break; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) == 0) yield return child;
                    else if (depth < maximumDepth && !SkippedNames.Contains(System.IO.Path.GetFileName(child)) &&
                             directories + pending.Count < MaximumDirectories)
                        pending.Enqueue((child, depth + 1));
                }
            }
        }
    }

    private static string? ReadText(string path, int limit)
    {
        try
        {
            if (limit <= 0 || !IsUnlinkedPath(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > limit) return null;
            var bytes = new byte[limit + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            return length > limit ? null : new UTF8Encoding(false, true).GetString(bytes, 0, length);
        }
        catch (Exception ex) when (IsAccessError(ex) || ex is DecoderFallbackException) { return null; }
    }

    private sealed record StringLiteral(int Start, int End, string? Value);
    private sealed record SourceText(string Text, string Mask, IReadOnlyList<StringLiteral> Literals);

    // Preserve offsets while masking comments/strings. This avoids treating path
    // examples in comments or SQL string contents as executable Dart expressions.
    private static SourceText Lex(string text)
    {
        var clean = text.ToCharArray();
        var mask = text.ToCharArray();
        var literals = new List<StringLiteral>();
        for (var index = 0; index < text.Length;)
        {
            var start = index;
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] is '/' or '*')
            {
                var line = text[index + 1] == '/';
                index += 2;
                var nesting = 1;
                while (index < text.Length)
                {
                    if (line && text[index] is '\r' or '\n') break;
                    if (!line && index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
                    { nesting++; index += 2; continue; }
                    if (!line && index + 1 < text.Length && text[index] == '*' && text[index + 1] == '/')
                    { index += 2; if (--nesting == 0) break; continue; }
                    index++;
                }
                Array.Fill(clean, ' ', start, index - start);
                Array.Fill(mask, ' ', start, index - start);
                continue;
            }
            var raw = text[index] == 'r' && index + 1 < text.Length && text[index + 1] is '\'' or '"' &&
                (index == 0 || !char.IsLetterOrDigit(text[index - 1]) && text[index - 1] != '_');
            var quoteIndex = raw ? index + 1 : index;
            if (text[quoteIndex] is not ('\'' or '"')) { index++; continue; }
            var quote = text[quoteIndex];
            var triple = quoteIndex + 2 < text.Length && text[quoteIndex + 1] == quote && text[quoteIndex + 2] == quote;
            index = quoteIndex + (triple ? 3 : 1);
            var value = new StringBuilder();
            var supported = !triple;
            var closed = false;
            while (index < text.Length)
            {
                var character = text[index++];
                if (character == quote && (!triple || index + 1 < text.Length && text[index] == quote && text[index + 1] == quote))
                { if (triple) index += 2; closed = true; break; }
                if (!raw && character == '\\' && index < text.Length)
                {
                    var escaped = text[index++];
                    if (escaped is '\\' or '\'' or '"' or '$') character = escaped;
                    else { supported = false; continue; }
                }
                if (!raw && character == '$' || char.IsControl(character)) supported = false;
                value.Append(character);
            }
            Array.Fill(mask, ' ', start, index - start);
            literals.Add(new(start, index, supported && closed ? value.ToString() : null));
        }
        return new(new string(clean), new string(mask), literals);
    }

    private static List<string> SplitArguments(string expression, char delimiter)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        var mask = Lex(expression).Mask;
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if (mask[index] is '(' or '[' or '{') depth++;
            else if (mask[index] is ')' or ']' or '}') depth--;
            else if (mask[index] == delimiter && depth == 0)
            { arguments.Add(expression[start..index].Trim()); start = index + 1; }
            if (depth < 0 || arguments.Count > 8) return [];
        }
        if (depth != 0) return [];
        var final = expression[start..].Trim();
        if (final.Length > 0) arguments.Add(final);
        return arguments;
    }

    private static int ClosingParenthesis(string mask, int open)
    {
        var depth = 0;
        for (var index = open; index < mask.Length && index - open <= 2048; index++)
        {
            if (mask[index] == '(') depth++;
            else if (mask[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static bool IsDatabasePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 1024 && !value.Any(char.IsControl) &&
        Path.GetExtension(value).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3";

    private static bool IsDirectoryComponent(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.EndsWith(' ') && !value.EndsWith('.');

    private static string? LocalPath(string path, string? root = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) ||
                path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;
            var full = root is null ? Path.GetFullPath(path) : Path.GetFullPath(path, root);
            if (full.StartsWith("\\\\", StringComparison.Ordinal) || Path.GetPathRoot(full)?.Length != 3) return null;
            return full;
        }
        catch (Exception ex) when (IsAccessError(ex)) { return null; }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsProjectTemplatePath(string working, string path) => IsWithin(working, path) &&
        Path.GetRelativePath(working, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).SkipLast(1).Any(TemplateNames.Contains);

    private static bool IsUnlinkedPath(string path)
    {
        try
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (IsAccessError(ex)) { return false; }
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, RegexTimeout);
    private static bool IsAccessError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;

    private sealed class WorkBudget(CancellationToken cancellationToken)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        public bool Expired => cancellationToken.IsCancellationRequested || _stopwatch.Elapsed >= MaximumWorkTime;
    }
}
