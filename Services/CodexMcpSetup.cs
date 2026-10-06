using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

internal sealed record CodexMcpSetupResult(bool Succeeded, string Message);

/// <summary>
/// Registers the current launcher only after an explicit Connect Codex action.
/// Uses the supported CLI, never edits client configuration or credentials directly.
/// </summary>
internal static class CodexMcpSetup
{
    private const string ServerName = "fullStackLauncher";
    private const int OutputLimit = 256 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private const string VerifyConnection = "Reconnect MCP or restart Codex, then open a new chat and ask your agent to call launcher_projects. Use the updated build for both the dashboard and MCP adapter to discover new tools, including launcher_save_follow_up_note. Registration alone does not prove a live dashboard connection.";

    internal static Task<CodexMcpSetupResult> ConnectAsync(FirstTimeSetupContent content,
        CancellationToken token = default) => Task.Run(() => ConnectOwned(content, token));

    private static CodexMcpSetupResult ConnectOwned(FirstTimeSetupContent content, CancellationToken token)
    {
        var acquired = false;
        try
        {
            // Keep mutex ownership on this worker thread across asynchronous CLI calls.
            // This serializes launcher setup windows/processes, not external client writers.
            using var gate = new Mutex(false, @"Local\FullStackLauncher.CodexMcpSetup");
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(false, "Another launcher is connecting Codex. Wait for it to finish, then try again.");
            try { return ConnectCoreAsync(content, token).GetAwaiter().GetResult(); }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return new(false, "Codex setup could not start. Use the copied connection values in your agent app's MCP settings.");
        }
    }

    private static async Task<CodexMcpSetupResult> ConnectCoreAsync(FirstTimeSetupContent content,
        CancellationToken token)
    {
        var registrationStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateContent(content);
            var executable = FindExecutable();
            if (executable is null)
                return new(false, "Codex CLI was not found. Install the Windows Codex app or add a native codex.exe to PATH, then reopen First Time Setup. You can also use the copied manual setup instructions.");

            var list = await RunAsync(executable, content.WorkingDirectory, ["mcp", "list", "--json"], token).ConfigureAwait(false);
            if (!list.Succeeded) return CliFailure("inspect MCP settings", list);
            var exists = ContainsServer(list.Output);
            var get = await RunAsync(executable, content.WorkingDirectory, ["mcp", "get", ServerName, "--json"], token).ConfigureAwait(false);
            if (exists)
            {
                if (!get.Succeeded) return CliFailure("inspect the existing fullStackLauncher entry", get);
                return ExistingResult(get.Output, content);
            }
            if (get.ExitCode == 0)
                return new(false, "MCP settings changed during setup. No registration was added. Check the existing fullStackLauncher entry and try again.");

            // The CLI has no create-only operation. Recheck immediately before adding.
            // An external writer can still race this check; never rewrite config ourselves.
            var freshList = await RunAsync(executable, content.WorkingDirectory, ["mcp", "list", "--json"], token).ConfigureAwait(false);
            if (!freshList.Succeeded) return CliFailure("recheck MCP settings", freshList);
            if (ContainsServer(freshList.Output))
            {
                var freshGet = await RunAsync(executable, content.WorkingDirectory, ["mcp", "get", ServerName, "--json"], token).ConfigureAwait(false);
                if (!freshGet.Succeeded) return CliFailure("inspect the new fullStackLauncher entry", freshGet);
                return ExistingResult(freshGet.Output, content);
            }

            token.ThrowIfCancellationRequested();
            var arguments = new List<string> { "mcp", "add", ServerName, "--", content.Command };
            arguments.AddRange(content.Arguments);
            registrationStarted = true;
            var added = await RunAsync(executable, content.WorkingDirectory, arguments, token).ConfigureAwait(false);
            if (!added.Succeeded)
                return new(false, $"Codex did not confirm registration (exit code {added.ExitCode}). An entry may have been saved. Inspect fullStackLauncher in Codex before retrying.");
            var saved = await RunAsync(executable, content.WorkingDirectory, ["mcp", "get", ServerName, "--json"], token).ConfigureAwait(false);
            if (!saved.Succeeded)
                return new(false, "Codex completed registration, but its saved entry could not be verified. Inspect fullStackLauncher in Codex before retrying. " + VerifyConnection);
            var mismatch = RegistrationMismatch(saved.Output, content);
            return mismatch is null
                ? new(true, "Codex MCP registration saved and verified. " + VerifyConnection)
                : new(false, "Codex completed registration, but the saved entry needs review: " + mismatch + " Use the copied manual setup instructions.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(false, registrationStarted
                ? "Setup canceled after registration began. An entry may have been saved. Inspect fullStackLauncher in Codex before retrying."
                : "Setup canceled before registration. No MCP entry was added.");
        }
        catch (TimeoutException)
        {
            return new(false, registrationStarted
                ? "Codex setup timed out after registration began. An entry may have been saved. Inspect fullStackLauncher in Codex before retrying."
                : "Codex setup timed out before registration. No MCP entry was added. Use the copied manual setup instructions.");
        }
        catch (JsonException)
        {
            return UnsupportedData(registrationStarted);
        }
        catch (InvalidDataException)
        {
            return UnsupportedData(registrationStarted);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return new(false, registrationStarted
                ? "Codex setup could not finish. An entry may have been saved. Inspect fullStackLauncher in Codex before retrying."
                : "Codex setup could not start. No MCP entry was added. Use the copied connection values in your agent app's MCP settings.");
        }
    }

    private static CodexMcpSetupResult ExistingResult(string output, FirstTimeSetupContent content)
    {
        var mismatch = RegistrationMismatch(output, content);
        return mismatch is null
            ? new(true, "Codex already has this launcher connection. " + VerifyConnection)
            : new(false, "Existing fullStackLauncher entry was preserved: " + mismatch + " Review it in Codex MCP settings using the copied connection values. This button does not replace or enable existing entries.");
    }

    private static string? RegistrationMismatch(string output, FirstTimeSetupContent content)
    {
        using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !StringProperty(root, "name", out var name) || name != ServerName)
            throw new InvalidDataException();
        if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            return "the server is disabled or its enabled state is unavailable.";
        if (root.TryGetProperty("disabled_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            return "Codex reports a disabled reason.";
        if (root.TryGetProperty("enabled_tools", out var enabledTools) && enabledTools.ValueKind != JsonValueKind.Null)
            return "an enabled-tools filter restricts the connection.";
        if (!EmptyArrayOrNull(root, "disabled_tools"))
            return "a disabled-tools filter restricts the connection.";
        if (!root.TryGetProperty("transport", out var transport) || transport.ValueKind != JsonValueKind.Object
            || !StringProperty(transport, "type", out var type) || type != "stdio")
            return "the transport is not this launcher's local stdio connection.";
        if (!StringProperty(transport, "command", out var command) || !PathsEqual(command, content.Command))
            return "the command points to a different launcher or location.";
        if (!transport.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array
            || args.GetArrayLength() != content.Arguments.Count)
            return "the arguments differ from the current launcher connection.";
        var index = 0;
        foreach (var argument in args.EnumerateArray())
            if (argument.ValueKind != JsonValueKind.String || argument.GetString() != content.Arguments[index++])
                return "the arguments differ from the current launcher connection.";
        if (transport.TryGetProperty("cwd", out var cwd) && cwd.ValueKind != JsonValueKind.Null
            && (cwd.ValueKind != JsonValueKind.String || !PathsEqual(cwd.GetString()!, content.WorkingDirectory)))
            return "the working directory differs from the current launcher connection.";
        if (transport.TryGetProperty("env", out var env) && env.ValueKind != JsonValueKind.Null
            && (env.ValueKind != JsonValueKind.Object || env.EnumerateObject().Any()))
            return "the entry has environment overrides that need manual review.";
        if (!EmptyArrayOrNull(transport, "env_vars"))
            return "the entry has environment passthrough settings that need manual review.";
        // CLI add does not support cwd. All launcher/settings operands are absolute,
        // so an omitted cwd is compatible with this generated registration.
        return null;
    }

    private static bool ContainsServer(string output)
    {
        using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var found = false;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !StringProperty(entry, "name", out var name))
                throw new InvalidDataException();
            if (!name.Equals(ServerName, StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve differently capitalized or duplicate names instead of adding another.
            if (name != ServerName || found) throw new InvalidDataException();
            found = true;
        }
        return found;
    }

    private static bool StringProperty(JsonElement root, string property, out string value)
    {
        value = "";
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return value.Length > 0;
    }

    private static bool EmptyArrayOrNull(JsonElement root, string property) =>
        !root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
        || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static bool PathsEqual(string left, string right) =>
        Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void ValidateContent(FirstTimeSetupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Path.IsPathFullyQualified(content.Command) || !File.Exists(content.Command)
            || !content.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(content.WorkingDirectory) || !Directory.Exists(content.WorkingDirectory)
            || content.Arguments.Count is < 3 or > 4
            || content.Arguments.Any(argument => string.IsNullOrEmpty(argument) || argument.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("The current launcher connection values are invalid.");
        var offset = content.Arguments.Count - 3;
        if (content.Arguments[offset] != "--agent-mcp" || content.Arguments[offset + 1] != "--settings"
            || !Path.IsPathFullyQualified(content.Arguments[offset + 2])
            || (offset == 1 && !Path.IsPathFullyQualified(content.Arguments[0])))
            throw new ArgumentException("The current launcher connection values are invalid.");
    }

    private static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Take(256))
        {
            var candidate = ExecutableIn(directory.Trim().Trim('"'));
            if (candidate is not null) return candidate;
        }
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var direct = ExecutableIn(bin);
        if (direct is not null) return direct;
        if (!Directory.Exists(bin)) return null;
        foreach (var directory in new DirectoryInfo(bin).EnumerateDirectories().Take(64).OrderByDescending(item => item.LastWriteTimeUtc))
        {
            var candidate = ExecutableIn(directory.FullName);
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static string? ExecutableIn(string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return null;
            var candidate = Path.GetFullPath(Path.Combine(directory, "codex.exe"));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (IsExpectedFailure(exception)) { return null; }
    }

    private static async Task<CliResult> RunAsync(string executable, string folder,
        IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(CommandTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = folder, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException();
        try
        {
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, retain: true, deadline.Token);
            // CLI output can include other servers' environment values. Never display or log it.
            var error = ReadBoundedAsync(process.StandardError, retain: false, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false);
            var captured = await output.ConfigureAwait(false);
            return new(process.ExitCode, captured.Text, captured.Truncated);
        }
        catch (OperationCanceledException)
        {
            KillOwnedProcess(process);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException();
        }
        catch
        {
            deadline.Cancel();
            KillOwnedProcess(process);
            throw;
        }
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
    }

    private static async Task<CapturedText> ReadBoundedAsync(StreamReader reader, bool retain, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (!retain) continue;
            var remaining = OutputLimit - text.Length;
            text.Append(buffer, 0, Math.Min(count, remaining));
            if (count > remaining) truncated = true;
        }
        return new(text.ToString(), truncated);
    }

    private static CodexMcpSetupResult CliFailure(string action, CliResult result) => new(false,
        $"Codex could not {action} (exit code {result.ExitCode}). No MCP entry was added. Use the copied manual setup instructions.");

    private static CodexMcpSetupResult UnsupportedData(bool registrationStarted) => new(false,
        registrationStarted
            ? "Codex registration began, but its response could not be verified. Inspect fullStackLauncher in Codex before retrying."
            : "Codex returned unsupported MCP settings. No MCP entry was added. Update Codex or use the copied manual setup instructions.");

    private static bool IsExpectedFailure(Exception exception) => exception is Win32Exception or IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or SecurityException or NotSupportedException;

    private sealed record CapturedText(string Text, bool Truncated);
    private sealed record CliResult(int ExitCode, string Output, bool Truncated)
    {
        public bool Succeeded => ExitCode == 0 && !Truncated;
    }
}
