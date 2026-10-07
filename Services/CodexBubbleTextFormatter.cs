using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

/// <summary>
/// Produces short command descriptions for chat presentation only. It never changes
/// stored messages, evaluates a script, or treats a command as a successful result.
/// </summary>
internal static class CodexBubbleTextFormatter
{
    private const int MaximumInputLength = 64 * 1024;
    private const int MaximumActions = 8;
    private const int MaximumTokens = 256;
    private static readonly Regex Diagnostic = new(
        @"(?im)^\s*(?:\[[^\]\r\n]{1,40}\]\s*)?(?:error\b|warning\b|fatal\b|exception\b|unhandled exception\b|traceback\b|build failed\b|npm err!\b|[\w-]+\s+:\s+|at\s+[\w.]+\([^\r\n]*\)\s*(?:in\s+|$))|\b(?:error|warning)\s+(?:CS|MSB|NETSDK|NU|TS)\d+\b|\b(?:FullyQualifiedErrorId|CategoryInfo)\s*:|\bexit code\s*[:=]?\s*[1-9]\d*\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(75));
    private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "ps1", "bash", "sh", "shell", "zsh", "cmd", "bat", "batch"
    };
    private static readonly HashSet<string> JsonCommandProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "command", "shell", "workdir", "cwd", "timeout", "timeout_ms", "yield_time_ms",
        "max_output_tokens", "login", "tty", "description", "justification", "sandbox_permissions", "prefix_rule"
    };

    internal static string Format(string text)
    {
        var safeText = SensitiveDataProtection.Redact(text);
        if (string.IsNullOrWhiteSpace(safeText) || safeText.Length > MaximumInputLength) return safeText;
        try
        {
            // Preserve complete diagnostic context, including a command printed next to its failure.
            if (Diagnostic.IsMatch(safeText)) return safeText;
            if (TryJsonCommand(safeText, out var jsonSummary)) return jsonSummary;
            if (IsStandalonePowerShellScript(safeText)) return DescribeScript(safeText, "PowerShell", true);

            var lines = safeText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var result = new List<string>(lines.Length);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (TryFence(line, out var marker, out var language))
                {
                    var end = index + 1;
                    while (end < lines.Length && !IsClosingFence(lines[end], marker)) end++;
                    if (end < lines.Length && ShellLanguages.Contains(language))
                    {
                        var body = string.Join("\n", lines[(index + 1)..end]);
                        result.Add(DescribeScript(body, ShellLabel(language), true));
                        index = end;
                        continue;
                    }
                    // Preserve every unrecognized fence intact; commands inside C#, SQL, or prose
                    // examples are not an instruction to summarize that source code.
                    var final = Math.Min(end, lines.Length - 1);
                    result.AddRange(lines[index..(final + 1)]);
                    index = final;
                    continue;
                }

                if (TryCommand(line.Trim(), out var summary)) result.Add(summary);
                else result.Add(line);
            }
            return string.Join(Environment.NewLine, result);
        }
        catch (RegexMatchTimeoutException) { return safeText; }
    }

    private static bool TryJsonCommand(string text, out string summary)
    {
        summary = "";
        if (!text.TrimStart().StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? command = null;
            var shell = "Shell";
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!JsonCommandProperties.Contains(property.Name)) return false;
                if (property.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("command", StringComparison.OrdinalIgnoreCase))
                {
                    if (command is not null || property.Value.ValueKind != JsonValueKind.String) return false;
                    command = property.Value.GetString();
                }
                else if (property.Name.Equals("shell", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind == JsonValueKind.String)
                    shell = ShellLabel(ExecutableName(property.Value.GetString() ?? ""));
            }
            if (string.IsNullOrWhiteSpace(command)) return false;
            command = SensitiveDataProtection.Redact(command);
            if (Diagnostic.IsMatch(command)) return false;
            summary = DescribeScript(command, shell, true);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryFence(string line, out string marker, out string language)
    {
        marker = language = "";
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return false;
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;
        if (length < 3) return false;
        marker = trimmed[..length];
        language = trimmed[length..].Trim();
        return true;
    }

    private static bool IsClosingFence(string line, string marker)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= marker.Length && trimmed.All(character => character == marker[0]);
    }

    private static string DescribeScript(string script, string shell, bool commandContext, int depth = 0)
    {
        var actions = new List<string>();
        var unknown = false;
        foreach (var segment in SplitCommands(script))
        {
            var command = segment.Trim();
            if (command.Length == 0 || command.StartsWith('#') || IsHousekeeping(command)) continue;
            if (TryCommand(command, out var action, commandContext, depth + 1))
            {
                if (!actions.Contains(action, StringComparer.Ordinal)) actions.Add(action);
            }
            else unknown = true;
            if (actions.Count >= MaximumActions)
            {
                unknown = true;
                break;
            }
        }
        if (unknown || actions.Count == 0) actions.Add($"Run {shell} script");
        return string.Join("; ", actions);
    }

    private static IEnumerable<string> SplitCommands(string script)
    {
        var start = 0;
        var quote = '\0';
        for (var index = 0; index < script.Length; index++)
        {
            var character = script[index];
            if (character == '`' && index + 1 < script.Length)
            {
                index++;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (index + 1 < script.Length && script[index + 1] == quote) index++;
                    else quote = '\0';
                }
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character is not (';' or '\r' or '\n' or '|') &&
                !(character == '&' && index + 1 < script.Length && script[index + 1] == '&')) continue;
            yield return script[start..index];
            if (index + 1 < script.Length && script[index + 1] == character) index++;
            start = index + 1;
        }
        if (start < script.Length) yield return script[start..];
    }

    private static bool TryCommand(string command, out string summary, bool commandContext = false, int depth = 0)
    {
        summary = "";
        if (command.Length == 0 || depth > 3) return false;
        if (!commandContext && (command.StartsWith("PS ", StringComparison.OrdinalIgnoreCase) ||
                               command.StartsWith("$ ", StringComparison.Ordinal)))
        {
            var promptEnd = command.StartsWith("$ ", StringComparison.Ordinal) ? 1 : command.IndexOf('>');
            if (promptEnd < 0) return false;
            command = command[(promptEnd + 1)..].Trim();
        }
        if (command.StartsWith("& ", StringComparison.Ordinal)) command = command[2..].TrimStart();
        if (command.StartsWith(". ", StringComparison.Ordinal)) command = command[2..].TrimStart();
        if (command.StartsWith('$') && command.IndexOf('=') is var equals && equals > 1)
        {
            var variable = command[..equals].Trim();
            if (variable.Any(character => !char.IsLetterOrDigit(character) && character is not ('$' or '_' or ':')))
                return false;
            var assignedCommand = command[(equals + 1)..].TrimStart();
            // A quoted command in a variable is data, not an invocation.
            if (assignedCommand.StartsWith('"') || assignedCommand.StartsWith('\'')) return false;
            return TryCommand(assignedCommand, out summary, commandContext, depth + 1);
        }

        if (!TryTokens(command, out var tokens) || tokens.Count == 0) return false;
        var executable = ExecutableName(tokens[0]);
        var arguments = tokens.Skip(1).ToArray();
        // Confirm the first invocation before summarizing a chain. This also covers
        // script files and command aliases, so later actions are never silently lost.
        var segments = SplitCommands(command).Take(2).ToArray();
        if (segments.Length > 1)
        {
            if (!TryCommand(segments[0].Trim(), out _, commandContext, depth + 1)) return false;
            summary = DescribeScript(command, executable.Contains('-') ? "PowerShell" : ShellLabel(executable), commandContext, depth);
            return true;
        }
        if (executable is "powershell" or "pwsh" or "bash" or "sh" or "zsh" or "cmd")
        {
            var shell = ShellLabel(executable);
            for (var index = 0; index < arguments.Length; index++)
            {
                if (arguments[index].Equals("-File", StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Length)
                {
                    summary = ScriptName(arguments[index + 1]) is { } filename ? $"Run {filename}" : $"Run {shell} script";
                    return true;
                }
                if (arguments[index].Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                    arguments[index].Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                    arguments[index].Equals("/c", StringComparison.OrdinalIgnoreCase))
                {
                    summary = index + 1 < arguments.Length && depth < 3
                        ? DescribeScript(string.Join(" ", arguments[(index + 1)..]), shell, true, depth)
                        : $"Run {shell} script";
                    return true;
                }
            }
            summary = $"Run {shell} script";
            return arguments.Length > 0 || commandContext;
        }

        if (executable == "dotnet") return TryDotnet(arguments, commandContext, out summary);
        if (executable is "npm" or "pnpm" or "yarn") return TryPackageCommand(executable, arguments, commandContext, out summary);
        if (executable == "git") return TryGit(arguments, commandContext, out summary);
        if (executable is "rg" or "grep" or "findstr")
        {
            if (!commandContext && !SafeSearchArguments(arguments)) return false;
            summary = arguments.Contains("--files", StringComparer.Ordinal) ? "Find files" : "Search files";
            return arguments.Length > 0;
        }
        if (executable is "msbuild" or "make" or "cmake")
        {
            if (!commandContext && !LooksLikeArguments(arguments) &&
                !(executable == "make" && arguments.Length == 1 && arguments[0] is "all" or "build" or "clean" or "install")) return false;
            summary = executable switch
            {
                "msbuild" => "Build project",
                "cmake" when arguments.Contains("--build") => "Build project",
                "cmake" when arguments.Contains("--version") => "Inspect CMake version",
                "cmake" => "Run CMake command",
                _ => "Run make command"
            };
            return true;
        }
        if (executable is "python" or "python3" or "py" or "node")
        {
            var script = arguments.FirstOrDefault(argument => ScriptName(argument) is not null);
            if (script is not null && (commandContext || LooksLikeArguments(arguments.Where(argument => argument != script).ToArray())))
            { summary = $"Run {ScriptName(script)}"; return true; }
            if (commandContext || arguments.Any(argument => argument is "-c" or "-e" or "-m"))
            {
                summary = $"Run {(executable == "node" ? "Node.js" : "Python")} script";
                return true;
            }
        }
        if (ScriptName(tokens[0]) is { } scriptName)
        {
            if (!commandContext && !LooksLikeArguments(arguments)) return false;
            summary = $"Run {scriptName}";
            return true;
        }

        var action = executable switch
        {
            "get-content" or "gc" or "cat" or "type" => "Read file",
            "get-childitem" or "gci" or "ls" or "dir" => "List files",
            "select-string" => "Search files",
            "get-process" => "Inspect processes",
            "get-ciminstance" => "Inspect system information",
            "get-filehash" => "Check file hash",
            "test-path" => "Check path",
            "copy-item" or "cp" or "copy" => "Copy files",
            "move-item" or "mv" or "move" => "Move files",
            "remove-item" or "rm" or "del" => "Remove files",
            "new-item" or "mkdir" => "Create file or folder",
            "set-content" or "add-content" or "out-file" => "Write file",
            "start-process" => "Start process",
            "stop-process" => "Stop process",
            "invoke-webrequest" or "curl" or "wget" => "Send web request",
            "invoke-restmethod" => "Send API request",
            _ => null
        };
        if (action is null || !commandContext && !LooksLikeArguments(arguments)) return false;
        var filenameArgument = arguments.FirstOrDefault(argument => IsPath(argument) && !argument.StartsWith('-'));
        summary = filenameArgument is not null && action is "Read file" or "Check file hash" or "Write file"
            ? $"{action} {DisplayName(filenameArgument)}" : action;
        return true;
    }

    private static bool TryDotnet(string[] arguments, bool commandContext, out string summary)
    {
        summary = "";
        if (arguments.Length == 0) return false;
        var action = arguments[0].ToLowerInvariant() switch
        {
            "build" => "Build", "publish" => "Publish", "test" => "Run tests for",
            "restore" => "Restore packages for", "clean" => "Clean", "run" => "Run",
            "--info" or "--version" or "--list-sdks" or "--list-runtimes" => "Inspect .NET installation",
            _ => null
        };
        if (action is null) return false;
        if (arguments[0].StartsWith("--", StringComparison.Ordinal)) { summary = action; return arguments.Length == 1; }
        var project = arguments.Skip(1).FirstOrDefault(argument => IsProject(argument));
        var configuration = OptionValue(arguments, "-c", "--configuration", "-p:Configuration", "/p:Configuration");
        if (!commandContext && !SafeDotnetArguments(arguments)) return false;
        summary = $"{action} {(project is null ? "project" : DisplayName(project))}";
        if (configuration is not null && SafeLabel(configuration)) summary += $" ({configuration})";
        return true;
    }

    private static bool SafeDotnetArguments(string[] arguments)
    {
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument == "--") return true;
            if (argument is "-c" or "--configuration" or "-o" or "--output" or "-r" or "--runtime" or
                "-f" or "--framework" or "--project" or "--verbosity" or "-v" or "--filter" or
                "--logger" or "--artifacts-path" or "--results-directory" or "--settings" or "--self-contained" or "--sc")
            {
                if (++index >= arguments.Length) return false;
                continue;
            }
            if (argument.StartsWith('-') || argument.StartsWith('/') || IsProject(argument) || IsPath(argument)) continue;
            return false;
        }
        return true;
    }

    private static bool TryPackageCommand(string executable, string[] arguments, bool commandContext, out string summary)
    {
        summary = "";
        if (arguments.Length == 0) return false;
        var verb = arguments[0].ToLowerInvariant();
        if (verb == "run" && arguments.Length > 1)
        {
            var task = arguments[1];
            if (!SafeLabel(task)) return false;
            if (!commandContext && arguments.Skip(2).Any(argument => !argument.StartsWith('-') && !IsPath(argument))) return false;
            summary = task switch
            {
                "build" => "Build app", "test" => "Run app tests", "lint" => "Check code style",
                "start" or "dev" => "Start app", _ => $"Run {executable} task {task}"
            };
            return true;
        }
        if (verb is "install" or "ci" or "restore") summary = "Install packages";
        else if (verb == "test") summary = "Run app tests";
        else if (verb == "build") summary = "Build app";
        else if (verb is "start" or "dev") summary = "Start app";
        else return false;
        return commandContext || arguments.Skip(1).All(argument => argument.StartsWith('-') || IsPath(argument));
    }

    private static bool TryGit(string[] arguments, bool commandContext, out string summary)
    {
        summary = "";
        var index = 0;
        while (index < arguments.Length && arguments[index].StartsWith('-'))
        {
            if (arguments[index] is "-C" or "-c" or "--git-dir" or "--work-tree") index += 2;
            else index++;
        }
        if (index >= arguments.Length) return false;
        var action = arguments[index].ToLowerInvariant() switch
        {
            "status" => "Check Git status", "diff" => "Review Git changes", "log" => "Read Git history",
            "show" => "Inspect Git revision", "branch" => "Inspect Git branches", "rev-parse" => "Inspect Git repository",
            "ls-files" => "List tracked files", "ls-remote" => "Inspect Git remote", "remote" => "Inspect Git remotes",
            "fetch" => "Fetch Git refs", "pull" => "Pull Git changes", "push" => "Push Git changes",
            "add" => "Stage Git changes", "commit" => "Create Git commit", "merge" => "Merge Git branch",
            "merge-tree" => "Check Git merge", "checkout" or "switch" => "Switch Git branch",
            "restore" => "Restore Git files", "reset" => "Reset Git state", "clean" => "Clean Git files",
            "cherry-pick" => "Apply Git commit", "rebase" => "Rebase Git branch", "clone" => "Clone Git repository",
            _ => null
        };
        if (action is null) return false;
        var operands = arguments[(index + 1)..];
        if (arguments[index].Equals("branch", StringComparison.OrdinalIgnoreCase))
        {
            if (operands.Any(argument => argument is "-d" or "-D" or "--delete")) action = "Delete Git branch";
            else if (operands.Any(argument => argument is "-m" or "-M" or "--move")) action = "Rename Git branch";
            else if (operands.Any(argument => argument is "-c" or "-C" or "--copy")) action = "Copy Git branch";
            else if (operands.Any(argument => !argument.StartsWith('-')) &&
                     !operands.Any(argument => argument is "--list" or "--contains" or "--merged" or "--no-merged"))
                action = "Create Git branch";
        }
        else if (arguments[index].Equals("remote", StringComparison.OrdinalIgnoreCase) && operands.Length > 0)
            action = operands[0] switch
            {
                "add" => "Add Git remote", "remove" or "rm" => "Remove Git remote", "rename" => "Rename Git remote",
                "set-url" => "Change Git remote URL", "set-head" => "Change Git remote HEAD",
                "prune" => "Prune Git remote refs", "update" => "Update Git remotes", _ => action
            };
        else if (arguments[index].Equals("reset", StringComparison.OrdinalIgnoreCase) && operands.Contains("--hard"))
            action = "Reset Git state and working files";
        else if (arguments[index].Equals("clean", StringComparison.OrdinalIgnoreCase))
            action = operands.Any(argument => argument is "-n" or "--dry-run" ||
                argument.StartsWith('-') && !argument.StartsWith("--", StringComparison.Ordinal) &&
                argument[1..].All(character => "dfinqxX".Contains(character)) && argument.Contains('n'))
                ? "Review untracked Git files" : "Remove untracked Git files";
        else if (arguments[index].Equals("commit", StringComparison.OrdinalIgnoreCase) && operands.Contains("--amend"))
            action = "Amend Git commit";
        else if (arguments[index].Equals("push", StringComparison.OrdinalIgnoreCase) && operands.Contains("--delete"))
            action = "Delete remote Git ref";
        else if (arguments[index].Equals("switch", StringComparison.OrdinalIgnoreCase) &&
                 operands.Any(argument => argument is "-c" or "-C" or "--create" or "--force-create"))
            action = "Create and switch Git branch";
        else if (arguments[index].Equals("checkout", StringComparison.OrdinalIgnoreCase) && operands.Contains("--"))
            action = "Restore Git files";
        if (!commandContext && !SafeGitArguments(arguments[(index + 1)..])) return false;
        summary = action;
        return true;
    }

    private static bool SafeGitArguments(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "-m" or "--message" or "--format" or "--pretty" or "--since" or "--until" or
                "--author" or "--grep" or "--output" or "-n" or "--max-count")
            {
                if (++index >= arguments.Length) return false;
                continue;
            }
            if (argument.StartsWith('-') || IsPath(argument) || argument is "HEAD" or "main" or "master" or
                "origin" or "upstream" || argument.StartsWith("HEAD", StringComparison.Ordinal) ||
                argument.Length is >= 7 and <= 40 && argument.All(Uri.IsHexDigit)) continue;
            return false;
        }
        return true;
    }

    private static bool SafeSearchArguments(string[] arguments)
    {
        var patternSeen = arguments.Contains("--files", StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "-g" or "--glob" or "-e" or "--regexp" or "-f" or "--file" or
                "-m" or "--max-count" or "-t" or "--type" or "-A" or "-B" or "-C")
            {
                if (++index >= arguments.Length) return false;
                if (argument is "-e" or "--regexp" or "-f" or "--file") patternSeen = true;
                continue;
            }
            if (argument.StartsWith('-') || argument.StartsWith('/') || IsPath(argument)) continue;
            if (!patternSeen) { patternSeen = true; continue; }
            return false;
        }
        return arguments.Length > 0;
    }

    private static string? OptionValue(string[] arguments, params string[] names)
    {
        for (var index = 0; index < arguments.Length; index++)
            foreach (var name in names)
            {
                if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Length)
                    return arguments[index + 1];
                if (arguments[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arguments[index][(name.Length + 1)..];
            }
        return null;
    }

    private static bool TryTokens(string text, out List<string> tokens)
    {
        tokens = [];
        var token = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '`' && index + 1 < text.Length)
            {
                token.Append(text[++index]);
                started = true;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote) token.Append(text[++index]);
                    else quote = '\0';
                }
                else token.Append(character);
                continue;
            }
            if (character is '\'' or '"') { quote = character; started = true; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; }
                if (tokens.Count >= MaximumTokens) return false;
            }
            else { token.Append(character); started = true; }
        }
        if (quote != '\0') return false;
        if (started) tokens.Add(token.ToString());
        return tokens.Count <= MaximumTokens;
    }

    private static bool IsHousekeeping(string command)
    {
        if (command.IndexOfAny(['{', '}', '(', ')']) >= 0) return false;
        var lowered = command.ToLowerInvariant();
        var variable = lowered.Split('=', 2)[0].Trim();
        return lowered.Contains('=') && variable is "$erroractionpreference" or "$psnativecommanduseerroractionpreference" ||
               lowered.StartsWith("set-location ", StringComparison.Ordinal) || lowered.StartsWith("cd ", StringComparison.Ordinal) ||
               lowered.StartsWith("select-object ", StringComparison.Ordinal) || lowered.StartsWith("format-list", StringComparison.Ordinal) ||
               lowered.StartsWith("format-table", StringComparison.Ordinal) || lowered.StartsWith("out-string", StringComparison.Ordinal);
    }

    private static bool IsStandalonePowerShellScript(string text)
    {
        var lines = SplitCommands(text).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length < 2 || !IsPowerShellAssignment(lines[0])) return false;
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || IsPowerShellAssignment(line) || IsHousekeeping(line) ||
                line is "{" or "}" or ")" or ");" ||
                new[] { "if (", "if(", "elseif (", "foreach (", "foreach(", "for (", "while (", "try {", "catch", "finally", "function ", "return ", "exit ", "throw " }
                    .Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                TryCommand(line, out _)) continue;
            // Other cmdlets are script syntax but have no safely recognized action.
            var name = line.Split(' ', '\t')[0];
            var dash = name.IndexOf('-');
            if (dash > 0 && dash < name.Length - 1 && name.All(character => char.IsLetter(character) || character == '-') &&
                TryTokens(line, out var tokens) &&
                (LooksLikeArguments(tokens.Skip(1).ToArray()) || line[name.Length..].TrimStart().StartsWith('"') ||
                 line[name.Length..].TrimStart().StartsWith('\''))) continue;
            return false;
        }
        return true;
    }

    private static bool IsPowerShellAssignment(string line)
    {
        if (!line.StartsWith('$')) return false;
        var equals = line.IndexOf('=');
        if (equals < 2) return false;
        return line[..equals].Trim().All(character => char.IsLetterOrDigit(character) || character is '$' or '_' or ':');
    }

    private static bool LooksLikeArguments(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith('-'))
            {
                if (argument.Equals("-Raw", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("-Recurse", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("-Force", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("-PassThru", StringComparison.OrdinalIgnoreCase) ||
                    argument.Equals("-Wait", StringComparison.OrdinalIgnoreCase)) continue;
                if (index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-')) index++;
                continue;
            }
            if (IsPath(argument) || argument.All(char.IsDigit)) continue;
            return false;
        }
        return true;
    }

    private static bool IsProject(string value) => new[] { ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx" }
        .Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsPath(string value) => value.Contains('\\') || value.Contains('/') ||
        value.StartsWith('.') || ScriptName(value) is not null || IsProject(value) ||
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static string? ScriptName(string value)
    {
        if (!new[] { ".ps1", ".cmd", ".bat", ".sh", ".py", ".js", ".mjs", ".cjs" }
            .Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return null;
        var name = DisplayName(value);
        return SafeLabel(name) ? name : null;
    }

    private static string DisplayName(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        var name = separator < 0 ? normalized : normalized[(separator + 1)..];
        return SafeLabel(name) ? name : "selected file";
    }

    private static bool SafeLabel(string value) => value.Length is > 0 and <= 100 &&
        value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-' or '+' or '(' or ')');

    private static string ExecutableName(string value)
    {
        var normalized = value.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static string ShellLabel(string name) => name.ToLowerInvariant() switch
    {
        "powershell" or "pwsh" or "ps1" => "PowerShell", "cmd" or "bat" or "batch" => "Command Prompt",
        "bash" => "Bash", "zsh" => "Zsh", _ => "shell"
    };
}
