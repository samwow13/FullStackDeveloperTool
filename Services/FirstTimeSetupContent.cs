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

            After completed source changes, call launcher_git_connections for the matching project. Resolve each changed file to its owning Git checkout and match that checkout to a returned repositoryRoot. Register or reuse the project session, then call launcher_record_git_changes separately for every changed repository using its returned repositoryId, actual current branch, and the connectionId matching its activeConnectionId. A project with API, frontend, and database repos needs separate repository-specific summaries; leave unchanged repos alone. Never choose a repo from dashboard selection or service labels, guess an ambiguous connection, or substitute another worktree. Use a distinct stable updateId up to 100 characters for each repository report; retry unchanged content with the same ID. If a save outcome is uncertain, retry its original ID, content, and scope. Never move that report to a new branch after a switch. Refresh discovery and verify change ownership before reporting new work; disclose unresolved saves if the original scope is unavailable. Send exactly one bullets item per call containing one short sentence in Caveman full style: start with an action verb, describe that repository's main completed and verified result, aim for 80 characters or fewer, and never exceed 120. Omit extra sentences, paths, internal names, logs, and secrets. The coordinating agent includes delegated changes once per changed repository. Check the response's project, repositoryRoot, remoteName, and branch. This queues suggestions only; it does not authorize staging, committing, or pushing. If the bridge is unavailable, report which suggestions were not queued.

            ### Suggested follow-up notes

            After completing work, save useful potential follow-up work discovered during that task to the matching project's Notes for human review. This is standing authorization to save evidence-grounded suggestions only. Do not invent tasks. Saving a suggestion does not authorize performing its work, adding or enabling a queue item, enabling a queue, starting a task, or changing services.

            Call launcher_projects and launcher_services and match the actual checkout to the most specific configured project root or service working folder. Notes and queue entries belong only to their saved project ID. Register or reuse an active launcher_register_project session for that project; never choose a project from the dashboard selection, prompt keywords, or a similarly named project. Use launcher_project_notes when useful to avoid duplicates. One coordinating agent saves suggestions for the task and delegated work once. Call launcher_save_follow_up_note with projectId, sessionToken, a stable unique updateId, a concise name, a self-contained prompt, and supporting context. Write saved prompts and context in normal English. Include the observed issue or opportunity, relevant files or known page, prior completed work, verification evidence and limits, and clear next scope. Include sourceTaskId, sourcePrompt, pageUrl, and pageTitle only when actually known; never guess provenance or claim a screenshot proves page source. Exclude secrets and unrelated private context. The dashboard records creation time and the original registered agent identity.

            Retry an uncertain save with exactly the same updateId and payload. Use a new ID for another suggestion; changed content under an existing ID is rejected. Only a successful response confirms saving. A retry after deletion must not recreate the note. If the project does not match, the dashboard is unavailable, or the tool is missing, report that the suggestion was not saved; do not write the notes file directly or alter project settings. No service reservation is needed. Heartbeat an existing session as usual and release any task-owned session when finished. Both the dashboard and MCP adapter must use the updated build; reconnect MCP after updating to discover these tools.
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
