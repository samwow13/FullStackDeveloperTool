using System.IO;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Verifies command startup before submitting a prompt or creating a Codex task.</summary>
internal static class CodexCommandAccessCheck
{
    private const string Marker = "FULL_STACK_LAUNCHER_COMMAND_READY";
    private const int CommandTimeoutMilliseconds = 10_000;

    internal static async Task VerifyAsync(CodexAppServerConnection connection, string folder,
        CodexAgentAccessMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new CodexCommandAccessException("The assigned project folder must be an existing absolute folder. No task or prompt was submitted.");

        var resolvedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!Path.IsPathFullyQualified(powershell) || !File.Exists(powershell))
            throw new CodexCommandAccessException("Windows PowerShell is unavailable for the Codex command access check. No task or prompt was submitted.");

        // Use the already selected access mode. A failed workspace check must never
        // retry with full access, change Codex settings, or stop a locked runtime.
        object sandboxPolicy = mode switch
        {
            CodexAgentAccessMode.Workspace => new
            {
                type = "workspaceWrite", writableRoots = new[] { resolvedFolder }, networkAccess = false
            },
            CodexAgentAccessMode.FullAccess => new { type = "dangerFullAccess" },
            _ => throw new ArgumentException("Choose a supported agent access mode.", nameof(mode))
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // command/exec is a documented App Server operation and creates no
            // thread or turn. The fixed command reads no project data, loads no
            // profile, accepts no input, and writes only a marker to stdout.
            var result = await connection.RequestAsync("command/exec", new
            {
                command = new[] { powershell, "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::Out.Write('" + Marker + "')" },
                cwd = resolvedFolder,
                sandboxPolicy,
                timeoutMs = CommandTimeoutMilliseconds
            }, timeout.Token).ConfigureAwait(false);

            if (!result.TryGetProperty("exitCode", out var exitCode)
                || exitCode.ValueKind != System.Text.Json.JsonValueKind.Number
                || !exitCode.TryGetInt32(out var code)
                || !result.TryGetProperty("stdout", out var stdout)
                || stdout.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new CodexCommandAccessException("Codex returned an unsupported command access check response. No task or prompt was submitted.");

            // Never display or persist stdout/stderr from the server. Only the
            // exact fixed marker and successful exit establish command startup.
            if (code != 0 || !string.Equals(stdout.GetString(), Marker, StringComparison.Ordinal))
                throw new CodexCommandAccessException("Codex command access check did not complete successfully. Review Codex command permissions and Windows sandbox setup. No task or prompt was submitted.");
        }
        catch (CodexRequestException ex)
        {
            throw new CodexCommandAccessException(ex.Message + " No task or prompt was submitted.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexCommandAccessException("Codex command access check timed out. No task or prompt was submitted.", ex);
        }
    }
}

