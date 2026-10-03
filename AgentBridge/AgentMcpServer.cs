using System.IO;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Services;

namespace FullStackLauncher.AgentBridge;

/// <summary>
/// Small stdio MCP adapter for the running dashboard. It has no process or settings-store
/// access of its own; all service operations go through the dashboard's named pipe.
/// </summary>
internal static class AgentMcpServer
{
    private const string Instructions =
        "Before starting a localhost host, call launcher_projects and launcher_services, then check launcher_service_status. " +
        "Reuse a running configured URL. For service changes, register a project session, claim its FIFO reservation, " +
        "and pass both session and lease tokens. Declare each service you use, heartbeat the session, " +
        "and release service use when done. Poll launcher_project_events for dashboard overrides and agent restart warnings. " +
        "Heartbeat responses also deliver pending restart notifications; deduplicate warnings by instanceId and sequence. " +
        "Never start another dev server for a configured service while it is running or starting. " +
        "After completing source changes, use launcher_git_connections for the matching configured project, " +
        "then register or reuse its project session and call launcher_record_git_changes for the active connection. " +
        "Report exactly one bullet containing one short sentence in Caveman full style. Start with an action verb and describe the main completed result. Drop filler and unnecessary articles; preserve meaning and technical accuracy. Aim for 80 characters or fewer; never exceed 120. No extra sentences, detail lists, or changelog. Omit internal names and implementation detail unless essential. Keep detailed reasoning and validation in the task report. This explicit commit-message style overrides normal-prose defaults for persisted text. Describe completed, verified changes only; reuse the same updateId on retries. " +
        "When using subagents, the coordinating agent submits one consolidated report per completed task. " +
        "Reports prepare the human-reviewed Commit all & push message; they never stage, commit, or push. " +
        "These tools manage configured services and Git change summaries, not Codex agents.";

    private const string NoArgs = """{"type":"object","properties":{},"additionalProperties":false}""";
    private const string ProjectArg = """{"type":"object","properties":{"projectId":{"type":"string"}},"required":["projectId"],"additionalProperties":false}""";
    private const string ServiceArg = """{"type":"object","properties":{"projectId":{"type":"string"},"serviceId":{"type":"string"}},"required":["projectId","serviceId"],"additionalProperties":false}""";

    private static readonly object[] Tools =
    [
        Tool("launcher_projects", "List configured launcher projects, roots, and current project reservations. Read only.", NoArgs),
        Tool("launcher_git_connections", "Find configured project repositories and the active saved Git connection, with credential-free remote URLs, current branch, and stable repository/connection IDs. Local inspection only; does not verify authentication or push permission.", ProjectArg),
        Tool("launcher_record_git_changes", "Queue one brief commit suggestion for the project's active Git connection and current branch. Send exactly one bullet containing one short Caveman-style sentence describing the main completed result; omit internal names and implementation detail unless essential. Requires an active project session, not a service reservation. Reuse updateId when retrying the same update. The WPF app assembles an editable bullet commit message; this never stages, commits, or pushes.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"repositoryId":{"type":"string"},"connectionId":{"type":"string"},"branch":{"type":"string"},"updateId":{"type":"string","minLength":1,"maxLength":100,"description":"Stable unique identifier for this completed update; reuse unchanged on retries."},"bullets":{"type":"array","minItems":1,"maxItems":5,"items":{"type":"string","minLength":1,"maxLength":120},"description":"Submit exactly one item containing one short Caveman-style sentence. Start with an action verb and describe the main completed result. Aim for 80 characters or fewer; maximum 120. No extra sentences, detail lists, or changelog. No internal names or implementation detail unless essential. No credentials, logs, code blocks, or bullet prefixes."}},"required":["projectId","sessionToken","repositoryId","connectionId","branch","updateId","bullets"],"additionalProperties":false}""", false, false),
        Tool("launcher_services", "List configured services and cached real process/HTTP state. Omit projectId to list all projects. Read only.",
            """{"type":"object","properties":{"projectId":{"type":"string"}},"additionalProperties":false}"""),
        Tool("launcher_service_status", "Fresh process and HTTP health check for one configured service. Read only.", ServiceArg),
        Tool("launcher_recent_activity", "Read recent launcher lifecycle messages for one service. Raw application output and configured commands are excluded.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"serviceId":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["projectId","serviceId"],"additionalProperties":false}"""),
        Tool("launcher_reservation_status", "Read owner, purpose, start/expiry time, and wait count for a configured project.", ProjectArg),
        Tool("launcher_register_project", "Register this active agent on a project. Returns a private session token and event cursor; heartbeat at least every 5 minutes.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"owner":{"type":"string","description":"Short, recognizable task or agent label."}},"required":["projectId","owner"],"additionalProperties":false}""", false),
        Tool("launcher_heartbeat_project", "Keep one registered agent session and its held reservation alive. Returns pending project restart warning notifications; inspect them before continuing local checks.", SessionSchema(), false),
        Tool("launcher_unregister_project", "End an agent session and release any held project reservation or wait ticket.", SessionSchema(), false),
        Tool("launcher_project_events", "Get dashboard service action events and agent restart warnings since a cursor for this active project session only. Keep session token for brief reconnects; save nextSequence and deduplicate warnings by instanceId and sequence.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"sinceSequence":{"type":"integer","minimum":0}},"required":["projectId","sessionToken"],"additionalProperties":false}"""),
        Tool("launcher_declare_service_use", "Declare that this active agent session is using one configured service. This is agent-reported use, not proof of a network connection.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"serviceId":{"type":"string"},"sessionToken":{"type":"string"}},"required":["projectId","serviceId","sessionToken"],"additionalProperties":false}""", false),
        Tool("launcher_release_service_use", "End this session's declared use of one configured service.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"serviceId":{"type":"string"},"sessionToken":{"type":"string"}},"required":["projectId","serviceId","sessionToken"],"additionalProperties":false}""", false),
        Tool("launcher_claim_project", "Claim cooperative project reservation, or join FIFO wait queue. Requires an active project session.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"purpose":{"type":"string"},"serviceId":{"type":"string","description":"Optional configured service to link this reservation or wait ticket to its service row."}},"required":["projectId","sessionToken","purpose"],"additionalProperties":false}""", false),
        Tool("launcher_wait_project", "Wait up to 20 seconds for a FIFO project ticket. Repeat while waiting; do not create competing hosts.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"ticket":{"type":"string"},"waitSeconds":{"type":"integer","minimum":0,"maximum":20}},"required":["projectId","sessionToken","ticket"],"additionalProperties":false}""", false),
        Tool("launcher_cancel_wait", "Cancel a project queue ticket; if already granted, release its reservation.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"ticket":{"type":"string"}},"required":["projectId","sessionToken","ticket"],"additionalProperties":false}""", false),
        Tool("launcher_release_project", "Release a held project reservation after disruptive work finishes.", LeaseSchema(), false),
        Tool("launcher_service_action", "Start, stop, or restart one configured service through launcher process protections. Requires active project reservation. Restart shows a nonblocking 'Agent initiated restart' warning and broadcasts the same message to active project sessions through events and heartbeat notifications. Returns observed health. Production API starts and restarts require dashboard review.",
            """{"type":"object","properties":{"projectId":{"type":"string"},"serviceId":{"type":"string"},"operation":{"type":"string","enum":["start","stop","restart"]},"sessionToken":{"type":"string"},"leaseToken":{"type":"string"}},"required":["projectId","serviceId","operation","sessionToken","leaseToken"],"additionalProperties":false}""", false)
    ];

    private static string SessionSchema() =>
        """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"}},"required":["projectId","sessionToken"],"additionalProperties":false}""";

    private static string LeaseSchema() =>
        """{"type":"object","properties":{"projectId":{"type":"string"},"sessionToken":{"type":"string"},"leaseToken":{"type":"string"}},"required":["projectId","sessionToken","leaseToken"],"additionalProperties":false}""";

    private static object Tool(string name, string description, string schema, bool readOnly = true, bool? destructive = null) => new
    {
        name, description, inputSchema = JsonSerializer.Deserialize<JsonElement>(schema),
        annotations = new { readOnlyHint = readOnly, destructiveHint = destructive ?? !readOnly }
    };

    internal static async Task RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length > 1_048_576) continue;
            object? response = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("method", out var methodElement) ||
                    methodElement.ValueKind != JsonValueKind.String) continue;
                var method = methodElement.GetString();
                if (!message.TryGetProperty("id", out var id)) continue; // Notification.
                var idCopy = id.Clone();
                var parameters = message.TryGetProperty("params", out var value) ? value : default;
                response = method switch
                {
                    "initialize" => Success(idCopy, new
                    {
                        protocolVersion = NegotiatedVersion(parameters),
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "full-stack-launcher", version = "1.1.0" },
                        instructions = Instructions
                    }),
                    "ping" => Success(idCopy, new { }),
                    "tools/list" => Success(idCopy, new { tools = Tools }),
                    "tools/call" => Success(idCopy, await CallToolAsync(parameters)),
                    _ => Error(idCopy, -32601, "Method not found")
                };
            }
            catch (JsonException) { }
            catch (Exception)
            {
                // Never write exception details to stdio: they may contain service data.
                response = new { jsonrpc = "2.0", id = (object?)null,
                    error = new { code = -32603, message = "Internal launcher MCP error" } };
            }
            if (response is not null)
                await output.WriteLineAsync(JsonSerializer.Serialize(response, AgentBridgeProtocol.Json));
        }
    }

    private static string NegotiatedVersion(JsonElement parameters)
    {
        var requested = StringArg(parameters, "protocolVersion");
        return requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"
            ? requested : "2025-11-25";
    }

    private static async Task<object> CallToolAsync(JsonElement parameters)
    {
        var name = StringArg(parameters, "name");
        if (name is null) return ToolFailure("Tool name is required.");
        var args = parameters.ValueKind == JsonValueKind.Object &&
                   parameters.TryGetProperty("arguments", out var value) ? value : default;
        var action = name switch
        {
            "launcher_projects" => "projects",
            "launcher_git_connections" => "git_connections",
            "launcher_record_git_changes" => "record_git_changes",
            "launcher_services" => "services",
            "launcher_service_status" => "service_status",
            "launcher_recent_activity" => "recent_activity",
            "launcher_reservation_status" => "reservation_status",
            "launcher_register_project" => "register_project",
            "launcher_heartbeat_project" => "heartbeat_project",
            "launcher_unregister_project" => "unregister_project",
            "launcher_project_events" => "project_events",
            "launcher_declare_service_use" => "declare_service_use",
            "launcher_release_service_use" => "release_service_use",
            "launcher_claim_project" => "claim_project",
            "launcher_wait_project" => "wait_project",
            "launcher_cancel_wait" => "cancel_wait",
            "launcher_release_project" => "release_project",
            "launcher_service_action" => "service_action",
            _ => null
        };
        if (action is null) return ToolFailure("Unknown launcher tool.");
        var request = new AgentBridgeRequest
        {
            Action = action,
            ProjectId = StringArg(args, "projectId"),
            ServiceId = StringArg(args, "serviceId"),
            Operation = StringArg(args, "operation"),
            Owner = StringArg(args, "owner"),
            Purpose = StringArg(args, "purpose"),
            SessionToken = StringArg(args, "sessionToken"),
            LeaseToken = StringArg(args, "leaseToken"),
            Ticket = StringArg(args, "ticket"),
            RepositoryId = StringArg(args, "repositoryId"),
            ConnectionId = StringArg(args, "connectionId"),
            Branch = StringArg(args, "branch"),
            UpdateId = StringArg(args, "updateId"),
            Bullets = StringArrayArg(args, "bullets"),
            Limit = IntArg(args, "limit"),
            WaitSeconds = IntArg(args, "waitSeconds"),
            SinceSequence = LongArg(args, "sinceSequence")
        };
        var result = await AgentBridgeProtocol.SendAsync(new SettingsStore().SettingsPath, request, CancellationToken.None);
        return result.Ok
            ? new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result.Data, AgentBridgeProtocol.Json) } },
                isError = false }
            : ToolFailure(result.Error ?? "Launcher bridge request failed.");
    }

    private static object ToolFailure(string message) => new
    {
        content = new[] { new { type = "text", text = message } }, isError = true
    };

    private static object Success(JsonElement id, object result) => new { jsonrpc = "2.0", id, result };
    private static object Error(JsonElement id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static string? StringArg(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntArg(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string[]? StringArrayArg(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 5) return null;
        var items = value.EnumerateArray().ToArray();
        return items.All(item => item.ValueKind == JsonValueKind.String)
            ? items.Select(item => item.GetString()!).ToArray() : null;
    }

    private static long? LongArg(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
