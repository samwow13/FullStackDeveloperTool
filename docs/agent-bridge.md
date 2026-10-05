# Local Codex bridge for Full Stack Launcher

Full Stack Launcher owns configured project services. Its agent bridge lets a local Codex task inspect live process and HTTP health, reuse an existing localhost URL, and request guarded Start, Stop, or Restart through the running dashboard. It also discovers configured Git connections and queues concise agent change summaries for the editable **Commit all & push** message. It does not create Codex agents or start a dashboard just to answer a read request.

## Build and connect

For an installed build, use **Functions > First Time Setup**. Copy the generated integration section into the project's existing `AGENTS.md` without replacing current instructions, then copy the setup prompt into your agent chat. Its command, arguments, and working directory come from the running application; `--settings` always names the dashboard's exact resolved settings file. Keep the executable in a stable location and regenerate the connection details after moving it.

**Connect Codex** is an explicit opt-in to run the installed native Codex CLI's supported `mcp add` command. It reads the server list and inspects an existing `fullStackLauncher` entry first, reuses an exact enabled match, and refuses conflicting or restricted entries. It verifies registration through `mcp get`; it does not restart Codex, enable computer use, or prove that an existing chat has discovered the tools. Other MCP entries are not intentionally edited. Avoid editing MCP configuration in another client during registration: the CLI provides no conditional create operation against concurrent external changes.

Reconnect MCP or restart the agent client and open a new chat when necessary. Then call `launcher_projects` through that client. A successful response, including an empty project list, proves that the client can reach the running dashboard. Check configured services only with read-only list/status tools; setup does not start or stop them. If the native CLI is unavailable, use the displayed manual stdio fields or let the prompted agent configure them with its authorized local tools or computer use. Opening the dialog and copying its content do not change `AGENTS.md`, client configuration, or launcher settings.

The agent restart warning update is packaged at `publish\agent-restart-warnings-20261002\FullStackLauncher.exe`. Close the old dashboard through its normal close flow, resolve any draft/service prompts, and open this updated EXE to activate the popup and notification behavior. Running processes keep their old code until restarted. The existing adapter can forward the additive event and heartbeat fields; use the updated EXE for its MCP adapter as well to refresh tool descriptions. Live popup, restart and notification behavior has not been exercised.

1. Package the launcher application without running test projects:

   ```powershell
   & 'C:\Users\samwo\OneDrive\Desktop\MyTools\FullStackDeveloperTool\publish.ps1' -OutputDirectory 'publish\agent-restart-warnings-20261002'
   ```

2. Close any older launcher dashboard through its normal close dialog, then open the updated `publish\agent-restart-warnings-20261002\FullStackLauncher.exe` under the same Windows user and desktop session as Codex. The dashboard must already be running when a bridge tool is called. Keep using your existing `%LOCALAPPDATA%\FullStackLauncher\launcher.settings.json`; a new public checkout starts with an empty project library. Closing an older dashboard can prompt about unsaved drafts and running services, so resolve those prompts before opening the new copy. Reopening creates a new bridge instance; active agents must register and acquire new leases before continuing disruptive work.

3. Register the local stdio MCP adapter with Codex:

   ```powershell
   $launcherExe = 'C:\Users\samwo\OneDrive\Desktop\MyTools\FullStackDeveloperTool\publish\agent-restart-warnings-20261002\FullStackLauncher.exe'
   codex mcp add fullStackLauncher -- $launcherExe --agent-mcp
   codex mcp list
   ```

   If the dashboard uses an explicit `--settings` file, append `--settings 'C:\path\to\settings.json'` to the MCP command too. Both processes must use the same resolved settings path. Restart Codex desktop or its MCP connection after adding the server. [Official OpenAI Codex MCP setup](https://learn.chatgpt.com/docs/extend/mcp?surface=cli) documents local stdio commands, `codex mcp add`, `codex mcp list`, and shared desktop/CLI configuration.

The bridge changes no global Codex configuration by itself. **Connect Codex** or the command above is the explicit opt-in. It uses the launcher's existing EXE in `--agent-mcp` mode. That process connects to the dashboard through a Windows current-user named pipe. It does not load or edit project settings and cannot run a service without the dashboard.

## Agent workflow

For work in `C:\Users\samwo\OneDrive\Desktop\MyWorkWebsites` or another configured project:

1. Call `launcher_projects`; match the project by returned root path and use its project ID. Call `launcher_services` for that ID. Use `launcher_service_status` before deciding whether to start any local host. If a service is Running, reuse its `activeUrl`; if Starting, wait and check again. URLs contain only the localhost origin, without a path, query, or fragment. The `configuredUrl` is a target, not proof that a host is listening.
2. Call `launcher_register_project` with a recognizable task name when beginning project work. Keep its `sessionToken` and `eventCursor`. For each service this task is using, call `launcher_declare_service_use` with that service's ID. This is the agent's statement of use, not proof of a network connection. Call `launcher_heartbeat_project` about every two minutes while working; sessions and declarations expire after five minutes without contact. Inspect its `notifications` for pending agent restart warnings. `launcher_project_events` returns only that registered session's project events after its cursor; save `nextSequence` after reading. Poll before and after local runtime checks, after a brief reconnect, and after service changes. Events before registration and events for other projects are unavailable.
3. Before a disruptive service action, call `launcher_claim_project` with a short purpose and the relevant `serviceId`. It returns `leaseToken` when acquired or a FIFO `ticket` when waiting. A service-scoped lease or wait ticket appears on that service's dashboard row and cannot authorize an action on a different service. Omitting `serviceId` preserves a project-wide reservation, which is not presented as use of any particular service. For a ticket, call `launcher_wait_project` (up to 20 seconds per call) until acquired; use `launcher_cancel_wait` if abandoning work. Check `launcher_reservation_status` to see current owner, purpose, start/expiry time, queue length, and any agent-start suspension.
4. With both tokens, call `launcher_service_action` for one configured service and `start`, `stop`, or `restart`. Read its `outcome` and returned service state. `starting` means HTTP health has not yet passed; call `launcher_service_status` later. If the call times out or disconnects, inspect fresh status before deciding on another action; the original action may have reached the dashboard. Release the lease with `launcher_release_project` when disruptive work is done. Call `launcher_release_service_use` when the task stops using that service, then `launcher_unregister_project` when the project work ends.

Use `launcher_recent_activity` for bounded launcher lifecycle messages. It excludes raw application stdout/stderr and configured command text. The launcher's credential redaction is best effort; arbitrary app output is not a secret store and is deliberately omitted from this bridge.

## Agent restart warnings

After an agent Restart passes configuration and command preflight, the dashboard shows the exact warning **Agent initiated restart** immediately before stopping the service. It is a nonblocking popup that does not take focus, remains visible when the dashboard is minimized, and closes after eight seconds or dismissal. Sequential API and frontend restarts reuse the same popup and reset its timer. Manual dashboard restarts do not display this agent warning. A rejected reservation, Production action, Force stop suspension, or preflight does not emit an initiated warning. The warning means an operation began; it does not prove the service became healthy.

The same warning is available to all active registered sessions in that project through `launcher_project_events`, including the initiating session. Its fixed fields are `action = "agent_restart"`, `result = "initiated"`, `message = "Agent initiated restart"`, and `severity = "warning"`; the event also carries sequence, project/service IDs and UTC time. `launcher_heartbeat_project` returns pending warning events in `notifications` using a separate per-session delivery cursor. Deduplicate by the response's `instanceId` and the event's `sequence` across both responses; a dashboard restart resets sequences. A lost heartbeat response can be replayed through `launcher_project_events` from the saved event cursor. Notifications use the existing bounded event retention and session lifetime; delivery requires polling or a heartbeat, not an unsolicited Codex chat message.

On a warning, wait for fresh `launcher_service_status` readiness before continuing dependent local checks. Preserve drafts and retry checks interrupted by the restart. Respect dashboard overrides and Force stop suspension. Unregistered, expired, and other-project sessions receive no warning; agents must register and remain active during shared project work.

CRM instructions require restarting the verified Local API and every configured local frontend through `launcher_service_action` after successfully applying relevant local migrations. Use a project-wide reservation for the sequence, or release and acquire each service-scoped reservation in turn. This requirement does not authorize production actions or bypass target verification, backups, SQL review, or dashboard Force stop all.

## Available MCP tools

| Tool | Purpose |
| --- | --- |
| `launcher_projects` | Configured projects, root paths, archive state, reservation status. |
| `launcher_git_connections` | Project repositories, current branch, active connection, credential-free URLs and stable identities. Local metadata only; authentication is not checked. |
| `launcher_record_git_changes` | Save a completed-task summary as one brief Caveman sentence in one bullet for the active connection and branch; requires a project session, no service lease. Never stages, commits or pushes. |
| `launcher_services` | Configured services, cached process/HTTP state, URLs, ports, API environment selection. |
| `launcher_service_status` | Fresh process and HTTP check for one service. |
| `launcher_recent_activity` | Recent launcher lifecycle messages for one service. |
| `launcher_reservation_status` | Current project reservation and wait count. |
| `launcher_register_project` | Start a project-scoped agent session and event cursor. |
| `launcher_heartbeat_project` | Renew an active session and its held reservation; return pending project restart warning notifications. |
| `launcher_unregister_project` | End session and release its reservation or queue ticket. |
| `launcher_project_events` | Read project-scoped dashboard action events and agent restart warnings for an active session. |
| `launcher_declare_service_use` | Report that this session is using one configured service; renew by session heartbeat. |
| `launcher_release_service_use` | Remove that session's declared use of one service. |
| `launcher_claim_project` | Acquire project lease or join FIFO queue. |
| `launcher_wait_project` | Wait on one FIFO ticket. |
| `launcher_cancel_wait` | Cancel waiting or release a just-granted ticket. |
| `launcher_release_project` | Release a held lease. |
| `launcher_service_action` | Start, stop, or restart a configured service; an agent restart shows and broadcasts the fixed warning. |

## Agent change summaries

Git discovery includes the configured project and service folders plus immediate child repositories under the project root. A database source repository at `<project root>\ORKidsDatabase`, alongside the API and frontend folders, needs its own `.git` file or directory but does not need a launcher service registration. Its SQL, stored procedures, and table schema remain ordinary source files. The bounded child scan checks `ORKidsDatabase` explicitly and skips linked child folders or linked `.git` metadata; it does not recursively search descendants or open a database connection.

The Git workspace's **Select which repo**, the **Repo** selector in the **Next commit** settings cog, and `launcher_git_connections` share this discovery. Git preselects the current Next commit repository; returning from Git selects the repository opened there in Next commit. `launcher_record_git_changes` repeats the same discovery and validates the current repository, branch, and active connection before saving a report.

After completing a source update:

1. Call `launcher_projects` and match the repository's configured project/root or service folder. Call `launcher_git_connections` with that `projectId`. Use the returned repository whose `repositoryRoot` matches the checkout you changed. Never substitute another checkout or linked worktree.
2. Use its current `branch` and `activeConnectionId`, and the matching connection's `connectionId`. Discovery prefers the explicit remote selected in the WPF Git workspace. With no saved choice, it uses the current branch's upstream or the only configured connection. An ambiguous or stale choice requires selection in the WPF Git workspace; do not guess. Discovery does not contact GitHub/Azure DevOps, read credentials, or prove authentication/push permission.
3. Register or reuse an active session for this project. Call `launcher_record_git_changes` with `projectId`, `sessionToken`, `repositoryId`, `connectionId`, `branch`, `updateId`, and `bullets`. A service reservation is not needed. Use a stable unique update ID, such as `task-id:completed-update-1`, no more than 100 characters. Retry the same content with the same ID. Reusing an ID for different content is rejected; retrying an already consumed report does not enqueue it again.
4. Submit exactly one bullet without a bullet prefix, logs, code blocks, secrets, or unsupported claims. Send exactly one bullet containing one short sentence in Caveman full style. Start with an action verb and describe the main completed result. Drop filler and unnecessary articles; preserve meaning and technical accuracy. Aim for 80 characters or fewer; never exceed 120. No extra sentences, detail lists, or changelog. Omit file paths, class names, method names, internal IDs, and implementation detail unless essential to understand the change. Keep detailed reasoning and validation in the task report. For example: "Shorten commit text." This commit-message style overrides the Caveman skill's normal-prose default for persisted text. The coordinating agent reports its work and delegated changes once; separate tasks submit separate reports. Heartbeat an existing long-lived session as usual and unregister when finished. Only a successful tool response confirms that the report was queued.

Example `bullets`:

```json
[
  "Shorten commit text."
]
```

The next **Commit all & push** review prepares a message such as:

```text
Update main

- Shorten commit text.
```

The message stays editable. Existing manual drafts stay intact, with an explicit action to use the agent summary message. Review the displayed agent changes and their inclusion checkbox. Identical bullet text is combined. Reports are scoped to this checkout, branch, and exact remote identity; they do not automatically follow branch switches or changed remote URLs.

The dashboard's **Next commit** switch in **Sections ▾** opens a live panel along the full window height to the right of Services. It shows one complete prepared message and the 4,000-character count. Unread AI text appears mint and underlined. Clicking the message, or pressing Enter while it has focus, saves read status for only the captured updates fully represented in that message; successful saves clear their unread styling. Scrolling and focus alone do not acknowledge updates. Read status does not include or consume a report. The panel and Git review share session-only custom drafts; later reports are flagged for review instead of silently changing a custom message or an open review. Oversized messages remain editable, and commit submission is blocked until they fit.

The updated ledger reads version 1 without changing it. Its next real save writes version 2, preserving viewed timestamps. Older dashboards reject version 2 rather than dropping acknowledgment data. Use the updated dashboard and reconnect its MCP adapter together when activating this build.

The ledger lives in the checkout's Git metadata as `launcher-agent-changes.json`, outside tracked source and launcher settings. Linked worktrees have separate ledgers. Writes are locked and atomic; unsupported or damaged files are preserved. Only the exact reviewed entries are marked included after a confirmed new local commit. Cancellation, failed commits, and push-only retries retain pending reports. A later push failure does not requeue reports already included in a confirmed local commit. Reports arriving after review remain pending for the next review. If saving inclusion fails after Git commits, the app reports that failure and retains the reports for manual review. The ledger is bounded to 10,000 retained report receipts and 16 MiB; it fails visibly at capacity instead of dropping retry history.

Both the running dashboard and the stdio MCP adapter must use the updated build. After replacing or changing the executable, restart the dashboard through its normal close flow and reconnect the Codex MCP server. Older connected adapters do not gain tools automatically. If tools are missing or the dashboard is unavailable, disclose that the summary was not queued; do not write another project's ledger or silently stage/commit/push instead.

## Coordination and safety

Reservations coordinate cooperating local agents. They do not lock the filesystem, block manually launched terminals, or prevent a person from using dashboard controls. Every service action also uses the launcher's existing process identity, port conflict, and command validation. The bridge cannot execute arbitrary commands, clean/setup commands, change API configuration, resolve an unknown port conflict, or read API secrets. Production API starts and restarts require dashboard review.

Sessions and leases expire after five minutes without a heartbeat or action. Queued tickets disappear with their sessions. Dashboard restart creates a new `instanceId`; all old sessions, tickets, and leases become invalid, so register and claim again. Only active registered sessions receive project events. The bridge keeps at most 200 recent events, and discards events when no eligible session remains. Polling supplies notifications; this stdio bridge does not push events.

Dashboard service cards show port and process/HTTP status without agent activity or connected-agent labels. The bridge's service-list/status responses retain token-free, project-scoped activity. Agent names and last-seen times come from currently registered sessions that explicitly declared use of that service or named it on a reservation/wait ticket; these responses do not measure TCP connections. An expired use declaration is reported as stale for at most five more minutes, without a live lease or ticket. Project-wide reservations are available in project reservation status, not attributed to one service.

Dashboard **Force stop all** revokes that project's leases and wait tickets, emits `force_stop_all` events, and suspends agent Start/Restart calls. Do not automatically reclaim and restart after this human action. A person must start or restart a service in the dashboard to lift the suspension. Stop and review that service's actual health before continuing. Dashboard Start, Restart, and Stop events include project/service IDs, action, UTC time, and result; no command text or log payload is included.

For Codex instructions in the `MyWorkWebsites` workspace, use this wording:

> Before starting a localhost development server, query Full Stack Launcher with `launcher_projects`, `launcher_services`, and `launcher_service_status`. Reuse a configured running host. Register a project session, declare each configured service you use, and heartbeat the session. For Start, Stop, or Restart, claim the project with that service's ID and hold its reservation. Release declared service use when done. Watch project events for dashboard overrides. Never launch a duplicate server for a configured service; never automatically restart after dashboard Force stop all. Do not create extra Codex agents to manage services. This bridge coordinates services, not Codex agents.
