using System.IO;
using System.Security;
using System.Text;

namespace FullStackLauncher.Services;

public sealed record SourceLineCountResult(long Lines, int Files, DateTimeOffset CountedAtUtc);

/// <summary>Counts physical source lines without retaining source text or invoking external tools.</summary>
public static class SourceLineCounter
{
    public const int ScopeVersion = 2;

    private const int BufferSize = 16 * 1024;

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".fsi", ".razor", ".cshtml", ".vbhtml", ".xaml",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".vue", ".svelte", ".astro",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".styl",
        ".json", ".jsonc", ".json5", ".xml", ".yaml", ".yml", ".toml", ".ini", ".config",
        ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx", ".resx",
        ".sql", ".graphql", ".gql", ".proto", ".prisma", ".bicep", ".tf", ".tfvars",
        ".py", ".pyi", ".rb", ".php", ".phtml", ".go", ".rs", ".java", ".kt", ".kts",
        ".scala", ".sc", ".swift", ".dart", ".c", ".h", ".cc", ".hh", ".cpp", ".hpp",
        ".cxx", ".hxx", ".m", ".mm", ".lua", ".r", ".ex", ".exs", ".erl", ".hrl",
        ".clj", ".cljs", ".cljc", ".edn", ".groovy", ".gradle", ".cmake",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".cmd", ".bat",
        ".ejs", ".hbs", ".handlebars", ".pug", ".jade", ".twig", ".liquid", ".mustache",
        ".mdx", ".glsl", ".vert", ".frag", ".hlsl", ".wgsl", ".dockerfile"
    };

    private static readonly HashSet<string> SourceFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "CMakeLists.txt", "Jenkinsfile",
        "Gemfile", "Rakefile", "Procfile", ".editorconfig", ".eslintrc", ".prettierrc", ".babelrc",
        ".browserslistrc", ".stylelintrc"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".bzr", ".vs", ".idea", ".vscode", ".codex", ".agents",
        "node_modules", "bower_components", "jspm_packages", "vendor", ".nuget",
        "bin", "obj", "dist", "build", "publish", "artifacts", "out", "output", "target",
        "coverage", "TestResults", "test-results", "playwright-report", ".nyc_output",
        ".angular", ".next", ".nuxt", ".output", ".svelte-kit", ".astro", ".parcel-cache",
        ".cache", ".turbo", ".vite", ".yarn", ".pnpm-store", ".npm", ".gradle", ".dart_tool",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".venv", "venv",
        "env", "htmlcov", "site-packages", "generated", "__generated__", "logs", "tmp", "temp",
        "portable", "monitor-runtime", "api-runtime"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock",
        "bun.lockb", "composer.lock", "Gemfile.lock", "Cargo.lock", "Pipfile.lock", "poetry.lock",
        "uv.lock", "packages.lock.json", "project.assets.json", "project.lock.json"
    };

    private static readonly string[] ExcludedNameMarkers = [".min.", ".g.", ".g.i.", ".generated."];
    private static readonly string[] ExcludedNameSuffixes = [".AssemblyInfo.cs", ".AssemblyInfo.vb", ".AssemblyInfo.fs"];

    public static string ScopeDescription { get; } =
        "Physical lines, including blanks and comments, in recognized source, markup, style, script, and config files. " +
        "Includes untracked files, tests, migrations, and designer source; Git ignore rules are not applied. " +
        "Skips dependency/build/cache/VCS folders, named generated/minified files, package.json, lockfiles, binaries, and links/junctions. " +
        "Unreadable files or folders fail the count. Counts an unfinished final line once; empty files have zero lines. " +
        "Average lines per file divides total counted lines by counted files, including empty files.";

    public static SourceLineCountResult Count(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(directory);
        var pending = new Stack<string>();
        pending.Push(root);
        var buffer = new char[BufferSize];
        long totalLines = 0;
        var totalFiles = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = BufferSize
        };

        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var folderInfo = new DirectoryInfo(folder);
                if (!folderInfo.Attributes.HasFlag(FileAttributes.Directory))
                    throw new SourceReadException($"Source path '{folder}' is not a folder.");
                if (IsLink(folderInfo))
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(folder, root))
                        throw new SourceReadException($"Source folder '{folder}' is a symbolic link or junction. Select its actual folder.");
                    continue;
                }

                foreach (var entry in folderInfo.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!ExcludedDirectories.Contains(entry.Name) && !IsLink(entry))
                            pending.Push(entry.FullName);
                        continue;
                    }

                    if (!IsSourceFile(entry.Name) || IsLink(entry)) continue;
                    var lines = CountFile(entry.FullName, buffer, cancellationToken);
                    if (lines is null) continue;
                    totalLines = checked(totalLines + lines.Value);
                    totalFiles = checked(totalFiles + 1);
                }
            }
            catch (SourceReadException)
            {
                throw;
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                throw new SourceReadException($"Could not count source folder '{folder}'. {FailureDescription(exception)}", exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SourceLineCountResult(totalLines, totalFiles, DateTimeOffset.UtcNow);
    }

    private static bool IsLink(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;

    private static bool IsSourceFile(string name)
    {
        if (ExcludedFileNames.Contains(name) ||
            ExcludedNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            ExcludedNameSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock.yaml", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock.yml", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            return false;

        return SourceExtensions.Contains(Path.GetExtension(name)) || SourceFileNames.Contains(name) ||
            name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase);
    }

    private static long? CountFile(string path, char[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            var before = new FileInfo(path);
            var originalLength = before.Length;
            var originalWriteTime = before.LastWriteTimeUtc;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: BufferSize);
            long lines = 0;
            var previousWasCarriageReturn = false;
            var hasUnterminatedLine = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = reader.Read(buffer, 0, buffer.Length);
                if (length == 0) break;
                for (var index = 0; index < length; index++)
                {
                    var character = buffer[index];
                    if (character == '\0') return null;
                    if (character == '\r')
                    {
                        lines = checked(lines + 1);
                        hasUnterminatedLine = false;
                    }
                    else if (character == '\n')
                    {
                        if (!previousWasCarriageReturn) lines = checked(lines + 1);
                        hasUnterminatedLine = false;
                    }
                    else
                    {
                        hasUnterminatedLine = true;
                    }
                    previousWasCarriageReturn = character == '\r';
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var after = new FileInfo(path);
            if (after.Length != originalLength || after.LastWriteTimeUtc != originalWriteTime)
                throw new SourceReadException($"Source file '{path}' changed while counting. Recount when the save finishes.");
            return checked(lines + (hasUnterminatedLine ? 1 : 0));
        }
        catch (SourceReadException)
        {
            throw;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new SourceReadException($"Could not count source file '{path}'. {FailureDescription(exception)}", exception);
        }
        finally
        {
            // Do not retain source text in the reusable buffer after a file succeeds, fails, or is skipped.
            Array.Clear(buffer);
        }
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static string FailureDescription(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => "Access was denied. Check folder and file permissions, then recount.",
        FileNotFoundException or DirectoryNotFoundException => "The path no longer exists. Check the service folder, then recount.",
        PathTooLongException => "The path is too long to read.",
        _ => "The path could not be read completely. Check that it is available, then recount."
    };

    private sealed class SourceReadException(string message, Exception? innerException = null) : IOException(message, innerException)
    {
    }
}
