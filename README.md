# Full Stack Launcher

A Windows WPF dashboard for your development services and console apps, distributable as one executable. Save and archive projects, see process status and console output, run commands with arguments, and stop tracked processes. Web services also have local endpoint checks and browser shortcuts. Your projects and preferences are kept in your Windows user account's local application data, so moving or replacing the EXE keeps them available.

The dark theme uses explicit light text on service cards, database panels, and copy menus, with readable muted labels for disabled buttons. The mint terminal/stack icon is embedded in the executable and all launcher windows for the title bar, taskbar, and File Explorer. Icon source artwork, sizes, and regeneration instructions are in `Assets/README.md`.

The Projects sidebar shows each configured app or service by name with a live status label and colored dot. Green means running or a successfully completed console command; amber means starting, working, or stopping; red means a conflict or error; gray means stopped or awaiting the first check. Every saved project is checked on the existing three-second refresh timer, including projects you have not selected or have archived and while sections are hidden. These indicators share the service cards' process checks and web services' local HTTP checks, including services started externally. Hover over a status row for its latest detail.

Each project also shows its current **Git branch** in the sidebar and beneath the selected project's folder path. Branch information refreshes every three seconds, when the dashboard regains focus, and with **Refresh**. The launcher checks the project root and each configured service folder, using their nearest Git repository; shared repositories appear once, while separate repositories get labeled rows. Hover for full branch names and folder details. Detached checkouts show a short commit ID; folders outside Git, missing folders and unreadable metadata have explicit status labels. Branches are read directly from local Git metadata, including linked worktrees and submodules, without requiring Git on PATH. This is the folder's current checkout, not necessarily the branch used to build an already-running service. Branch information stays in memory and never changes a checkout or saved settings.

## Run from source

Double-click **launch.cmd**. It builds the launcher in Release mode and opens its window. Building requires Windows and the .NET 10 SDK or a newer compatible SDK.

You can also run:

```powershell
dotnet run --project .\FullStackLauncher.csproj
```

The launcher needs the same tools your projects normally use on `PATH`. This CRM needs its backend's .NET SDK, plus a Node.js/npm version supported by the Angular project. The default single-file build includes the launcher's .NET runtime; it does not install SDKs, Node, npm, dependencies, or databases for your projects.

## Start with your own projects

A new Windows user starts with no projects, selected project, or developer-tool shortcuts. Source checkouts and published EXEs do not import the author's settings or carry personal project profiles. Choose **+ Project** or **+ Console project** to configure your own folders and commands. The root `launcher.settings.json` is an empty example, not an automatically imported settings file.

Run **Setup** if dependencies have not been restored or installed. **Clean** executes the saved clean command; it does not remove source files or install dependencies. Angular's default clean command clears its build cache.

Use the project editor to add more projects and any number of services. Each service has its own type, working folder and start command; clean and setup are optional. Web services also have a local URL and UI path. The **Console** type uses process tracking without a web address; other kinds retain the existing web service behavior. Commands are trusted Windows shell commands executed in the specified working folder with your account's privileges.

## Archive and unarchive projects

Select a project and choose **Archive selected project** to move it out of the active sidebar. Open **Archived (N)**, select a project, and choose **Unarchive selected project** to return it to the active list. The launcher starts with the active list. If every project is archived, the Archived control remains available.

Archiving preserves the project's settings, stable IDs, database selection, notes, queue, and captured output. Running apps keep running and remain monitored; the active list shows a hint when archived projects have tracked processes. Open Archived to inspect or stop them. Batch actions apply to the selected project, and launcher shutdown still checks owned processes across all projects. Save or cancel inline project edits before archiving or changing the list.

## Console apps and executable arguments

Choose **+ Console project** in the sidebar for a single console app, or **Add console app** in an existing project's full settings editor. Set its project root, working folder, and start command. There is no localhost, port, UI path, or Local/Prod API configuration to fill in. Optional clean/setup commands stay collapsed in the editor and appear on its card only when configured.

Examples of start commands:

```text
dotnet run -- --mode quick
dotnet run -- "C:\Tools\Target.exe" --mode quick
"C:\Tools\Worker.exe" --mode quick
```

For `dotnet run`, arguments after `--` are passed to your console app. Use the argument names your app actually accepts for a target executable and its options. Quote paths containing spaces. Running a .NET project requires its SDK; directly running a published EXE uses that application's normal runtime requirements.

**Use executable…** selects and quotes a direct EXE command. It replaces the default `dotnet run` or an existing direct EXE path while keeping that EXE command's arguments. For an existing .NET command with arguments, enter the target path in the command so the app's arguments remain intact.

Use **Run**, **Restart**, and **Stop** on the app card, with selectable output in its console. Console apps show running process IDs and **Completed** after a successful command and its tracked children finish; failures show the command's nonzero exit code. These run results and logs are held in memory for the current launcher session. No HTTP listener or readiness response is required.

Console commands started here are supervised together with their child processes, including executables outside the project folder and children that outlive the original command. Stop and launcher shutdown include those owned children. Applications delegated to an already-running external instance are outside that ownership. Console commands receive closed standard input; interactive prompts are not supported.

## Edit project names and service ports

Click the project title or **Edit** to rename the project and its services directly in the dashboard. The toggle shows **Editing**, and mint fields identify what can change. **Save changes**, clicking the toggle again, or Enter in a field saves; **Cancel** or Escape discards the draft. Ctrl+S also saves. Save or cancel before switching projects; closing with edits offers save/discard/cancel. Save errors keep the draft open. Project and service names update in the sidebar without interrupting running services or changing their IDs, notes, or logs. The unused Settings shortcut has been removed.

Each service card shows its configured port beside the service kind, including when collapsed. In edit mode, **Desired port** becomes available after a successful check confirms that service has no running processes. **Stop service to edit port** is available beside a running service. A stopped service with a failed launch or a conflict can still choose a different port. Ports must be whole numbers from 1 to 65535 and cannot duplicate another saved service's port. Saving updates both the local address and supported launch arguments; use Start after saving to apply the new port. Names can be saved while services run.

Inline ports support `dotnet run`, configured Local/Prod APIs, and Angular, Vite, Next.js or webpack dev-server commands, including verified npm scripts. For custom commands, unreadable scripts, or npm scripts containing their own port flag, the card explains why the port is locked. **Save & more options…** preserves access to the full editor for paths, commands, addresses and service management once project services are stopped.

Saving an inline .NET API port change also updates matching local `target` URLs in the same project's Angular development JSON proxies, discovered through `angular.json`'s default serve configuration. For this CRM, that is `proxy.conf.json`: changing API port 5170 to 5000 updates its `/api/**` target to `http://localhost:5000`. Only matching loopback targets change; other addresses, options, paths, comments and formatting remain intact. A frontend-only port edit keeps its API target unchanged. The API uses the saved launcher URL/arguments on its next Start; its launch-profile file is not rewritten.

Restart a running Angular frontend after saving an API port change so it loads the updated proxy. Save shows this reminder and never restarts a service automatically. Missing, unsupported, malformed, unmatched or unwritable proxy configuration keeps editing open with an error. Proxy replacements are backed up and restored if a later file/settings save fails; failed recovery retains the backup paths for manual review. Automatic synchronization supports JSON objects mapping routes to proxy options, not JavaScript proxies, environment files, or command-line configuration/proxy overrides. Other CORS origins and hard-coded URLs, and changes made in the full profile editor, still require manual updates.

Each service needs a separate working folder. Across all saved projects, two service folders cannot be equal or nested inside one another. This first version uses project-folder evidence to recognize external processes, so overlapping folders would make ownership ambiguous. Set each service to its actual API/frontend folder using **Browse** before saving. A project root can contain all of its service folders.

## API secrets and Local / Prod configuration

Each .NET service card has **Manage secrets & settings…**, **Use Local & restart**, and **Use Prod & restart**. The editor discovers the configured folder's single `.csproj` and literal `UserSecretsId`. Choose the store to edit, select or add a colon-separated key such as `ConnectionStrings:DefaultConnection`, enter its value, choose **Stage value**, then **Save changes**. Removing an override is also staged until saved. The key list includes appsettings key names and source filenames; stored values stay masked until you explicitly reveal the selected value. Failed saves retain drafts, and concurrent edits require a reload before saving.

Local and Prod save separate Windows-user encrypted files under `%LOCALAPPDATA%\FullStackLauncher\api-secrets\<project-secret-ID hash>\`. Windows DPAPI protects their contents, temporary files, and backups; no new plaintext credential files are written. Values stay out of project profiles, public builds, and command arguments. This manages configuration for the locally running API, not Azure deployment settings. Changing the editor's profile does not activate it.

To reuse existing configuration, choose **Import legacy values**, review the staged values, and save. Import is specific to the selected Local or Prod profile and never copies Local into Prod. The original `.NET UserSecrets` files remain unchanged for direct `dotnet`/EF workflows. Those externally owned files are still plaintext; the launcher neither silently synchronizes them nor rewrites them. An encrypted profile remains authoritative even when all its overrides have been removed. A managed launch with legacy secrets but no encrypted profile asks you to import/save first.

The dashboard buttons preflight the chosen configuration and save its selection, then start or restart that API. Prod must contain saved values, including an explicit nonempty production value for every connection-string key discovered in the readable Local store; Local values are never copied to Prod automatically. An invalid inactive store does not block switching to a valid store. A failed selection save leaves the current API running. Failed stops/starts retain the selected profile and report the running environment separately rather than claiming it applied.

After opting into these controls, API starts use `dotnet run --project <project> --no-launch-profile` with the configured local listening address. This replaces the saved start command for that service; Clean and Setup retain their saved commands. Managed builds use an isolated artifacts directory under LocalAppData and disable the SDK-generated UserSecrets attribute. This keeps the standard Development provider from reviving old plaintext overrides without changing ordinary command-line build outputs. It requires a .NET 8 or newer compatible SDK; APIs with custom configuration providers remain responsible for their own provider order.

Local sets both .NET host environments to `Development`; Prod sets them to `Production`. Selected secrets are passed through the child process environment, above `appsettings.json` and the environment-specific appsettings file. Inherited hierarchical configuration, connection-string aliases and host environment/URL settings are removed. Ordinary process variables remain. Runtime/OS control keys cannot be used as application secrets. Environment variables and decrypted values exist in process memory; DPAPI does not protect against software already running as the same Windows user.

Configuration changes apply on restart. Managed launches cannot represent JSON null/container overrides through environment variables: replace them with text values or remove the overrides before launching. A separately launched Development API continues using its own normal user-secrets provider. Environment selection alone cannot establish which database a value points to; configure both encrypted profiles deliberately.

Prod selection or an applied Prod process shows a red service section and a persistent production banner, including when services are hidden or another project is selected. **ACTIVE** is shown only after a launcher-managed API responds locally; external processes are labeled **ENVIRONMENT UNVERIFIED** until restarted through a configuration button. The database explorer labels saved configuration separately from the API's running state. Production uses the API's real Production behavior: this CRM disables Swagger, Development diagnostics and Power Settings, and enables its secure-cookie/HTTPS rules. Set up local HTTPS when required; an existing `/swagger` UI path will return 404 in Production.

The Release launcher application build passed with zero warnings/errors, and `publish.ps1` produced the updated self-contained `publish/single-file/FullStackLauncher.exe`. Automated tests remain deferred under `AGENTS.md`; live secret editing and API switching were not exercised against real credentials or production data.

Reference: [.NET user-secrets and configuration behavior](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-8.0).

## Developer tools

The developer-tools panel keeps added **pgAdmin 4**, desktop applications, and website shortcuts available across all projects. It starts empty for new users. Manage the list to add, rename, or remove shortcuts; it is saved globally with your launcher settings, including when you remove every shortcut.

The pgAdmin shortcut automatically finds the installed desktop application and opens its own desktop/login UI. It does not rely on a fixed localhost port. Leave the executable override blank for automatic discovery, or browse to `pgAdmin4.exe` for a custom installation. If you use pgAdmin hosted on a server, add a **Website** shortcut with that server's login URL instead. Sign in through pgAdmin as usual; that shortcut is independent of the database explorer's project connections.

Desktop shortcuts accept an existing `.exe` path, relative to the settings folder or using environment variables such as `%LOCALAPPDATA%` and `%ProgramFiles%`. Website shortcuts accept `http://` or `https://` URLs and open in your default browser.

## Project notes and queue preparation

Choose **Notes & queue ↗** on the dashboard's Sections bar to open the notes workspace. It follows the project selected in the dashboard, independently of services, database browsing, and Codex alerts. Project renames preserve notes because ownership uses the profile's stable ID.

Create a bullet note with an optional short task name and a multiline description. When the name is blank, the first nonempty description line supplies it. **Save note** saves locally; **Add to queue** is a separate action for a saved note. Edit, reorder, mark complete/reopen, or delete notes. **Remove from queue** keeps the note, while **Delete note** removes both note and queue membership and retains any previous execution receipts. Marking a note complete disables its queued item; reopening it does not automatically enable that item.

Queue items have their own order and enable/disable control. **Refresh models** starts a temporary hidden Codex App Server child process and loads the installed model and **Thinking level** choices. Each item's selected combination saves immediately, and model discovery errors appear inline. No fixed model list, numeric difficulty, clipboard, or simulated typing is used. Ordinary note capture does not need Codex to be installed or signed in. The documented discovery protocol has been exercised live; WPF model-refresh interaction remains to be checked manually.

**This increment prepares queues; it does not execute them.** Queues are paused and automatic execution is explicitly unavailable. Auto-run, exact predecessor selection, queue pause/stop controls, background tray ownership, recovery, keep-awake, the activity tracker upgrade, and per-task completion notifications are next. The current completion monitor and audio controls continue to operate as described below. See the [upgrade progress and next starting point](Upgrades/ProjectTaskQueue/README.md) and [Codex integration findings](Upgrades/ProjectTaskQueue/EXECUTION_PATH.md).

**Codex connection…** opens a separate window for an explicit bounded connection check. Refresh models, select a model/thinking level, review the fixed prompt, then click **Run connection check**. This creates a diagnostic task using the assigned queue folder and your Codex account; it may consume usage. It requests a short reply without tools or project work and uses read-only/no-network permissions. The work deadline is 90 seconds, followed by bounded interruption and child cleanup. **Stop check** interrupts only this check's exact turn. Closing during activity requests Stop and keeps the window available until a result or unresolved status is recorded; the check does not continue in the tray.

Saved check history retains the frozen intent, exact task/turn IDs, resolved settings, start/terminal evidence, and reported outcome separately from queue attempts. Uncertain attempts are held for manual review, never automatically resent. After checking in Codex that an attempt is no longer running, the checkbox and **Record review and release check hold** permit a new explicit check without changing the old outcome. **Found in intended project** and **Could not find task** retain manual observations only. In the live protocol verification, Codex discovered the task in its general **Tasks** section with `projectId: null`, despite an exact saved-project folder match. This build cannot assign a task to a desktop project.

Unsaved note drafts remain in memory while switching projects. **Save all drafts** saves them together; closing the notes workspace or launcher offers save/discard/cancel. Drafts are not durable until saved. **Reload notes** refreshes saved data and retains drafts after a concurrent edit; review drafts before explicitly saving over newer edits. If another writer deleted a note, its retained draft appears as a recovered draft and saves as a new note. Removing a project resolves its unsaved drafts first; saved notes and receipts remain in storage under the original project ID. Re-adding a profile creates a different ID and does not automatically adopt those old notes.

Notes, prompts, queue configuration, and execution receipts live at `%LOCALAPPDATA%\FullStackLauncher\project-tasks.json`. An explicit `--settings "D:\Work\launcher.json"` uses the isolated `D:\Work\launcher.json.project-tasks.json` store, including when another settings filename lives in the same directory. Personal task data never enters dashboard settings, monitor preferences, or public builds.

The version 2 store loads version 1 through an in-memory migration and writes version 2 on the next normal atomic save, retaining a `.bak` previous version. Older launchers reject version 2 so they cannot drop receipt purpose or evidence. A save-time `.lock` handle and content fingerprints reject competing changes. Malformed/unsupported files are preserved, saving is disabled, and a failed reload retains the last successfully loaded view. The file handle protects individual saves; the future execution coordinator still needs its own lifetime ownership lock and IPC.

A queue's assigned folder is captured when its first item is added. Editing a project folder does not silently retarget the queue: the workspace shows the difference and provides **Use current project folder**, which keeps the queue paused and clears any predecessor assignment. Future execution must revalidate the folder and model before dispatch.

Validation for this increment: `dotnet build .\FullStackLauncher.csproj -c Release` passed with zero warnings and errors. Manual App Server requests confirmed model discovery, one bounded structured completion, an exact interrupted turn, and read-only retrieval after disconnect; see the [live record](Upgrades/ProjectTaskQueue/LIVE_CHECK.md). Actual WPF interaction, application-level Stop/timeout/conflict behavior, active-run recovery, and publishing were not verified. No backlog tasks, automated tests, or harnesses were run.

## Codex completion alerts

New monitors automatically run from an immutable copy in `%LOCALAPPDATA%\FullStackLauncher\monitor-runtime`, so a hidden tray monitor cannot lock the Debug/Release build files or the published EXE. Each application/dependency version gets its own verified copy; starting alerts again forwards to the existing monitor. Watched projects, sound preferences and completion markers remain in the existing monitor settings file. The dashboard itself stays in its normal build folder for debugging: close a running dashboard or stop debugging before rebuilding changed dashboard files.

Choose **Alerts ▾ → Open Codex alerts** in the dashboard to open an independent monitor process. It uses the same executable with `--codex-monitor`. When closing the dashboard with a watcher running, **Close Codex watcher too?** offers **Yes** to close its tray icon and overlay too, **No** to leave it running (the default), or **Cancel** to keep the dashboard open. Existing unsaved-edit and service-stop confirmations still apply. The watcher saves its preferences before exiting; a shutdown failure leaves the dashboard open with a message so you can retry.

Reopening the main dashboard automatically restarts an existing watcher with the current launcher build. It stages the updated runtime first, requests graceful watcher exit, waits for the single-instance lock to be released, then starts the replacement in the tray. Saved watched folders (including an empty list), sound preference, completed markers, and overlay bounds/visibility are restored. In-memory timing estimates restart. If no watcher was running, opening the dashboard does not start one. Restart errors appear in the dashboard without preventing it from opening; use Alerts or the watcher tray menu to recover. This controls only the launcher's watcher through its own local IPC, never the Codex app, its tasks, other dashboards or processes selected by executable name.

Only one watcher runs per Windows session; opening Alerts again brings the existing watcher forward and keeps its watched projects. On first use, the selected dashboard project supplies a default. Use **Add project to watch** and the watched list's **Remove** buttons to manage folders. Removing every folder leaves monitoring empty until you add another; dashboard startup does not restore removed watches.

To start it from source, double-click **launch-codex-monitor.cmd**. The script builds the launcher in Release mode and starts the monitor. The published EXE includes the monitor; no command script or companion file is needed. Open it from the dashboard's Alerts menu, or start it directly:

```powershell
.\FullStackLauncher.exe --codex-monitor
```

Select or enter a folder used by your Codex tasks in **Codex project folder**, then choose **Add project to watch**. Repeat for as many projects as you need. The dropdown combines remembered folder paths and working folders found in local Codex metadata. Watched folders are remembered locally even when no current task mentions them and can differ from the selected launcher profile. Matching considers tasks in each folder and its descendants, linked Git worktrees, and subagents related through their parent task. A chat and its subagents appear under one watched project: overlapping watches prefer the most specific folder containing the root chat, with deterministic matching for linked worktrees. Project labels include full-path tooltips; the detailed task table also exposes the actual working folder.

Watch the workspace where the Codex task was started. For example, a task started in `MyWorkWebsites` is not included in a watch limited to its `CascadeDigitalSolutionsCRM` child folder. When local metadata shows an unwatched containing workspace, the watched row explains the mismatch and offers **Watch workspace**, with the full path in its tooltip. This adds the containing workspace as a separate watch; it can include work across several child projects. Remove the narrower watch if it is no longer needed. Existing watches are never silently broadened, and previously completed history alone does not create popup activity.

The monitor checks every five seconds, reading local metadata once for all watched projects. Adding or removing a watch during a read immediately queues a fresh read for the updated list. Each project tracks completion independently: after observing running or queued work, it waits until that project's monitored work has completed and confirms that state for ten seconds before sounding. With sound enabled, reminders repeat every 60 seconds until dismissed. New activity stops that project's reminder on the next refresh. Merely opening an idle project does not start a reminder.

- **Dismiss reminder** silences the current batch and waits for new work.
- **Enable Sounds** turns automatic sounds on or off and remembers the choice across restarts. Turning it off immediately stops playback while every watched project, completion indicator, and the overlay keeps updating. The same toggle is available in the tray menu. Turning it on resumes eligible reminders on their next scheduled refresh; it does not replay old dismissed work.
- **Refresh** checks status immediately.
- **Hide to tray**, or closing the monitor window, keeps it running in the Windows system tray. Open it again from the tray or the dashboard.
- **Exit monitor** stops the monitor. Its tray menu also provides open, dismiss, Enable Sounds, and exit controls.

### Chat progress overlay

Choose **Alerts ▾ → Show chat progress** in the dashboard, or **Show chat progress** in the alerts window, to display a compact floating list above other desktop windows. The overlay has a transparent background with subtle light backing behind readable chat names, colored dots, and a single **Clear** button. Drag a chat row to move it and use the corner grip to resize it. It starts hidden; its position, size, and visibility are remembered. **Hide chat progress** in the dashboard or alerts window hides it while monitoring continues. Windows secure desktop and exclusive fullscreen applications may cover the overlay.

Each row represents a root chat and includes the progress of its observed subagents, with a project label and a rough percentage on the right. Yellow means running or queued; green means finished. Gray indicates unavailable status and red indicates failure. The list starts with work observed by the monitor rather than filling with old completed history. The alerts window's **Chat progress** tab shows the same list; **All tasks and agents** provides project labels and individual agent estimates beside their statuses.

Percentages marked **~N% est.** are low-confidence elapsed-time guesses, not measured work completion or a guaranteed finish time. A run starts with a 10-minute assumption; after at least three completed runs have been observed in that project during the current monitor session, new runs use the median of up to 20 observed durations. Each run keeps its original baseline. Time before the monitor first saw the run is unknown, waiting intervals are excluded, and long observation gaps restart timing. Running estimates stop at 95%; only an explicit completed status shows 100%, with chat rows also requiring completion confirmation. A chat uses its least advanced observed member, so new subagents can lower the estimate. Waiting, failed, missing, or unavailable members suppress the chat estimate. Hover an estimate for its basis. These estimates never trigger completion alerts and are not saved to disk.

Green rows remain through hiding, showing, and restarting the monitor. Use the overlay's **Clear** button to clear all finished rows, or **Clear** beside an individual green row in the alerts window to remove just that row. **Clear finished chats** is also available in the alerts window and dashboard's **Alerts ▾** menu. New activity in a chat replaces its green status. **Dismiss reminder** silences the sound without clearing finished chats.

No API key is needed. Keep Codex open while monitoring. This is an experimental integration with Codex's internal local status format, not a supported public status API. It reads `state_5.sqlite`, `thread_history_1.sqlite`, and `queue_1.sqlite` in read-only mode, with a bounded fallback to local lifecycle records. It does not monitor remote or cloud tasks. Missing, unreadable, unknown, failed, or interrupted state suspends completion alerts; an app crash is not inferred to mean successful completion. A Codex update that changes these internal formats may require updating the monitor.

Monitor preferences, watched-folder history, the watched-project list, and the reminder sound choice are stored separately from dashboard settings at `%LOCALAPPDATA%\FullStackLauncher\codex-monitor.settings.json`. An older saved single-folder selection migrates into the watch list. Retained completion markers store only task/turn IDs, project, and completion time; chat titles and messages are not saved. Moving or updating the EXE preserves this file. On a newly started monitor process, `--project` adds a watched folder alongside saved watches; other optional arguments choose a different Codex data directory or a diagnostics file:

```powershell
.\FullStackLauncher.exe --codex-monitor --project "D:\My projects\Workspace" --codex-home "D:\Codex data" --diagnostics-file "D:\Diagnostics\codex-monitor.json"
```

Use full paths for `--project` and `--diagnostics-file`. Without `--codex-home`, the monitor uses `CODEX_HOME` when set, otherwise the current user's `.codex` folder. Diagnostics contain counts and status only; they do not contain task IDs, titles, or messages. The same optional arguments can be passed to `launch-codex-monitor.cmd`.

Control the overlay from a shortcut or command line with `--overlay-command show`, `hide`, or `clear`. Commands are delivered to the existing monitor and keep its selected project:

```powershell
.\FullStackLauncher.exe --codex-monitor --overlay-command show
.\FullStackLauncher.exe --codex-monitor --overlay-command hide
.\FullStackLauncher.exe --codex-monitor --overlay-command clear
.\FullStackLauncher.exe --codex-monitor --exit-monitor
```

The final command gracefully exits the monitor.

## PostgreSQL database explorer

The **Database explorer** panel below the service cards finds PostgreSQL connections in the .NET service folders of all saved projects. It reads each `.csproj`'s `UserSecretsId` and combines the appropriate appsettings files with protected launcher overrides when an encrypted profile exists. Until then, unmanaged discovery can read the external legacy .NET user-secrets. Managed profiles never fall back to those files. Inherited `ConnectionStrings__Name` variables apply only to unmanaged discovery. These saved sources are labeled separately from the running API's configuration. Environment connections are also available independently. It does not evaluate MSBuild, custom configuration providers, launch-profile overrides, or pgAdmin's saved credentials. Add a project with its API service folder to make that project's connections available.

1. Select a **Connection from your projects**. The selected project's connection loads automatically on first use; the connected server is shown beside the database selector.
2. Pick an **Existing database** from that server. The list excludes template databases and databases for which your role lacks `CONNECT`. The selected connection and database name are remembered separately for each launcher project. This selection only controls the launcher explorer; it does not change the API's configuration or run migrations.
3. Pick a table or view to see column names, PostgreSQL types (including lengths/precision), nullability, defaults/generated expressions, identity generation, constraints (including primary/foreign keys), and comments. Use the filter for schema/table names. Both result grids have separated columns, alternating rows, resizable/reorderable headers, and individual cell selection. Scroll horizontally for additional fields; use the details panels for full, selectable text.
4. Choose **Top 10 rows** to query actual data for the selected table. Changing tables while this tab is active loads that table's preview; **Query** refreshes it. Previews return at most ten rows, ordered by the primary key when present. Tables and views without a primary key show an explicitly unordered sample. Results respect the connected PostgreSQL role's permissions and row-level security. Empty results are shown clearly.
5. Choose **Relationships** to explore the selected table's incoming and outgoing foreign keys. Each connection shows the referencing table, referenced table, paired columns, cardinality, and update/delete rules. Open a related table from its relationship card to continue exploring. Composite keys remain together, self-references are identified, and full constraint definitions are available to copy.

The **Top 10 rows** copy controls and right-click menu let you copy a cell, its row, or the loaded results. **Ctrl+C** copies selected cells in either grid. Row/result copies include headers in the current column order and use tab-separated text for pasting into a spreadsheet. SQL NULL is copied as `\N`; empty strings stay empty when copied individually and are quoted in TSV. Tabs, newlines, and quotes are escaped in TSV, and copied values retain any preview truncation marker. Copying is explicit, and preview data remains in memory.

The table navigator can be resized or hidden to give results more room, then restored from the explorer's controls. Details panels can also be collapsed; when expanded in a short panel, they scroll below the result grid so both stay reachable. A successful relationship lookup with no results shows **No relationships yet** inline. Connection, permission, and other lookup failures show an inline error instead; they are not treated as empty results. Unexpected UI errors use the launcher's status area rather than opening repeated error dialogs.

Relationships come from PostgreSQL's declared foreign-key constraints, not guessed column-name matches. Only connections between tables visible to your role are shown. A table with no visible foreign keys gets an explanatory empty state; application-only relationships and view dependencies are not inferred. Cardinality describes schema rules rather than current row counts: a unique referencing key indicates one-to-one, other referencing keys indicate many-to-one, and nullable foreign keys can make the parent optional. Unvalidated/unenforced rules are identified, and temporal foreign keys use a coverage label. Many-to-many designs can be followed through the two foreign keys of their junction table. The reader uses the PostgreSQL catalogs for [constraint definitions](https://www.postgresql.org/docs/current/catalog-pg-constraint.html) and [unique index metadata](https://www.postgresql.org/docs/current/catalog-pg-index.html).

After connecting, the selectors collapse into a compact database/server summary. **Connection…** reveals them again, and **Reload connections** reloads development configuration and the server's database list. **Refresh** reloads tables and the active columns, data, or relationship view. If credentials change, reload connections; inherited environment changes require restarting the launcher. **Cancel** stops pending work. Requests have bounded timeouts, and switching projects, connections, or tables cancels the previous request so old results cannot replace the current selection.

The explorer uses read-only transactions for schema discovery and row previews. It displays user schemas and objects visible to the connected role, including views, materialized views, partitions, and foreign tables. It never creates/alters databases or accepts arbitrary SQL. Preview identifiers are quoted, and cells use PostgreSQL text representations for arrays, JSON, binary and extension types. Long values are capped at 4,096 characters with a truncation marker; NULL cells show `∅` and empty strings show `""`. Preview results remain in memory and are cleared when switching tables/connections; they are never written to launcher settings or service logs. A database with no accessible objects shows an empty state; restricted database-list access falls back to browsing the configured database. A missing saved connection/database requires selecting an available one.

Credentials from configured projects are read in memory from their existing source. **Add connection… → Connect & save** accepts a masked Npgsql connection string, verifies PostgreSQL sign-in without changing database data, then saves it for later launcher sessions. Saved connections are available throughout the explorer, comparison, and account tools. They retain their stable source IDs so a project's selected connection can be restored after restart. Re-entering an identical connection with the same label reuses its saved entry.

The saved connection library is encrypted with Windows DPAPI for the current Windows user at `%LOCALAPPDATA%\FullStackLauncher\database-connections.dat`, separate from launcher settings, source configuration, and public builds. A nondefault `--settings` path uses an independent library under `%LOCALAPPDATA%\FullStackLauncher\database-connections\<settings-path-hash>\`. Saves use a writer lock, an atomic replacement, and an encrypted previous-version `.bak`; unreadable or malformed libraries are preserved with visible feedback. Copying the EXE does not copy credentials to another user or computer. Remote connections always use SSL Mode=VerifyFull, including existing saved connections. The server certificate and hostname must validate; configure a trusted Root Certificate for private CAs. Loopback development connections retain their TLS choice. Saved connection values stay out of launcher settings and logs; generic table previews may still display sensitive database columns. Save API overrides through the encrypted configuration editor and use an appropriate secret store for deployed APIs.

## Manage database accounts

Open **Database → Accounts…**, beside **Compare & migrate…**. Select a Local, Prod, or saved connection and its exact database, then **Connect / refresh accounts**. The launcher connects directly to PostgreSQL and detects the supported CRM or SimNow account tables. No API project, running API, or installed .NET SDK is required. The target and detected account type remain visible above the account list. Local/Prod labels describe the saved configuration; always check the actual host and database. **Add connection… → Connect & save** makes a successfully verified connection available after restarting the launcher.

The account table includes administrators and inactive/locked users. Select a row to set its password or remove the account; use **New account** to create a login. These are application users, not PostgreSQL server login roles. Search uses the fields available in the connected application's schema. Passwords are masked during entry and never shown in the account table. Share initial credentials privately; these actions send no email.

For **CRM**, enter a username, email and initial password. Create a Customer assigned to an active project or an Administrator with access to every project. Passwords follow the CRM's 12-character uppercase/lowercase/number/symbol rule and use compatible ASP.NET Core Identity hashes. **Set password** clears lockout and invalidates existing sessions and invitation/reset links. **Remove account** removes a customer, unbilled work and account access data while retaining billed work and payment history, detaching/anonymizing customer identity references and disabling manual recurring billing. Every CRM Administrator remains protected. Accounts with active, pending or unresolved Stripe billing must be removed through the administrator portal, where Stripe cleanup can complete.

For **SimNow**, enter a username, display name, initial password and an available Admin or Member role. There is no email or assigned-project field in this schema. Usernames are stored lowercase. The launcher requires at least 12 password characters with uppercase, lowercase, a number and a symbol. Passwords use SimNow's existing `PBKDF2$210000$<salt>$<hash>` format: PBKDF2-HMAC-SHA256 with a random 16-byte salt and a 32-byte hash, both Base64 encoded. Setting a password clears failed-login attempts and lockout. Administrator accounts are protected from deletion; removing a member deletes its role assignments. Existing SimNow sign-in tokens remain valid until their normal expiry; its schema has no stored sessions for this tool to revoke. SimNow can recreate its configured seed administrator at API startup if that account is deleted outside this tool.

Confirm every change by typing the displayed database target; changes to an existing account also require its current username. The launcher rechecks the account and deletion eligibility in the transaction. Interrupted or uncertain changes require reconnecting and inspecting the account before another attempt.

Database credentials provide maintenance authority and must have the required database permissions. Supported account tables must already exist; unknown or incompatible schemas produce a clear error. The workspace uses Npgsql directly inside the launcher and does not start the web host, email/billing workers or migrations. Passwords and account rows remain in memory and are not saved in launcher settings/logs. No production changes are performed merely by building/publishing this feature. Use the main **Database** explorer to inspect other tables and preview their rows.

## Compare databases and apply migrations

Open **Database → Compare & migrate…**. Choose a source/reference database and a target/deployment database independently. Each side shows its host and accepts an exact database name or **List databases**. **Swap source / target** reverses the comparison. The selected project's API migration project is discovered automatically when it has the supported offline design-time factory.

The connection list includes explicit **Local configuration** and **Prod configuration** sources alongside the explorer's effective source and encrypted saved connections. These are locally saved configurations: Local can still point to a remote server, and Prod is not a live fetch of Azure application settings. Production sources use base/Production appsettings and the separate Prod secrets store; they never inherit Local user-secrets. Managed profiles do not inherit connection-string environment overrides. Changing these selectors does not switch or restart the API. **Add connection…** verifies and saves a masked Npgsql connection string, makes it available to both selectors, and keeps it across reloads and launcher restarts. Credentials stay in the separate Windows-user encrypted library; comparison results and plans remain in memory.

1. Click **Compare schemas**. PostgreSQL 12+ catalog reads use independent read-only, repeatable-read snapshots. Results identify **Only in source**, **Only in target**, and **Changed** objects; select a row for both full definitions. Search by object type/schema/name or filter by difference type. Renamed objects appear as separate missing/extra objects. Selecting the same host/database on both sides produces a notice.
2. Use **Migrations & SQL** to see source/target EF history. The **Source / target history** selector keeps this comparison available after preparing a repository plan. Missing standard history means no recorded IDs were found; ambiguous or unreadable history stays unavailable. Matching migration IDs do not prove that schemas match.
3. Select the CRM API `.csproj` and click **Prepare migration plan**. It checks the repository EF tool, restores it when missing, builds the API in a separate `LauncherMigrations` output configuration, lists migrations without connecting the API, and generates exact forward SQL from the target's applied tip. Target credentials never enter subprocess arguments or generated SQL. The included `ApplicationDbContextDesignTimeFactory` handles the launcher's offline metadata mode without starting the web host or background workers; ordinary EF/configuration scripts retain normal configuration precedence.
4. Review pending migration IDs, the full SQL, and the displayed target. To execute, acknowledge SQL review/recovery readiness and type the exact displayed host/database, then click **Apply reviewed migrations**. This tool applies existing repository migrations; it does not generate synchronization SQL from differences, create missing migration files, or downgrade a database.
5. The tool rechecks the SQL hash, project inputs, target identity, migration history, and covered schema before execution. A database advisory lock coordinates launcher deployments, and an exclusive history-table lock coordinates existing EF history writers. Reviewed SQL runs in one transaction with final history verification before commit. Changing a selector/project invalidates the plan; reviews expire after 30 minutes, and every attempted application retires its plan. After a confirmed commit, the tool refreshes the comparison when both endpoints are selected. Review remaining differences rather than assuming all environments should have identical owners/grants.

Application is limited to the CRM's supported `ApplicationDbContext` factory and one standard `public.__EFMigrationsHistory` table. Unknown/out-of-order/gapped history, unsupported history shapes or RLS, nonempty unbaselined public tables, replicas, and SQL requiring separate transactions are blocked. Scripts with transaction/session control or operations such as concurrent index creation can be exported for a separate reviewed deployment process. Other PostgreSQL databases remain available for comparison. A commit transport failure is reported as an unknown outcome; refresh history/schema and prepare again before deciding on a retry. Closing while busy keeps the window open so its outcome can be shown. These locks do not prevent all unrelated manual database administration; arrange deployments accordingly.

**Coverage & results** lists the scope and sanitized, in-memory operation results. Comparison includes schemas, tables/partitions, columns/defaults/generation, keys/checks/exclusions, indexes, views/materialized views, sequence definitions, routines, enum/domain/composite/range types, collations, extensions, triggers/rules, RLS policies, owners/grants/default privileges, comments, and database encoding/collation. It excludes table contents, current sequence counters, server/cloud configuration, roles/memberships and other advanced objects identified in the coverage text. Extension-owned implementation objects are represented by extension name/version/schema. Cross-version formatting and environment-specific permissions can cause intentional differences.

**Export comparison…** writes a JSON report containing identities, definitions, coverage and migration IDs. **Export SQL…** writes the exact generated SQL, without the launcher's transaction wrapper/rechecks. Exports are explicit; definitions may include routine bodies/comments. Connection strings and table rows are not exported.

Verification for this addition: launcher Release compilation passed with zero warnings/errors using `dotnet build FullStackLauncher.csproj -c Release --no-restore -p:UseAppHost=false -o bin/DatabaseToolsReview` because the normal apphost was in use. The API application build and offline EF listing/script generation passed; 16 repository migrations were listed and a forward SQL script generated without connecting to a database. Automated tests remain deferred. Actual WPF interaction, live catalog capture and migration execution against a target remain unverified; no production migrations were applied during implementation.

## Status and process ownership

The **Sections** bar independently shows or hides Projects, Tools, Services, Console, and Database. Hiding a section gives its space to the remaining panels; the Sections bar remains available to restore it. **Reset layout** restores the default panels and split sizes. Visibility and split proportions are saved in your local launcher settings. Individual service cards can also collapse to their name and status.

Every service has a console beneath its card, separate from the collapsible service details. Start, Restart, Clean, Setup, Local/Prod switching, conflict recovery, and Stop open the affected service's console automatically. **Start all** opens a console for each service it starts. New errors reopen that service's output. The combined console respects your saved visibility choice: service actions (including batch actions), command output, and errors do not open it automatically. Use **Sections → Console** to show or hide it. **Console output** on each card hides or restores that service's output without stopping capture.

The terminals use a dark background with mint accents, cyan commands, light ordinary output, mint success, amber warnings, coral errors, and lavender information. Timestamps and explicit labels distinguish the categories without relying on color. Compiler warnings and errors are classified from their content; stderr alone is not a failure. **Follow output** follows the latest line; scrolling up pauses it so you can inspect and select earlier text. **Copy all**, Ctrl+C on selected text, and **Clear** are available in each console. Each service retains 600 entries, while the combined console retains 1,000 entries for the selected project; Clear affects only that view. Output stays in memory and is not saved with settings. Existing API secret redaction applies before display.

The combined console remains beside service controls above the database explorer. Drag the vertical divider to resize it, and the horizontal divider to adjust services/console height versus the database. Hiding sections leaves processes running and capture active. Output comes from commands started by this launcher; it cannot attach to an external process's existing terminal output. Database migration and Codex diagnostic workspaces retain their own sanitized operation histories.

The dashboard refreshes process and local listening-port status automatically and captures command output. A started process is not necessarily ready to serve requests; use the service's status and URL together. Local URLs printed by a managed service can update the displayed URL, including when a development server selects another port. The UI path, such as `/swagger`, is applied when opening the browser.

Only `http://` and `https://` loopback URLs are accepted in project settings. A listener already using an expected port is reported separately from a process started by this launcher. The launcher does not claim ownership of every `dotnet` or `node` process on the machine.

**Force stop** terminates the service's process trees. **Restart** performs that stop followed by the start command. These explicit actions can also stop an externally started service when its executable or command line can be tied to the configured working folder. When a verified external process owns exactly one listening port, the launcher can use that port for its URL.

When a service shows **Conflict**, its card offers **Resolve conflict & restart**. The confirmation lists the names and PIDs of the blocking processes and their current children. Confirming force stops those processes plus this service's matching processes, waits for the port to clear, and runs this service's start command. Unsaved work in the listed processes may be lost. Recovery checks process start times to avoid stopping a reused PID, protects the launcher and its ancestors, and requires another review if a replacement process takes the port. It never stops all Node or .NET processes by name. If Windows denies access or a supervisor respawns the listener, the service shows an error/conflict so you can stop it in its original application and retry. Ordinary Restart all and launcher shutdown do not clear unverified conflicts.

**Force stop all** also includes services whose ports are blocked even when the service itself is stopped. It stops normal service trees first, then reviews conflicting processes one service at a time with the same identity safeguards, leaving approved services stopped. Canceling a conflict review preserves that blocker and reports that attention is still needed. New service commands are disabled during the batch. A stopped service with a genuinely occupied port explicitly says the service is stopped and identifies the blocking PID; an exited listener or a released old detected port does not remain a conflict.

Closing the launcher asks to stop the commands it started, then cleans up those owned processes. Services started elsewhere remain running when the launcher closes. Cancel the close prompt to keep the launcher and its commands running.

## Saved projects and local storage

`%LOCALAPPDATA%\FullStackLauncher\launcher.settings.json` holds all project/service profiles, the selected project, developer-tool shortcuts, database source selections, and workspace section visibility/split proportions. Source builds and published copies share this store by default. Add, edit, or remove projects in the dashboard; an intentionally empty list stays empty. Existing settings without layout preferences use the default layout.

When the local settings file does not exist, the launcher creates an empty project and shortcut library. It does not import source-folder, adjacent, or embedded profiles. Existing local settings remain in use, so updating the app on your own account preserves your setup; a new user's account gets a blank slate. Old portable files remain untouched and can be used only through an explicit `--settings` choice.

Project root paths are relative to the **settings file's folder** unless absolute. Service working folders are relative to their **project root** unless absolute. The folder browsers save relative paths whenever the folders are on the same drive. Moving only the EXE does not affect these paths. If you move a repository, update its project root in the editor. Existing folders are required when saving a project.

Choose another settings file explicitly when needed:

```powershell
.\FullStackLauncher.exe --settings "D:\My projects\launcher.settings.json"
```

An explicit `--settings` path uses only that file. A missing explicit file starts empty, and saving creates it. Saves replace the file atomically and retain its previous version as `launcher.settings.json.bak`. Malformed settings are preserved, with an additional timestamped recovery copy when possible; the app displays a warning and disables saving instead of overwriting them. Fix the file and restart, or launch with another `--settings` file. If another launcher copy or program changes valid settings while the launcher is open, restart to reload them before saving.

Project profiles and notes are ordinary local data, not credential stores. Commands containing recognizable literal passwords, tokens, or credential URLs are rejected before saving/starting; use encrypted API configuration or the launched application's credential provider. Console output is filtered for known API values and common credential formats before display/copy, but arbitrary applications must still avoid printing secrets. Database row previews and schema exports are deliberate access to database content and must be reviewed before sharing.

## Build a single-file EXE

From this folder, run:

```powershell
.\publish.ps1
```

The script publishes exactly one file, `publish\single-file\FullStackLauncher.exe`, as a self-contained Windows x64 app. For Windows ARM64, use `./publish.ps1 -Runtime win-arm64`. Publishing may download runtime packs from NuGet. For a smaller EXE that requires the .NET 10 Windows Desktop Runtime on the destination, use `./publish.ps1 -FrameworkDependent`. Native runtime libraries are bundled and extracted by .NET when the EXE runs; no companion distribution files are required.

Publishing never reads or embeds source settings, local saved settings, credentials, notes, or project profiles. The old `-SettingsPath` publishing option and embedded seed hook have been removed. Every newly published build initializes an empty library for a new Windows user. Publishing does not change your existing local library.

Copy just **FullStackLauncher.exe** to your desktop or another location and open it. The dashboard and Codex watcher both run from this file and keep their local settings through subsequent EXE replacements. On another Windows account or machine, add your own projects and provide their development tools and credentials. Older EXEs already produced with personal embedded profiles are not retroactively sanitized; distribute a newly published build.

Republishing can replace an EXE in the same single-file output folder. Alternative output folders must remain inside this source folder. The script rejects folders containing other files, preserving older `portable` folders and their saved profiles. Use a new empty output folder when needed, then copy the resulting EXE elsewhere.

## Extending it

Project profiles live in `Models/LauncherSettings.cs`; settings loading and validation are in `Services/SettingsStore.cs`; process control is in the service layer; WPF windows provide the dashboard and editor. Add services through saved profiles before adding code. New integrations can build on the same process, log, and status model.

## Verification

Credential/public-default update (September 13, 2026): focused Release compilation completed with zero warnings/errors, and the final self-contained single-file EXE published successfully after retrying NuGet with normal network access. Read-only inspection of the new EXE and managed assembly found no embedded initial-settings resource marker, personal project-root matches, or distinctive configured API-secret matches. MSBuild evaluation confirmed that managed API launch options isolate artifacts and disable the generated UserSecrets attribute without changing ordinary build paths. Existing user settings and external credential files were not changed. Live WPF editing, actual encrypted-profile imports/saves, and database connectivity were not exercised; automated suites remain deferred.

Automated testing is deferred during rapid development under the repository's `AGENTS.md` policy. The commands below are reference material for later use, not routine development checks. Do not create, build, run, or maintain test harnesses, test servers, UI test scripts, or fixtures until the user explicitly requests testing or declares a stable version and asks for testing to begin.

The settings checks are a standalone console harness using the real models and settings store, with no test-framework NuGet packages:

```powershell
dotnet run --project .\Tests\SettingsChecks\SettingsChecks.csproj --configuration Release --no-launch-profile
```

Its 13 checks cover saved projects and commands, relative and absolute paths, command-line paths containing spaces, creation and backups, corrupt-file protection, concurrent edits, overlapping-folder rejection, local URL validation, and selected-project fallback. Fixtures stay in an isolated temporary folder; the harness does not load or modify the launcher's real settings.

Exercise the actual WPF Start, Restart, Force stop, and setup-cancellation controls with isolated local HTTP fixtures:

```powershell
.\Tests\UiSmoke\run.ps1
```

This script builds its test server, creates dedicated folders and settings under `artifacts/ui-fixture`, and verifies that another service can start and remain monitored while a setup command is active. It also checks WPF binding errors. It does not start the CRM API or Angular application.

For a dashboard/editor rendering check using the CRM profile without starting its services:

```powershell
dotnet run --project .\Tests\UiSmoke\UiSmoke.csproj -c Release -- --settings "$((Resolve-Path .\launcher.settings.json).Path)"
```
