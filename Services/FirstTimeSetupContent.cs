using System.IO;
using System.Text;

namespace FullStackLauncher.Services;

internal sealed record FirstTimeSetupContent(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string AgentsInstructions,
    string SetupPrompt,
    string CodexCommand,
    string ManualInstructions)
{
    internal static FirstTimeSetupContent Create(string settingsPath, string? projectRoot)
    {
        var command = Path.GetFullPath(Environment.ProcessPath
            ?? throw new IOException("The running launcher executable could not be located."));
        var workingDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(Path.GetFullPath(Path.Combine(workingDirectory,
                typeof(App).Assembly.GetName().Name + ".dll")));
        }
        arguments.Add("--agent-mcp");
        arguments.Add("--settings");
        arguments.Add(Path.GetFullPath(settingsPath));

        var fields = BuildConnectionFields(command, arguments, workingDirectory);
        var codexCommand = "codex mcp add fullStackLauncher -- " + PowerShellLiteral(command) + " " +
            string.Join(" ", arguments.Select(PowerShellLiteral)) + Environment.NewLine + "codex mcp list";
        var projectHint = string.IsNullOrWhiteSpace(projectRoot)
            ? "Use the current repository's applicable AGENTS.md file."
            : "Project working folder: " + Path.GetFullPath(projectRoot) + Environment.NewLine +
              "Use that repository's applicable AGENTS.md file.";

        var manualInstructions = $$"""
            1. Keep Full Stack Dev Tool open under the same Windows user and desktop session as your agent. Keep the app in a stable folder; moving its executable requires updating the MCP command.
            2. Open your agent app's MCP settings. Add a local stdio server named fullStackLauncher. Enter these values; add each argument separately and do not include display quotes around paths:

            {{fields}}

            No environment variables, API keys, or remote URL are required.
            3. Save and enable the server. Reconnect MCP or restart your agent app, then open a new chat if existing chats do not discover the tools.
            4. Ask your agent to call launcher_projects. A successful response, even with an empty project list, proves the client can reach the running dashboard. A saved entry or listed tools alone does not prove that connection. If a project is configured, use launcher_services and launcher_service_status for read-only checks. Do not start, stop, or restart services just to verify setup.

            Codex CLI alternative, when codex is installed and available in PowerShell:

            {{codexCommand}}

            Inspect any existing fullStackLauncher entry first. Reuse an exact match. Preserve a different entry until the developer chooses which connection to keep. CLI registration still needs the reconnect and live check above.
            """;

        var agentsInstructions = $$"""
            ## Full Stack Dev Tool MCP integration

            Keep existing AGENTS.md instructions. Append this section once, or update only this integration section when its connection details change.

            ### First-time connection

            Connect the local stdio MCP server named fullStackLauncher using these machine-specific values:

            {{fields}}

            The dashboard must already be open under the same Windows user and desktop session. Both processes must use the exact settings path shown above. No API key or environment variable is required. If the app moves, obtain fresh values from Functions > First Time Setup.

            When asked to perform first-time setup, inspect the client's existing MCP registration first. Reuse a matching fullStackLauncher entry. Preserve other servers and settings. If this server name points elsewhere, explain the mismatch and let the developer choose the replacement rather than blindly overwriting it.

            Prefer the client's supported CLI or configuration tools when available. For Codex, run the following in PowerShell after checking for an existing entry:

            ```powershell
            {{codexCommand}}
            ```

            If authorized computer use is enabled, configure the client's MCP settings yourself using the exact command, separate arguments, and working directory above. If no setup tools or computer use are available, give the developer those exact fields and explain how to add a local stdio server manually. Never claim setup is complete from a saved registration alone. Ask the developer to reconnect MCP or restart the client and open a new chat when required, then call launcher_projects. A successful response, including an empty list, proves the live dashboard connection. Verify configured services only with read-only launcher_services and launcher_service_status calls.

            ### Project workflow

            Before starting a development server, call launcher_projects and inspect launcher_services for candidate projects. Match the current checkout to a returned project root or service working folder. Use the returned project and service IDs and call launcher_service_status. Reuse a Running service's activeUrl; wait and recheck a Starting service. A configuredUrl alone does not prove a host is running. Never start a duplicate host for a configured service. If no project matches, report that limitation; do not invent IDs or change the project library.

            Register with launcher_register_project when using a configured project. Keep its sessionToken and eventCursor. Declare each service you use with launcher_declare_service_use. Heartbeat with launcher_heartbeat_project about every two minutes; sessions and leases expire after five minutes without contact. Read launcher_project_events and heartbeat notifications for dashboard overrides and restart warnings. Save nextSequence and deduplicate by instanceId and sequence. After a dashboard restart, register again; old tokens are invalid.

            For an authorized Start, Stop, or Restart, claim the project with launcher_claim_project and the relevant serviceId. Wait with launcher_wait_project if queued. Pass sessionToken and leaseToken to launcher_service_action, then verify fresh launcher_service_status. Recheck status before retrying a timed-out action. Release the lease when finished; release service use and unregister when work ends. Respect dashboard controls. Never automatically restart after Force stop all. Production API Start/Restart requires dashboard review. Setup verification does not authorize service changes.

            After completed source changes, call launcher_git_connections for the matching project. Choose the repository whose repositoryRoot matches the checkout you changed. Use its returned repositoryId, current branch, and the connectionId matching activeConnectionId; do not guess an ambiguous connection or substitute another worktree. Register or reuse the project session and call launcher_record_git_changes with one stable updateId up to 100 characters. Retry unchanged content with the same ID. If the branch or connection changes, refresh discovery before reporting again. Send exactly one bullets item containing one short sentence in Caveman full style: start with an action verb, describe the main completed and verified result, aim for 80 characters or fewer, and never exceed 120. Omit extra sentences, paths, internal names, logs, and secrets. The coordinating agent includes delegated changes once. This queues a suggestion only; it does not authorize staging, committing, or pushing. If the bridge is unavailable, report that the suggestion was not queued.
            """;

        var setupPrompt = $$"""
            Read the applicable existing AGENTS.md instructions first, including the Full Stack Dev Tool MCP integration section I appended. Preserve all existing instructions and unrelated client settings. Perform first-time MCP setup for Full Stack Dev Tool on this computer.

            {{projectHint}}

            Use this connection:

            {{fields}}

            Inspect the client's existing fullStackLauncher registration. Reuse an exact match. If it differs, explain the mismatch and preserve it until I choose a replacement. Prefer supported CLI or configuration tools. If authorized computer use is enabled, enter the settings yourself. If setup tools or computer use are unavailable, give me exact manual steps and copyable values instead. No API keys or environment variables are needed.

            For Codex with its CLI available, use this PowerShell command after checking the existing registration:

            {{codexCommand}}

            Keep the dashboard open under the same Windows user and desktop session. Tell me when MCP must reconnect, the client must restart, or a new chat is needed; an existing chat may not gain new tools. After reconnecting, call launcher_projects through the configured MCP connection. A successful response, even with no configured projects, proves the live connection. A saved registration or tools list proves less. Match my checkout before read-only launcher_services and launcher_service_status checks. Do not add projects, change service configuration, start/stop/restart services, stage, commit, or push as part of setup. Report what you configured, what the live check proved, and any remaining manual step.
            """;

        return new FirstTimeSetupContent(command, arguments.AsReadOnly(), workingDirectory,
            agentsInstructions, setupPrompt, codexCommand, manualInstructions);
    }

    private static string BuildConnectionFields(string command, IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var fields = new StringBuilder();
        fields.AppendLine("Server name: fullStackLauncher");
        fields.AppendLine("Transport: stdio (local command)");
        fields.AppendLine("Command: " + command);
        fields.AppendLine("Arguments (one entry per line, in this order):");
        foreach (var argument in arguments) fields.AppendLine("    " + argument);
        fields.Append("Working directory: " + workingDirectory);
        return fields.ToString();
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
