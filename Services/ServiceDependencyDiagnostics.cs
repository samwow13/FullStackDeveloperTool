using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

/// <summary>
/// Recognizes dependency failures in one command's already-redacted output. The owning
/// output capture must serialize observation and reads; discard this object for each new command.
/// Guidance is evidence of a recognized failure, not proof that a dependency is absent from disk.
/// </summary>
internal sealed class ServiceDependencyDiagnostics
{
    private const int MaximumLineLength = 2048;
    private const int ContextLines = 16;
    private const string VersionPattern = @"[0-9]{1,3}(?:\.[0-9]{1,5}){1,3}(?:-(?:preview|rc|alpha|beta)(?:\.[0-9]{1,8}){0,4})?";
    private const string ToolPattern = @"dotnet|node|npm|npx|ng|python|python3|py|java|docker|git";
    private static readonly Regex WarningLine = Pattern(@"^\s*(?:npm\s+warn\b|warning\b|warn\b)|:\s*warning\s+");
    private static readonly Regex MissingWindowsCommand = Pattern(
        @"(?:^|\s|:)['""’]?(?<tool>" + ToolPattern + @")(?:\.exe|\.cmd)?['""’]?\s+is not recognized as (?:an internal or external command|the name of a cmdlet)");
    private static readonly Regex MissingShellCommand = Pattern(
        @"(?:^|:\s+)['""’]?(?<tool>" + ToolPattern + @")(?:\.exe|\.cmd)?['""’]?:\s*(?:command )?not found\b");
    private static readonly Regex MissingEnvironmentCommand = Pattern(
        @"^\s*(?:/usr/bin/)?env:\s*['""‘’]?(?<tool>" + ToolPattern + @")['""‘’]?:\s*No such file or directory\b");
    private static readonly Regex RuntimeFramework = Pattern(
        @"\b(?:The framework|Framework:)\s*['""](?<framework>Microsoft\.(?:NETCore|AspNetCore|WindowsDesktop)\.App)['""],?\s*version\s*['""](?<version>" + VersionPattern + @")['""](?:\s*\((?<arch>x64|x86|arm64|arm)\))?");
    private static readonly Regex RuntimeArchitecture = Pattern(@"^\s*Architecture:\s*(?<arch>x64|x86|arm64|arm)\s*$");
    private static readonly Regex RequestedSdk = Pattern(
        @"^\s*Requested SDK version:\s*(?<version>" + VersionPattern + @")\s*$|\bglobal\.json version\s*\[(?<version>" + VersionPattern + @")\]");
    private static readonly Regex UnsupportedTarget = Pattern(
        @"\bNETSDK1045:\s*The current \.NET SDK does not support targeting (?<framework>\.NET(?: Core)?)\s+(?<version>[0-9]{1,3}\.[0-9]{1,3})\.");
    private static readonly Regex NodeDetected = Pattern(@"^\s*Node\.js version v?(?<version>" + VersionPattern + @") detected\.");
    private static readonly Regex AngularMinimum = Pattern(@"\bThe Angular CLI requires a minimum Node\.js version of\s+(?<versions>.{1,120})");
    private static readonly Regex AngularVersions = Pattern(@"v(?<version>[0-9]{1,3}\.[0-9]{1,5}(?:\.[0-9]{1,5})?)\b");
    private static readonly Regex NpmErrorPrefix = Pattern(@"^\s*npm\s+(?:ERR!|error)\s+");
    private static readonly Regex EngineRequirement = Pattern(@"['""](?<engine>node|npm)['""]\s*:\s*['""](?<version>[^'""]{1,120})['""]");
    private static readonly Regex SafeVersionRange = Pattern(@"^[v0-9x.*<>=~^|+\- \t]{1,120}$");
    private static readonly Regex ExactVersion = Pattern(@"^v?(?<version>" + VersionPattern + @")$");
    private static readonly Regex MissingAngularPackage = Pattern(
        @"\bCannot find (?:module|package)\s*['""](?:@angular/(?:cli|core|compiler-cli|build)|@angular-devkit/(?:build-angular|core|architect))(?:/[^'""\r\n]{0,160})?['""]");
    private static readonly Regex MissingAngularBuilder = Pattern(
        @"\bCould not find the ['""](?:@angular-devkit/build-angular|@angular/build):[a-z-]{1,40}['""] builder's node package\.");

    private DiagnosticKind _kind;
    private int _runtimeContext;
    private int _sdkContext;
    private int _engineContext;
    private int _nodeContext;
    private string? _runtimeName;
    private string? _runtimeVersion;
    private string? _runtimeArchitecture;
    private string? _sdkVersion;
    private string? _detectedNodeVersion;
    private string? _requiredNodeRange;
    private string? _requiredNpmRange;
    private string? _actualNodeVersion;
    private string? _actualNpmVersion;

    public string? Guidance { get; private set; }

    public void Observe(string redactedLine)
    {
        // Bound both parsing cost and how far unrelated output can supply version details.
        _runtimeContext = Math.Max(0, _runtimeContext - 1);
        _sdkContext = Math.Max(0, _sdkContext - 1);
        _engineContext = Math.Max(0, _engineContext - 1);
        _nodeContext = Math.Max(0, _nodeContext - 1);
        if (string.IsNullOrWhiteSpace(redactedLine)) return;
        var line = redactedLine.Length > MaximumLineLength ? redactedLine[..MaximumLineLength] : redactedLine;
        try
        {
            ObserveCore(line.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            // Unrecognized or adversarial output must never interrupt command capture.
        }
    }

    /// <summary>Call only when process creation reports that this exact executable was not found.</summary>
    public void ObserveMissingExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > MaximumLineLength) return;
        var file = executable[(Math.Max(executable.LastIndexOf('/'), executable.LastIndexOf('\\')) + 1)..];
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            file = file[..^4];
        ObserveMissingTool(file);
    }

    private void ObserveCore(string line)
    {
        if (WarningLine.IsMatch(line)) return;

        var command = MissingWindowsCommand.Match(line);
        if (!command.Success) command = MissingShellCommand.Match(line);
        if (!command.Success) command = MissingEnvironmentCommand.Match(line);
        if (command.Success)
        {
            ObserveMissingTool(command.Groups["tool"].Value);
            return;
        }

        var framework = RuntimeFramework.Match(line);
        var runtimeFailure = Contains(line, "You must install or update .NET to run this application") ||
            Contains(line, "You must install .NET to run this application") ||
            Contains(line, "It was not possible to find any compatible framework version") ||
            (framework.Success && Contains(line, "was not found"));
        if (runtimeFailure && Select(DiagnosticKind.Runtime))
        {
            _runtimeContext = ContextLines;
            UpdateRuntimeGuidance();
        }
        if (_kind == DiagnosticKind.Runtime && _runtimeContext > 0)
        {
            if (framework.Success)
            {
                _runtimeName = framework.Groups["framework"].Value.ToLowerInvariant() switch
                {
                    "microsoft.aspnetcore.app" => "ASP.NET Core Runtime",
                    "microsoft.windowsdesktop.app" => ".NET Desktop Runtime",
                    _ => ".NET Runtime"
                };
                _runtimeVersion = framework.Groups["version"].Value;
                if (framework.Groups["arch"].Success) _runtimeArchitecture = framework.Groups["arch"].Value.ToLowerInvariant();
            }
            var architecture = RuntimeArchitecture.Match(line);
            if (architecture.Success) _runtimeArchitecture = architecture.Groups["arch"].Value.ToLowerInvariant();
            UpdateRuntimeGuidance();
        }

        var target = UnsupportedTarget.Match(line);
        if (target.Success && Select(DiagnosticKind.SdkTarget))
        {
            Guidance = $"Install or select a .NET SDK that supports {target.Groups["framework"].Value} {target.Groups["version"].Value}; check global.json and PATH, then retry.";
            return;
        }
        var sdkFailure = Contains(line, "No .NET SDKs were found") ||
            Contains(line, "A compatible .NET SDK was not found") ||
            Contains(line, "NETSDK1141:") ||
            (Contains(line, "A compatible installed .NET SDK for global.json version") && Contains(line, "was not found"));
        if (sdkFailure && Select(DiagnosticKind.Sdk))
        {
            _sdkContext = ContextLines;
            UpdateSdkGuidance();
        }
        if (_kind == DiagnosticKind.Sdk && _sdkContext > 0)
        {
            var requested = RequestedSdk.Match(line);
            if (requested.Success) _sdkVersion = requested.Groups["version"].Value;
            UpdateSdkGuidance();
        }

        var detected = NodeDetected.Match(line);
        if (detected.Success)
        {
            _detectedNodeVersion = detected.Groups["version"].Value;
            _nodeContext = 4;
        }
        var minimum = AngularMinimum.Match(line);
        if (minimum.Success && Select(DiagnosticKind.AngularNode))
        {
            var versions = AngularVersions.Matches(minimum.Groups["versions"].Value)
                .Select(match => match.Groups["version"].Value).Distinct().Take(4).ToArray();
            var required = versions.Length > 0 ? $" (reported minimums: {string.Join(" or ", versions)})" : "";
            var actual = _nodeContext > 0 ? $"Node.js {_detectedNodeVersion} is incompatible. " : "Node.js is incompatible. ";
            Guidance = $"{actual}Install a Node.js version supported by this Angular project{required}, then retry.";
            return;
        }

        // EBADENGINE warnings are advisory. Require npm's fatal error prefix before retaining a diagnosis.
        var npmError = NpmErrorPrefix.IsMatch(line);
        if (npmError && (Contains(line, "code EBADENGINE") || Contains(line, "engine Unsupported engine")) && Select(DiagnosticKind.NpmEngine))
        {
            _engineContext = ContextLines;
            UpdateEngineGuidance();
        }
        if (_kind == DiagnosticKind.NpmEngine && _engineContext > 0 && npmError)
        {
            var required = Contains(line, "Required:");
            var actual = Contains(line, "Actual:");
            if (required || actual)
            {
                foreach (Match item in EngineRequirement.Matches(line))
                {
                    var node = item.Groups["engine"].Value.Equals("node", StringComparison.OrdinalIgnoreCase);
                    var value = item.Groups["version"].Value.Trim();
                    if (required && SafeVersionRange.IsMatch(value) && value.Any(char.IsAsciiDigit))
                    {
                        var range = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        if (node) _requiredNodeRange = range;
                        else _requiredNpmRange = range;
                    }
                    else if (actual)
                    {
                        var version = ExactVersion.Match(value);
                        if (!version.Success) continue;
                        if (node) _actualNodeVersion = version.Groups["version"].Value;
                        else _actualNpmVersion = version.Groups["version"].Value;
                    }
                }
                UpdateEngineGuidance();
            }
        }

        if ((MissingAngularPackage.IsMatch(line) || MissingAngularBuilder.IsMatch(line)) && Select(DiagnosticKind.AngularDependencies))
            Guidance = "Angular dependencies are missing. Install this project's dependencies with its configured package manager, then retry.";
    }

    private void ObserveMissingTool(string tool)
    {
        var guidance = tool.ToLowerInvariant() switch
        {
            "dotnet" => "dotnet is unavailable. Install the .NET SDK or runtime required by this app and make dotnet available on PATH; reopen Launcher, then retry.",
            "node" => "Node.js is unavailable. Install a Node.js version supported by this project and make node available on PATH; reopen Launcher, then retry.",
            "npm" or "npx" => "npm is unavailable. Install or repair the Node.js version required by this project, including npm on PATH; reopen Launcher, then retry.",
            "ng" => "Angular CLI is unavailable. Install this project's dependencies and use its configured npm script or local Angular CLI, then retry.",
            "python" or "python3" or "py" => "Python is unavailable. Install the Python version required by this app and make its configured command available on PATH; reopen Launcher, then retry.",
            "java" => "Java is unavailable. Install the Java runtime or JDK required by this app and make java available on PATH; reopen Launcher, then retry.",
            "docker" => "Docker is unavailable. Install Docker and make its CLI available on PATH; reopen Launcher, then retry.",
            "git" => "Git is unavailable. Install Git and make it available on PATH; reopen Launcher, then retry.",
            _ => null
        };
        if (guidance is not null && Select(DiagnosticKind.MissingCommand)) Guidance = guidance;
    }

    private void UpdateRuntimeGuidance()
    {
        var version = _runtimeVersion is null ? "required by this app" : $"compatible with {_runtimeVersion}";
        var architecture = _runtimeArchitecture is null ? "" : $" ({_runtimeArchitecture})";
        Guidance = $"Install {_runtimeName ?? "the .NET runtime"} {version}{architecture}, then retry.";
    }

    private void UpdateSdkGuidance() => Guidance = _sdkVersion is null
        ? "Install or select the .NET SDK required by this project; check global.json and PATH, then retry."
        : $"Install .NET SDK {_sdkVersion}, or a version allowed by global.json, then retry.";

    private void UpdateEngineGuidance()
    {
        var requirements = new List<string>(2);
        if (_requiredNodeRange is not null) requirements.Add($"Node.js {_requiredNodeRange}");
        if (_requiredNpmRange is not null) requirements.Add($"npm {_requiredNpmRange}");
        var versions = new List<string>(2);
        if (_actualNodeVersion is not null) versions.Add($"Node.js {_actualNodeVersion}");
        if (_actualNpmVersion is not null) versions.Add($"npm {_actualNpmVersion}");
        var actual = versions.Count == 0 ? "" : $" Current: {string.Join(", ", versions)}.";
        Guidance = requirements.Count == 0
            ? "Node.js/npm versions are incompatible. Install versions matching this project's package engine requirements, then retry."
            : $"Install compatible versions matching {string.Join(" and ", requirements)}.{actual} Retry dependency installation.";
    }

    private bool Select(DiagnosticKind kind)
    {
        // Retain the first recognized cause; later lines may enrich only that same diagnosis.
        if (_kind != DiagnosticKind.None && _kind != kind) return false;
        _kind = kind;
        return true;
    }

    private static bool Contains(string value, string text) => value.Contains(text, StringComparison.OrdinalIgnoreCase);
    private static Regex Pattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    private enum DiagnosticKind { None, MissingCommand, Runtime, Sdk, SdkTarget, AngularNode, NpmEngine, AngularDependencies }
}
