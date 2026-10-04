using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FullStackLauncher.Services;

public sealed record ServiceCommandSuggestion(string Type, string Name, string Command,
    string? Url, string? UiPath, string Evidence, bool IsDetected, bool IsApi);

public sealed record ServiceCommandDetection(IReadOnlyList<ServiceCommandSuggestion> Suggestions,
    IReadOnlyList<string> Notes, bool IsComplete = true);

/// <summary>Reads bounded direct project files to propose commands for explicit review. Never executes project code or tools.</summary>
public static class ServiceCommandDiscovery
{
    private const int MaximumEntries = 256;
    private const int MaximumFilesRead = 24;
    private const int MaximumFileBytes = 128 * 1024;
    private const int MaximumTotalBytes = 512 * 1024;
    private const int MaximumSuggestions = 32;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly string[] ComposeNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    public static async Task<ServiceCommandDetection> DetectAsync(string workingDirectory, bool isApi,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await Task.Run(() => DetectCore(workingDirectory, isApi, deadline.Token), deadline.Token)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    public static ServiceCommandSuggestion CreateTemplate(string type, bool isApi, string workingDirectory)
    {
        type = string.IsNullOrWhiteSpace(type) ? isApi ? "Custom API" : "Console" : type.Trim();
        var name = FolderName(workingDirectory);
        var normalized = type.ToLowerInvariant();
        var command = normalized switch
        {
            ".net" or ".net console" or ".net worker" or ".net app" => isApi
                ? "dotnet run --no-launch-profile -- --urls http://127.0.0.1:5000" : "dotnet run",
            "fastapi" => "python -m uvicorn main:app --reload --host 127.0.0.1 --port 8000",
            "flask" => "python -m flask --app app run --host 127.0.0.1 --port 5000",
            "django" => "python manage.py runserver 127.0.0.1:8000",
            "python" => "python main.py",
            "node.js" or "node" or "express" => "npm start",
            "nestjs" => "npm run start:dev",
            "spring boot" => "mvn spring-boot:run",
            "go" => "go run .",
            "dart" => "dart run",
            "flutter" => "flutter run",
            "docker" => DockerTemplate(workingDirectory),
            "docker compose" => "docker compose up",
            _ => ""
        };
        var port = normalized switch
        {
            ".net" or ".net console" or ".net worker" or ".net app" or "flask" => 5000,
            "spring boot" => 8080,
            "express" or "node.js" or "node" or "nestjs" => 3000,
            _ => 8000
        };
        var docker = normalized is "docker" or "docker compose";
        var evidence = docker
            ? "Starter only. Review Docker files, build context, and container lifecycle; add HTTP port mapping if needed."
            : "Starter only. Review the entry point, dependencies, and command arguments.";
        return new(type, name, command, isApi ? docker ? "" : $"http://127.0.0.1:{port}" : null,
            isApi ? normalized == "fastapi" ? "/docs" : "/" : null, evidence, false, isApi);
    }

    private static ServiceCommandDetection DetectCore(string workingDirectory, bool isApi, CancellationToken token)
    {
        var suggestions = new List<ServiceCommandSuggestion>();
        var notes = new List<string>();
        try
        {
            var root = Path.GetFullPath(workingDirectory);
            if (!Directory.Exists(root) || IsLinked(root))
                return new([], ["The selected folder is unavailable or linked. Choose a type and enter its command manually."], false);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var entries = 0;
            var knownProjectFiles = 0;
            var linkedGoSource = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries)
                {
                    notes.Add("Folder scan reached its limit. Some direct files were not inspected.");
                    return new([], notes.ToArray(), false);
                }
                var attributes = File.GetAttributes(path);
                var name = Path.GetFileName(path);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) knownProjectFiles++;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && name.EndsWith(".go", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)) linkedGoSource = true;
                }
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                files.TryAdd(name, path);
            }
            var reader = new BoundedReader(notes, token);
            DetectDotnet(files, reader, isApi, suggestions, notes, knownProjectFiles);
            DetectDocker(root, files, reader, isApi, suggestions, notes);
            DetectPython(files, reader, isApi, suggestions, notes);
            DetectNode(files, reader, isApi, suggestions);
            if (isApi) DetectGoAndSpring(root, files, reader, suggestions, notes, linkedGoSource);
            if (!isApi) DetectDart(files, reader, suggestions);
            if (suggestions.Count >= MaximumSuggestions)
            {
                notes.Add("Suggestion limit reached. Review the selected folder or enter a command manually.");
            }
            if (suggestions.Count == 0)
                notes.Add("No unambiguous runnable entry point detected. Choose a type and review its starter command.");
            else if (suggestions.Count > 1)
                notes.Add("Several entry points detected. Choose the command to add; none has been started.");
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            notes.Add("Folder detection could not finish. Choose a type and review its starter command.");
            return new([], notes.Distinct().ToArray(), false);
        }
        return new(suggestions.ToArray(), notes.Distinct().ToArray());
    }

    private static void DetectDotnet(Dictionary<string, string> files, BoundedReader reader, bool isApi,
        List<ServiceCommandSuggestion> suggestions, List<string> notes, int knownProjectFiles)
    {
        var projects = files.Where(file => file.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var project in projects)
        {
            reader.CheckCancellation();
            if (suggestions.Count >= MaximumSuggestions) return;
            if (!TryQuoteOperand(project.Key, out var projectArgument))
            {
                notes.Add($"'{project.Key}' needs a manually reviewed command because its filename contains shell syntax.");
                continue;
            }
            var source = reader.Read(project.Value);
            if (source is null) continue;
            try
            {
                using var xml = XmlReader.Create(new StringReader(source), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
                var document = XDocument.Load(xml);
                var root = document.Root;
                if (root?.Name.LocalName != "Project") continue;
                var sdkElements = root.Elements().Where(element => element.Name.LocalName == "Sdk").ToArray();
                var sdkValues = (root.Attribute("Sdk")?.Value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Concat(sdkElements.Select(element => element.Attribute("Name")?.Value ?? "")).ToArray();
                if (sdkValues.Any(value => value.Contains('$')) || sdkElements.Any(HasCondition) ||
                    root.Descendants().Any(element => element.Name.LocalName == "Import"))
                {
                    notes.Add($"'{project.Key}' uses imported or computed project settings; select its command manually.");
                    continue;
                }
                var properties = root.Elements().Where(element => element.Name.LocalName == "PropertyGroup")
                    .SelectMany(group => group.Elements()).Where(element => element.Name.LocalName is
                    "OutputType" or "TargetFramework" or "TargetFrameworks" or "UseWPF" or "UseWindowsForms" or "UseMaui" or "IsTestProject").ToArray();
                if (properties.Any(element => HasCondition(element) || element.Value.Contains("$(", StringComparison.Ordinal)))
                {
                    notes.Add($"'{project.Key}' uses conditional or computed launch properties; select its command manually.");
                    continue;
                }
                if (properties.Any(element => element.Name.LocalName is "UseWPF" or "UseWindowsForms" or "UseMaui" &&
                    element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))) continue;
                var packageNames = root.Elements().Where(element => element.Name.LocalName == "ItemGroup")
                    .SelectMany(group => group.Elements()).Where(element => element.Name.LocalName == "PackageReference")
                    .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? "");
                if (properties.Any(element => element.Name.LocalName == "IsTestProject" && element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                    packageNames.Any(value => value.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("NUnit", StringComparison.OrdinalIgnoreCase) || value.Equals("MSTest.TestFramework", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("xunit", StringComparison.OrdinalIgnoreCase) || value.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase))) continue;
                var sdks = sdkValues.Select(value => value.Split('/')[0]).ToArray();
                if (!sdks.Any(value => value.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || value.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var web = sdks.Any(value => value.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase));
                var outputs = properties.Where(element => element.Name.LocalName == "OutputType")
                    .Select(element => element.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var executable = outputs.Length == 1 && (outputs[0].Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                    outputs[0].Equals("WinExe", StringComparison.OrdinalIgnoreCase));
                if (web != isApi || !web && !executable || web && outputs.Length > 0 && !executable) continue;
                var frameworkProperties = properties.Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToArray();
                var frameworks = frameworkProperties.SelectMany(element => element.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumSuggestions + 1).ToArray();
                if (frameworks.Any(value => !IsIdentifierOperand(value)) || frameworkProperties.Length > 1)
                {
                    notes.Add($"'{project.Key}' has computed or conflicting target frameworks; select its command manually.");
                    continue;
                }
                var type = web ? ".NET" : outputs[0].Equals("WinExe", StringComparison.OrdinalIgnoreCase) ? ".NET App"
                    : sdks.Any(value => value.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
                    ? ".NET Worker" : ".NET Console";
                foreach (var framework in frameworks.Length > 1 ? frameworks : new[] { "" })
                {
                    reader.CheckCancellation();
                    if (suggestions.Count >= MaximumSuggestions) return;
                    var command = "dotnet run" + (knownProjectFiles > 1 ? $" --project {projectArgument}" : "") +
                        (framework.Length > 0 ? $" --framework \"{framework}\"" : "");
                    if (web) command += " --no-launch-profile -- --urls http://127.0.0.1:5000";
                    suggestions.Add(new(type, Path.GetFileNameWithoutExtension(project.Key), command,
                        web ? "http://127.0.0.1:5000" : null, web ? "/" : null,
                        $"{project.Key}: literal {(web ? "Web SDK" : $"OutputType {outputs[0]}")}" +
                        (framework.Length > 0 ? $"; framework {framework}" : ""), true, web));
                }
            }
            catch (XmlException) { notes.Add($"'{project.Key}' could not be read as a literal project file."); }
        }
    }

    private static void DetectDocker(string root, Dictionary<string, string> files, BoundedReader reader,
        bool isApi, List<ServiceCommandSuggestion> suggestions, List<string> notes)
    {
        var compose = ComposeNames.Where(files.ContainsKey).ToArray();
        if (compose.Length > 0)
        {
            if (isApi)
            {
                notes.Add("Compose files detected. HTTP ports depend on Compose configuration; choose Docker manually and review its command and URL.");
                return;
            }
            foreach (var main in compose)
            {
                reader.CheckCancellation();
                if (suggestions.Count >= MaximumSuggestions) return;
                var prefix = main.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) ? "docker-compose" : "compose";
                var overrides = new[] { prefix + ".override.yaml", prefix + ".override.yml" }.Where(files.ContainsKey).ToArray();
                foreach (var secondary in overrides.Length > 0 ? overrides : new[] { "" })
                {
                    if (suggestions.Count >= MaximumSuggestions) return;
                    var command = $"docker compose -f \"{main}\"" +
                        (secondary.Length > 0 ? $" -f \"{secondary}\"" : "") + " up";
                    suggestions.Add(new("Docker Compose", FolderName(root), command, null, null,
                        secondary.Length > 0 ? $"{main} and {secondary}; foreground Compose command" : $"{main}; foreground Compose command", true, false));
                }
            }
            return;
        }
        foreach (var file in files.Where(file => !SensitiveFilename(file.Key) &&
                     (file.Key.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || file.Key.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase))
        {
            reader.CheckCancellation();
            if (suggestions.Count >= MaximumSuggestions) return;
            if (!TryQuoteOperand(file.Key, out var argument))
            {
                notes.Add($"'{file.Key}' needs a manually reviewed Docker command.");
                continue;
            }
            int? port = null;
            if (isApi)
            {
                var source = reader.Read(file.Value);
                if (source is null) continue;
                var exposes = Matches(source, @"(?im)^\s*EXPOSE[ \t]+([^\r\n#]+)").ToArray();
                if (exposes.Length == 1 && Match(exposes[0].Groups[1].Value.Trim(), @"^(\d{1,5})(?:/tcp)?$", RegexOptions.IgnoreCase) is { Success: true } match &&
                    int.TryParse(match.Groups[1].Value, out var value) && value is >= 1 and <= 65535) port = value;
                if (port is null)
                {
                    notes.Add($"'{file.Key}' has no single literal TCP EXPOSE port. Choose Docker manually and review its command and URL.");
                    continue;
                }
            }
            var tag = DockerTag(root, file.Key);
            var command = $"docker build -f {argument} -t {tag} . && docker run --rm " +
                (port is { } number ? $"-p 127.0.0.1:{number}:{number} " : "") + tag;
            suggestions.Add(new("Docker", FolderName(root), command, port is { } httpPort ? $"http://127.0.0.1:{httpPort}" : null,
                isApi ? "/" : null, $"{file.Key}; review build context '.'" +
                (port is { } tcpPort ? $"; EXPOSE {tcpPort} declares TCP only, HTTP URL is an editable assumption" : ""), true, isApi));
        }
    }

    private static void DetectPython(Dictionary<string, string> files, BoundedReader reader, bool isApi,
        List<ServiceCommandSuggestion> suggestions, List<string> notes)
    {
        foreach (var file in files.Where(file => file.Key.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && !SensitiveFilename(file.Key))
                     .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase))
        {
            reader.CheckCancellation();
            if (suggestions.Count >= MaximumSuggestions) return;
            var text = reader.Read(file.Value);
            if (text is null) continue;
            var source = MaskPythonStringsAndComments(text);
            if (isApi && file.Key.Equals("manage.py", StringComparison.OrdinalIgnoreCase) && source.Contains("django.core.management", StringComparison.Ordinal))
            {
                suggestions.Add(new("Django", "Django API", "python manage.py runserver 127.0.0.1:8000",
                    "http://127.0.0.1:8000", "/", "manage.py: Django management import", true, true));
                continue;
            }
            var module = Path.GetFileNameWithoutExtension(file.Key);
            var web = false;
            foreach (var (package, factory, type, port) in new[] { ("fastapi", "FastAPI", "FastAPI", 8000), ("flask", "Flask", "Flask", 5000) })
            {
                var import = Match(source, $@"(?m)^from[ \t]+{package}[ \t]+import[ \t]+{factory}(?:[ \t]+as[ \t]+(?<alias>[A-Za-z_]\w*))?\b");
                var qualified = Match(source, $@"(?m)^import[ \t]+{package}(?:[ \t]+as[ \t]+(?<alias>[A-Za-z_]\w*))?\b");
                if (!import.Success && !qualified.Success) continue;
                web = true;
                if (!isApi) continue;
                if (!Match(module, @"^[A-Za-z_][A-Za-z0-9_]*$").Success)
                {
                    notes.Add($"'{file.Key}' needs a manually reviewed Python import module.");
                    continue;
                }
                var expression = import.Success ? import.Groups["alias"].Success ? import.Groups["alias"].Value : factory
                    : (qualified.Groups["alias"].Success ? qualified.Groups["alias"].Value : package) + "." + factory;
                var apps = Matches(source, $@"(?m)^(?<app>[A-Za-z_][A-Za-z0-9_]*)(?:[ \t]*:[^=\r\n]+)?[ \t]*=[ \t]*{Regex.Escape(expression)}[ \t]*\(")
                    .Take(MaximumSuggestions + 1).ToArray();
                if (apps.Length == 0) notes.Add($"'{file.Key}' imports {type} but has no literal top-level application variable. Review its entry point manually.");
                foreach (var app in apps)
                {
                    reader.CheckCancellation();
                    if (suggestions.Count >= MaximumSuggestions) return;
                    var target = module + ":" + app.Groups["app"].Value;
                    var command = type == "FastAPI" ? $"python -m uvicorn \"{target}\" --reload --host 127.0.0.1 --port {port}"
                        : $"python -m flask --app \"{target}\" run --host 127.0.0.1 --port {port}";
                    suggestions.Add(new(type, module + " API", command, $"http://127.0.0.1:{port}", type == "FastAPI" ? "/docs" : "/",
                        $"{file.Key}: literal {type} application '{app.Groups["app"].Value}'", true, true));
                }
            }
            if (!isApi && !web && (file.Key.Equals("main.py", StringComparison.OrdinalIgnoreCase) || file.Key.Equals("app.py", StringComparison.OrdinalIgnoreCase)) &&
                TryQuoteOperand(file.Key, out var argument))
                suggestions.Add(new("Python", module, $"python {argument}", null, null, $"{file.Key}; review script entry point", true, false));
        }
    }

    private static void DetectNode(Dictionary<string, string> files, BoundedReader reader, bool isApi,
        List<ServiceCommandSuggestion> suggestions)
    {
        if (suggestions.Count >= MaximumSuggestions) return;
        if (!files.TryGetValue("package.json", out var path) || reader.Read(path) is not { } source) return;
        try
        {
            using var json = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                reader.Note("package.json must contain a JSON object. Choose its command manually.");
                return;
            }
            if (!json.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return;
            var type = "Node.js";
            foreach (var key in new[] { "dependencies", "devDependencies" })
                if (json.RootElement.TryGetProperty(key, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                {
                    if (dependencies.TryGetProperty("@nestjs/core", out _)) type = "NestJS";
                    else if (type != "NestJS" && dependencies.TryGetProperty("express", out _)) type = "Express";
                }
            foreach (var script in new[] { "dev", "start", "start:dev" })
                if (suggestions.Count < MaximumSuggestions && scripts.TryGetProperty(script, out var command) &&
                    command.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(command.GetString()))
                    suggestions.Add(new(type, type + (isApi ? " API" : " app"), script == "start" ? "npm start" : $"npm run {script}",
                        isApi ? "http://127.0.0.1:3000" : null, isApi ? "/" : null,
                        $"package.json: '{script}' script" + (isApi ? "; review HTTP port 3000" : ""), true, isApi));
        }
        catch (JsonException) { reader.Note("package.json could not be read. Choose its command manually."); }
    }

    private static void DetectGoAndSpring(string root, Dictionary<string, string> files, BoundedReader reader,
        List<ServiceCommandSuggestion> suggestions, List<string> notes, bool linkedGoSource)
    {
        if (suggestions.Count >= MaximumSuggestions) return;
        if (files.ContainsKey("go.mod") && linkedGoSource)
            notes.Add("Go sources include a linked file that was not inspected. Review the entry point manually.");
        if (files.ContainsKey("go.mod") && !linkedGoSource)
        {
            var mains = new List<string>();
            var uncertain = false;
            foreach (var file in files.Where(file => file.Key.EndsWith(".go", StringComparison.OrdinalIgnoreCase) &&
                         !file.Key.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)))
            {
                reader.CheckCancellation();
                if (SensitiveFilename(file.Key) || Match(file.Key,
                    @"_(?:aix|android|darwin|dragonfly|freebsd|hurd|illumos|ios|js|linux|nacl|netbsd|openbsd|plan9|solaris|wasip1|windows|zos|386|amd64|amd64p32|arm|arm64|loong64|mips|mipsle|mips64|mips64le|ppc64|ppc64le|riscv64|s390x|sparc64|wasm)(?:_[A-Za-z0-9]+)?\.go$",
                    RegexOptions.IgnoreCase).Success)
                {
                    uncertain = true;
                    continue;
                }
                var text = reader.Read(file.Value);
                if (text is null) { uncertain = true; continue; }
                if (Match(text, @"(?m)^//(?:go:build|[ \t]*\+build)\b").Success) uncertain = true;
                var source = WithoutSlashComments(MaskPythonStringsAndComments(text, includeBackticks: true));
                var package = Match(source, @"(?m)^package[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\r?$");
                if (!package.Success || package.Groups["name"].Value != "main") uncertain = true;
                var functions = Matches(source, @"(?m)^func[ \t]+main[ \t]*\([ \t]*\)").Take(2).Count();
                if (functions > 1) uncertain = true;
                if (functions == 1) mains.Add(file.Key);
            }
            if (uncertain || mains.Count > 1)
                notes.Add("Go entry point is unclear because files are unavailable, platform-specific, conditional, or conflicting. Review its command manually.");
            else if (mains.Count == 1)
                suggestions.Add(new("Go", FolderName(root) + " API", "go run .", "http://127.0.0.1:8000", "/",
                    $"go.mod and {mains[0]}: package main with main function; HTTP URL is a starter assumption", true, true));
        }
        if (suggestions.Count >= MaximumSuggestions) return;
        if (files.TryGetValue("pom.xml", out var pomPath) && reader.Read(pomPath) is { } pomSource)
        {
            try
            {
                using var xml = XmlReader.Create(new StringReader(pomSource), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
                var document = XDocument.Load(xml);
                var plugins = document.Root?.Elements().Where(element => element.Name.LocalName == "build")
                    .SelectMany(build => build.Elements().Where(element => element.Name.LocalName == "plugins"))
                    .SelectMany(group => group.Elements().Where(element => element.Name.LocalName == "plugin"));
                if (plugins?.Any(plugin => plugin.Elements().Any(element => element.Name.LocalName == "groupId" &&
                        element.Value.Trim() == "org.springframework.boot") && plugin.Elements().Any(element =>
                        element.Name.LocalName == "artifactId" && element.Value.Trim() == "spring-boot-maven-plugin")) == true)
                    suggestions.Add(new("Spring Boot", FolderName(root) + " API",
                        files.ContainsKey("mvnw.cmd") ? "call mvnw.cmd spring-boot:run" : "mvn spring-boot:run",
                        "http://127.0.0.1:8080", "/", "pom.xml: literal Spring Boot plugin; HTTP URL is a starter assumption", true, true));
            }
            catch (XmlException) { notes.Add("pom.xml could not be read as a literal project file."); }
        }
        foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
        {
            reader.CheckCancellation();
            if (suggestions.Count >= MaximumSuggestions) return;
            if (!files.TryGetValue(name, out var path) || reader.Read(path) is not { } text) continue;
            var source = WithoutSlashComments(text);
            if (!Match(source, @"(?m)^[ \t]*id(?:[ \t]*\([ \t]*|[ \t]+)['""]org\.springframework\.boot['""]").Success) continue;
            suggestions.Add(new("Spring Boot", FolderName(root) + " API",
                files.ContainsKey("gradlew.bat") ? "call gradlew.bat bootRun" : "gradle bootRun", "http://127.0.0.1:8080", "/",
                $"{name}: literal Spring Boot plugin; HTTP URL is a starter assumption", true, true));
        }
    }

    private static string WithoutSlashComments(string source) => Regex.Replace(
        Regex.Replace(source, @"/\*[\s\S]*?\*/", "", RegexOptions.NonBacktracking, MatchTimeout),
        @"(?m)//[^\r\n]*", "", RegexOptions.NonBacktracking, MatchTimeout);

    private static void DetectDart(Dictionary<string, string> files, BoundedReader reader, List<ServiceCommandSuggestion> suggestions)
    {
        if (suggestions.Count >= MaximumSuggestions) return;
        if (files.TryGetValue("pubspec.yaml", out var path) && reader.Read(path) is { } source)
        {
            source = Regex.Replace(source, @"(?m)#.*$", "", RegexOptions.NonBacktracking, MatchTimeout);
            var flutter = Match(source, @"(?m)^[ \t]+sdk:[ \t]*flutter[ \t]*$").Success;
            suggestions.Add(new(flutter ? "Flutter" : "Dart", flutter ? "Flutter app" : "Dart app", flutter ? "flutter run" : "dart run",
                null, null, flutter ? "pubspec.yaml: Flutter SDK; choose a device argument if needed" : "pubspec.yaml; review package entry point", true, false));
        }
        else if (files.ContainsKey("main.dart"))
            suggestions.Add(new("Dart", "Dart app", "dart run \"main.dart\"", null, null, "main.dart", true, false));
    }

    private static bool HasCondition(XElement element) => element.AncestorsAndSelf().Any(parent => parent.Attribute("Condition") is not null);
    private static bool IsLinked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool SensitiveFilename(string name) => name.StartsWith('.') || new[] { "secret", "credential", "settings", "config" }
        .Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static bool IsIdentifierOperand(string value) => Match(value, @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$").Success;
    private static Match Match(string text, string pattern, RegexOptions options = RegexOptions.None) =>
        Regex.Match(text, pattern, options | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
    private static IEnumerable<Match> Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout).Cast<Match>();
    private static bool TryQuoteOperand(string value, out string quoted)
    {
        quoted = "";
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['&', '|', '%', '!', '^', '<', '>', '"', '\r', '\n', '\0']) >= 0) return false;
        quoted = '"' + value + '"';
        return true;
    }

    private static string FolderName(string path)
    {
        try { return Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is { Length: > 0 } name ? name : "Service"; }
        catch (Exception ex) when (IsReadError(ex)) { return "Service"; }
    }

    private static string DockerTag(string root, string dockerfile = "Dockerfile")
    {
        var slug = Regex.Replace(FolderName(root).ToLowerInvariant(), "[^a-z0-9]+", "-", RegexOptions.NonBacktracking, MatchTimeout).Trim('-');
        if (slug.Length == 0) slug = "app";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant() + "\0" + dockerfile.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
        return "launcher-" + slug + "-" + hash;
    }

    private static string DockerTemplate(string root)
    {
        var tag = DockerTag(root);
        return $"docker build -f \"Dockerfile\" -t {tag} . && docker run --rm {tag}";
    }

    private static string MaskPythonStringsAndComments(string text, bool includeBackticks = false)
    {
        var masked = text.ToCharArray();
        for (var i = 0; i < masked.Length; i++)
        {
            if (text[i] == '#')
            {
                while (i < text.Length && text[i] is not ('\r' or '\n')) masked[i++] = ' ';
                i--;
            }
            else if (text[i] is '\'' or '"' || includeBackticks && text[i] == '`')
            {
                var quote = text[i];
                var count = i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote ? 3 : 1;
                for (var opening = 0; opening < count; opening++) masked[i++] = ' ';
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        masked[i++] = ' ';
                        if (i < text.Length && text[i] is not ('\r' or '\n')) masked[i++] = ' ';
                        continue;
                    }
                    if (text[i] == quote && (count == 1 || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                    {
                        for (var closing = 0; closing < count; closing++) masked[i++] = ' ';
                        break;
                    }
                    if (text[i] is not ('\r' or '\n')) masked[i] = ' ';
                    i++;
                }
                i--;
            }
        }
        return new string(masked);
    }

    private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or
        ArgumentException or NotSupportedException or RegexMatchTimeoutException;

    private sealed class BoundedReader(List<string> notes, CancellationToken token)
    {
        private int _files;
        private int _bytes;
        public void CheckCancellation() => token.ThrowIfCancellationRequested();
        public void Note(string text) => notes.Add(text);
        public string? Read(string path)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (IsLinked(path)) return null;
                if (++_files > MaximumFilesRead || _bytes >= MaximumTotalBytes)
                {
                    Note("File inspection reached its limit. Some entry points were not inspected.");
                    return null;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var limit = Math.Min(MaximumFileBytes, MaximumTotalBytes - _bytes);
                if (stream.Length > limit)
                {
                    Note($"'{Path.GetFileName(path)}' exceeds the detection read limit; review its command manually.");
                    return null;
                }
                var buffer = new byte[limit];
                var read = 0;
                while (read < buffer.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var count = stream.Read(buffer, read, buffer.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                _bytes += read;
                if (stream.Length > limit)
                {
                    Note($"'{Path.GetFileName(path)}' changed or exceeds the detection read limit.");
                    return null;
                }
                using var text = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, true);
                return text.ReadToEnd();
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                Note($"'{Path.GetFileName(path)}' could not be read. Review its command manually.");
                return null;
            }
        }
    }
}
