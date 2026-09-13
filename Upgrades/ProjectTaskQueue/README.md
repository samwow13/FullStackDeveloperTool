# Project notes, Codex task queue, and task activity tracker

Implementation handoff updated September 12, 2026 after the second increment: live protocol verification and a WPF connection-check workflow. Read the progress section below and the applicable repository instructions before continuing. The remaining sections retain the full target specification; they do not imply that every feature is available yet. No earlier conversation is required.

## Current progress and next starting point

**Stopped after the Step 1 execution adapter and connection-check UI, with live discovery, completion, and interruption verified through manually entered protocol requests.** Step 2 and the editing portion of Step 3 remain implemented. **Automatic queue execution is still unavailable.** The observed desktop association limitation is now concrete: a task created with the saved desktop project's exact folder appeared in the general Tasks section with `projectId: null`, rather than being assigned to that project.

Open **Notes & queue ↗** on the dashboard's Sections bar. Its separate workspace follows the selected dashboard project. It provides bullet notes with names and multiline prompts, explicit save, completion/reopen, deletion, independent note/queue ordering, explicit queue membership, enable/disable per item, and discovered per-item model/thinking-level selectors. Saving or queueing notes does not run Codex. New queue configurations are paused.

Open **Codex connection…** in the notes workspace to refresh models and explicitly run a separate fixed-prompt connection check. It freezes the assigned folder/model/effort, saves intent before submission, retains exact task/turn identities and resolved settings, offers **Stop check**, and records manual desktop-placement observations. It does not submit note content, enable queues, or continue in the tray after closing. An unfinished/uncertain check is held for review and never resent automatically.

Notes are stored separately by stable project ID, with atomic replacement/backups, a save-time writer lock, and concurrent-edit/corrupt-file protection. Unsaved drafts survive project switches; reload preserves them. The task store is now **version 2**, reading version 1 through an in-memory migration and writing version 2 on the next normal atomic save. Receipts distinguish `QueueItem` from `ConnectionCheck`; old launchers fail closed on version 2. Manual holds can be released only by explicitly recording that the attempt is no longer running in Codex; this does not change the original outcome or enable execution.

| Step | Delivered / remaining |
| --- | --- |
| 1 — Execution path | Reusable owned, hidden App Server transport and bounded connection-check service implemented. Live manual handshake/catalog, exact thread/turn start, structured completion, and exact interruption passed. Desktop discovery works, but saved-project association was absent. See [execution findings](EXECUTION_PATH.md) and [live record](LIVE_CHECK.md). |
| 2 — Durable data | Version 2 with version 1 migration, isolated overrides, immutable receipt purpose/snapshot/IDs, diagnostic evidence, and receipt/notification preservation safeguards implemented. |
| 3 — WPF panel | Notes/queue preparation plus connection-check window implemented. Auto-run, Pause queue, Pause all queues, Stop current queued task, and exact predecessor selection remain for the coordinator increment. The new Stop check control applies only to a diagnostic check. |
| 4 — Runner/coordinator | Not implemented. The new transport can be reused, but the reply-only diagnostic service is not a general work runner. |
| 5 — Activity tracker | Not implemented. Existing completion monitor, audio, and overlay remain unchanged. |
| 6 — Verification/docs | Focused Release application build passed with zero warnings/errors. Manual protocol checks recorded. Actual WPF interaction, application-level Stop/timeout/conflict paths, active-run recovery, approvals/child work, publishing, and notification/power behavior were not exercised. No automated tests or harnesses were added or run. |

**Up next, in order:**

1. Manually exercise **Notes & queue → Codex connection** in WPF, including model refresh, bounded check, Stop, reopening history, and note-draft preservation. Resolve a documented way to assign desktop projects, or agree an honest product behavior for general Tasks placement. Do not force association with private APIs or database writes. Verify project instruction loading and supported exact-turn reconciliation after a connection loss before dispatching real work.
2. Implement Step 4: independent tray owner and versioned IPC carrying the resolved task-store identity, general execution adapter, exact predecessor selection/sequencing, checkout exclusion, pause/stop, recovery, and shared keep-awake ownership. Serialize owner mutations and dispatch so background receipt writes cannot race stale editor saves. A new JSON-RPC request ID is not idempotency. Exclude `ConnectionCheck` receipts from queue scheduling, queue completion history, and task notifications.
3. Implement Step 5: shared Recently completed / Starting task / Running activity, retained completion messages, and deduplicated per-task notification attempts across all tracker surfaces.

The diagnostic folder mutex is scoped to this check feature; it is not the future coordinator owner. The check never resumes an uncertain run or claims recovered success. Its fixed prompt requests no tools, but the stable API has no universal tools-off switch: it uses read-only/no-network permissions, refuses server requests, and stops on observed tool/child work. General execution will need proper approval/input handling and child aggregation. Never run prepared notes automatically to verify integration.

Current implementation files: `ProjectTasks/ProjectTaskModels.cs`, `ProjectTaskStore.cs`, `CodexAppServerConnection.cs`, `CodexModelCatalog.cs`, `CodexConnectionCheck.cs`, `CodexConnectionViewModel.cs`, `CodexConnectionWindow.xaml` and code-behind, `ProjectTasksViewModel.cs`, `ProjectTasksPanel.xaml` and code-behind, `ProjectTasksWindow.xaml` and code-behind, plus the earlier dashboard integration in `MainWindow`.

## 1. User goal and scope

Extend the existing FullStackLauncher WPF application with project-specific notes that become an editable bullet list and, when explicitly queued, prompts for Codex. Run queued items sequentially in the assigned project with a selected model and thinking level. Let the user enable or disable automatic advancement at any time.

Update the existing completion tracker into a task activity display centered on **Recently completed: [task name]** and **Starting task: [task name]**. The user should see what just finished and which task the queue is actually starting.

Submit work in the background without activating the Codex window, simulating typing, changing the clipboard, or stealing focus. The intended result is a fresh Codex task for each queued item, associated with the correct project. Exact desktop sidebar association remains an integration question to validate first.

While auto-queue is working, keep the computer awake so idle sleep does not interrupt execution. Still deliver a user-facing completion message at the end of every task, even when the next queued task starts immediately.

The scope is the launcher. Preserve its service controls, database explorer, project settings, developer tools, single-file distribution, and existing user data. Do not change the CRM API, frontend, or database for this feature.

## 2. Read first and reconcile existing conventions

- Read the CRM root `AGENTS.md`, `FullStackLauncher/AGENTS.md`, and the launcher's existing `README.md`.
- The repository defers automated testing. Do not create, modify, build, or run test projects, test harnesses, UI automation scripts, or test infrastructure. Use a focused application build and appropriate manual verification during implementation. This documentation-only handoff requires neither.
- The current monitor must keep its access to Codex metadata read-only. Do not write Codex SQLite databases, edit its stored sessions directly, inspect credentials, call private agent tools, or start/stop/reconfigure the existing Codex daemon.
- The requested queue is a new, explicit execution feature. Implement it through a separate adapter to a documented Codex interface. A hidden, application-owned Codex child process for an enabled queue is distinct from managing the existing desktop daemon. Keep passive monitoring and active execution separate.
- The user's new task-name/status requirement intentionally changes the old overlay convention that allowed only names, dots, and Clear. Show the requested activity labels while preserving the compact transparent appearance, readability, dragging, resizing, remembered bounds/visibility, and accessible Clear controls. Update the relevant `AGENTS.md` wording when implementing this behavior.
- Keep existing independent audio controls. Pausing audio, hiding the overlay, clearing activity history, and pausing queue advancement must remain separate actions.

## 3. Required user experience

### Project notes and queue

1. Selecting a launcher project loads that project's notes and queue.
2. Add a note with a short task name and a multiline description/prompt. Display notes as bullets with editable details. A first nonempty line can supply the initial task name.
3. Support edit, delete, mark complete, and reorder. Save changes locally and preserve them across restarts and project renames.
4. Saving a note alone does not run Codex. Provide an explicit **Add to queue** action. **Remove from queue** retains the note; **Delete note** removes the note. Keep previous execution receipts independent of note deletion.
5. Each queued item has its own model and **Thinking level** selection. Treat thinking level as Codex reasoning effort; do not present an unsupported numeric difficulty parameter. Discover valid model/effort combinations from the installed Codex interface and validate again before launch.
6. Show queue order, per-item state, and project-level **Auto-run** / **Pause queue** controls. New queues default to disabled. Include a global **Pause all queues** action.
7. Allow an enabled queue to wait for a specifically selected existing Codex task to finish, then launch its first item. Subsequent items wait for the exact task dispatched for their predecessor. Merely enabling a queue must not choose an arbitrary existing task as its dependency.
8. If there is no predecessor, make the initial-start behavior clear in the UI: enabling execution can start the first eligible item. Queue edits and project selection changes never enable execution on their own.
9. **Pause queue** prevents future dispatches and lets the current task finish. **Stop current task** is a separate interrupt action. Disabling an item skips it during selection of the next eligible item.
10. Changes to a running item's note/model affect future execution only. Preserve the actual dispatched prompt, model, effort, and folder in its execution receipt. Deleting or removing a running item must not silently terminate or lose tracking of that run.

### Updated tracker: recently completed and starting task

Use the same activity source for the dashboard, alerts window, and floating tracker where applicable. Show task names instead of relying on colored dots alone.

| Situation | Required display/behavior |
| --- | --- |
| A tracked run finishes successfully | `Recently completed: Add project notes` with its completion time in the detailed view. |
| The next queue item is claimed and dispatch begins | `Starting task: Add queue controls`. Show only during actual startup, not while merely queued. |
| Codex acknowledges the turn has started | Replace Starting with `Running: Add queue controls`; retain the recently completed entry. |
| Waiting on a selected existing task | `Waiting for: [task name]`; do not claim the queued item is starting. |
| Queue is disabled | `Queue paused`; an optional `Next: [task name]` is a preview, not a startup status. |
| Model/transport/startup fails | `Could not start: [task name]` with a useful inline reason; pause advancement. |
| Approval, clarification, or blocker needs user action | `Needs attention: [task name]`; stop automatic advancement and make the request accessible. |
| Status is unavailable or recovery is unresolved | Show unavailable/recovering status; never invent completion or launch another item. |
| The queue is exhausted | `Queue complete`; retain recently completed history. |

Example during a handoff:

```text
Recently completed: Add project notes
Starting task: Add queue controls
```

Once startup is acknowledged:

```text
Recently completed: Add project notes
Running: Add queue controls
```

Keep recent completions ordered newest first and make the most recent completion prominent. Preserve the existing behavior that observed completion entries survive hiding/showing and restart until explicitly cleared. Do not import all historical completed Codex tasks when the tracker opens. Clear removes displayed completion history, not notes, queued work, execution receipts, or active status.

For launcher-owned runs, use the task-name snapshot from the run receipt so the tracker stays understandable after a note is edited or deleted. Store that snapshot in the new task store. For externally observed tasks, retain the monitor's minimal persisted identifiers and resolve titles from available metadata; do not add external task titles or messages to monitor preferences or diagnostics. If a title cannot be resolved, use an honest fallback label.

Include project identity when activity from multiple projects is visible. Changing the visible project or watched folder must not retarget an already armed queue. Root-task status must account for relevant child work; a subagent finishing alone must not release the next item.

### Keep awake during automatic work

- Acquire a temporary Windows system-awake request while the queue starts/runs tasks or waits for an actively running selected predecessor. Keep it held across automatic handoffs between tasks, including while the dashboard is closed and the queue owner runs in the tray.
- Prevent idle system sleep; the display may turn off and the user may lock the computer. Do not change the user's permanent power plan, simulate input, or override an explicit user sleep/shutdown action.
- Release the request when the queue finishes, or is paused/stopped with no tracked task still running. Pausing future dispatch while the current task continues must keep that task protected until it ends. An empty enabled queue alone must not keep the machine awake.
- Coordinate requests across queues: one queue finishing must not release protection needed by another. Clean up on owner exit and error paths; after restart, reconcile actual active work before reacquiring. Do not persist a flag that falsely claims a live power request exists.
- Show whether keep-awake is active. If it cannot be established, show a useful inline warning rather than claiming the computer is protected from idle sleep.

### Message after every task completion

- For every newly confirmed task completion, show a local desktop/tray notification and retain its completion message in the app's activity history. Include the project, task name, completion time, and a short result summary when available; keep the agent's final response accessible from the run details.
- Notify for each task, not only when the entire queue becomes idle or empty. Starting the next item immediately must not suppress, overwrite, or cancel the previous task's message. Deliver the final item's message as well as showing Queue complete.
- Use the recorded run result to compose the notification; no additional model prompt or external messaging service is required. Show next-task information only when its actual state is known. Failures, interruptions, and blockers need accurately labeled messages, never a successful-completion label.
- Persist notification handling with the execution receipt and deduplicate by run identity. Repeated polls, reconnection, restored history, or Clear must not repeatedly send old completion messages. Retain messages in the app even if Windows notification settings suppress the popup, and keep notifications free of focus-stealing behavior.
- Preserve audio preferences: muting completion sounds must not remove completion messages, stop monitoring, or pause the queue. Keep these per-task messages separate from the existing repeating project-batch audio reminder.

## 4. Findings and execution approach

The installed CLI during analysis was `codex-cli 0.154.0-alpha.6.2`. Its help exposed `app-server`, `exec --json`, a project directory argument, model selection, and stdin prompt input. Recheck installed capabilities during implementation; do not hardcode the version-specific executable path discovered on this machine.

Preferred long-term adapter: the documented Codex App Server over redirected standard input/output. Initialize the connection, discover models, create a task with `thread/start`, submit the prompt with `turn/start`, and consume progress/completion events. Record both thread and turn identifiers. App Server completion notifications include a status that must be checked; the notification name alone is insufficient.

A simpler initial execution adapter is `codex exec --json`, which emits machine-readable events and accepts a project working directory and model. Use it if it meets the validated product requirements; keep transport details behind an interface so the UI and coordinator are independent of that choice. If approvals or input cannot be handled through a chosen adapter, expose that limitation and pause rather than silently treating a blocked result as success.

Use direct process arguments (`ProcessStartInfo.ArgumentList`) and serialized protocol messages or stdin for prompts. Set `UseShellExecute = false`, `CreateNoWindow = true`, and hidden window behavior where applicable. Do not concatenate note text into `cmd.exe` or PowerShell command strings. Let Codex manage its own authentication. Keep ordinary Codex permissions and surface requests that need user action; silent dispatch does not require bypassing sandbox or approval rules.

The Windows desktop application and native Windows Codex share a Codex home directory by default. This is promising for session discovery, but it does not establish exact desktop sidebar association or live synchronization. The tools available to an agent within the Codex desktop conversation are not automatically a public API for the WPF app.

Official references from the feasibility review:

- [Codex App Server](https://learn.chatgpt.com/docs/app-server): task/turn lifecycle, transports, model discovery, input/approval handling.
- [Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode): `codex exec --json`, stored authentication, structured output, and session continuation.
- [Projects and chats](https://learn.chatgpt.com/docs/projects): the CLI's project is its selected working directory.
- [Windows application](https://learn.chatgpt.com/docs/windows/windows-app): native Windows Codex home and session-sharing considerations.

## 5. Architecture and data ownership

Suggested components; adapt names to the existing code style:

| Component | Responsibility |
| --- | --- |
| `ProjectTasks` WPF control and view model | Bullet editor, queue ordering, per-item settings, execution controls, activity display. |
| `ProjectTaskStore` | Versioned local persistence, atomic saves/backups, concurrent-edit protection, run receipts. |
| `ProjectQueueCoordinator` | Enable/pause state, dependency tracking, item claiming, serial dispatch, reconciliation. |
| `ICodexTaskRunner` and a concrete adapter | Supported Codex transport, model discovery, startup, progress, completion, input/approval requests, interruption. |
| Shared task activity model | Recently completed, starting, running, waiting, and needs-attention projections for all tracker surfaces. |
| Keep-awake service | Own temporary Windows power requests for active queue work and release them through the coordinator's lifecycle. |
| Completion notification service | Per-run completion messages, local desktop/tray delivery, retained activity history, and deduplication. |

Reuse the launcher's stable `ProjectProfile.Id` for note ownership. Resolve and validate the actual working folder separately. Display names and the currently selected project are not execution identities. Prevent conflicting dispatches from multiple launcher profiles that resolve to the same checkout; also account for overlapping writable folders when deciding whether work can safely run concurrently.

Use a separate local store such as `%LOCALAPPDATA%\FullStackLauncher\project-tasks.json`. Keep notes, prompts, and task-name snapshots out of `launcher.settings.json`, monitor preferences, and embedded first-use publish settings. Preserve isolated behavior for explicit settings overrides by defining an isolated task-store location for those instances.

Persist at least:

- Notes: stable item ID, project ID, task name, prompt, order, completion/archive state, timestamps.
- Queue items: note reference, enabled state, order, model, effort, lifecycle state.
- Queue configuration: enabled/paused state, assigned project/folder, optional exact external predecessor identity, recovery state.
- Execution receipts: unique attempt ID, immutable dispatched name/prompt/model/effort/folder, returned thread/turn IDs, lifecycle timestamps, outcome, result/handoff summary, actionable failure/blocked state, and completion-message/notification handling state.

Keep a single execution owner with a single-writer lock. The existing monitor runs independently in the tray; prefer that existing lifetime for the queue coordinator so closing the dashboard can leave enabled queues running. The dashboard communicates with that owner rather than starting a competing coordinator. Make tray behavior and exit semantics explicit; quitting the owner must not lose knowledge of active runs or advance the queue as if they completed.

Do not use `ServiceRunner` as the Codex runner. It assumes a local HTTP service and service-specific shutdown behavior. Its hidden process setup is a useful pattern, but Codex requires separate process ownership and lifecycle handling.

## 6. Completion, sequencing, and recovery rules

1. Use actual run transitions, never audio events, UI colors, or historical green markers as dispatch triggers.
2. Only one item may be claimed/starting/running for a queue at a time. Serialize pause and dispatch decisions and recheck effective enablement immediately before sending work. An already submitted request may still start; keep it tracked and expose Stop current task.
3. Persist the dispatch intent and frozen run settings before external submission. Save the returned task/turn identity as soon as known. A crash after submission but before saving its result is an uncertain attempt, not permission to resend.
4. On restart or connection loss, reconcile the outstanding attempt before launching anything else. Use a real supported idempotency mechanism if available; an invented JSON-RPC request ID is not an exactly-once guarantee. If reconciliation is inconclusive, pause for attention and retain the receipt.
5. Only a successful terminal result for the expected task and expected turn can make its successor eligible. Failed, interrupted, unknown, missing, or stale state never counts as success. Handle relevant child work and pending queued input before advancing.
6. A completed agent turn does not prove the requested work succeeded. Capture an explicit outcome such as completed/blocked/needs-input with a concise result summary where the adapter supports structured output. Treat that as the agent's reported outcome, not independent proof of software correctness. Surface approval/input events and unresolved blockers; do not chain blindly from every final answer.
7. Existing external tasks are an observational dependency. The current monitor reads lifecycle status without message outcome, so retain its confirmation interval and fail-closed behavior, and clearly distinguish observed turn completion from verified task success. If the selected predecessor's required completion condition cannot be established, hold rather than guessing.
8. Deduplicate completion handling by attempt/task/turn identity so repeated polls, reconnects, Clear, or opening the UI cannot launch twice. Unrelated tasks and other projects must never advance this queue.
9. A fresh task does not inherit its predecessor's conversation automatically. Include necessary project context and a concise handoff when the queued item depends on previous work. Use the intended checkout so prior file changes are available. Do not create a fresh unrelated worktree for every sequential item and assume it contains earlier uncommitted changes.
10. Pause with an actionable state when authentication, model availability, usage limits, protocol compatibility, or the target folder prevents execution. Retry must be explicit and retain the earlier attempt history.

## 7. Implementation steps

### Step 1: Validate the execution path

- Inspect the installed CLI help and current official documentation; verify the exact supported transport and parameters.
- Establish whether a task created through that path is visible under the intended desktop project, how it refreshes, and whether expected project instructions/settings apply.
- Validate task/turn identity, startup acknowledgment, terminal status, interruption, and model/effort discovery. Avoid private desktop APIs or database writes to force a result.
- Document any desktop association limitation before committing the UI to a misleading promise. Independent notes/UI work can continue while resolving this integration.

### Step 2: Add durable project notes and queue data

- Implement the separate store and migration/version strategy using existing atomic-save patterns.
- Add note and queue models plus immutable execution receipts.
- Preserve existing project data, project rename behavior, empty project lists, explicit settings isolation, and publish defaults.

### Step 3: Add the WPF notes and queue panel

- Bind it through the existing project-selection flow.
- Implement bullet CRUD, reorder, completion, queue membership, model/effort selectors, pause/enable controls, and clear inline feedback.
- Keep model calls out of basic note capture; ordinary text entry should not require Codex access.

### Step 4: Add the runner and serial coordinator

- Implement the validated execution adapter with hidden process launch and asynchronous output handling.
- Add single-owner coordination, dependency selection, per-checkout exclusion, startup receipts, exact completion matching, pause/interrupt behavior, and recovery.
- Ensure the queue always uses its stored assigned project and folder rather than whichever project is currently displayed.
- Integrate the keep-awake service with active work, predecessor waiting, inter-task handoffs, pause/stop, tray lifetime, and cleanup. Aggregate ownership across queues rather than toggling system-awake state independently per task.

### Step 5: Upgrade the tracker

- Replace the completion-only presentation with the Recently completed / Starting task / Running transitions defined above.
- Feed the dashboard, alerts window, and floating tracker from the shared activity model.
- Preserve external task observation, child aggregation, confirmation behavior, historical marker migration, audio independence, and overlay controls.
- Keep missing names/status honest and ensure Clear cannot affect scheduling.
- Send and retain a completion message for every finished task, including immediate automatic handoffs and the last item. Keep notification handling independent from queue advancement, tracker visibility, and audio muting.

### Step 6: Verify and document the delivered behavior

- Build only `FullStackLauncher.csproj`, for example from the launcher directory: `dotnet build .\FullStackLauncher.csproj -c Release`.
- Manually inspect the edited WPF screens, keyboard access, readable dark controls, and the floating tracker. Follow the repository's deferred-testing policy; do not add an automated harness.
- For any live Codex verification, use a deliberately selected bounded task in an appropriate working folder. Do not execute real backlog notes just because this document contains example names. Report live behaviors that were not exercised.
- Manually verify that keep-awake remains active across task handoffs and tray operation, releases when work ends, and behaves correctly when pausing during a run. Verify per-task messages with immediate advancement and muted audio, and that refreshing/reopening the tracker does not duplicate them.
- Update the launcher README and applicable `AGENTS.md` sections to describe the implemented queue, new tracker, storage, background lifetime, and remaining integration limitations.
- Keep single-file publishing compatible; do not embed personal task data. Report the application build result and actual manual verification performed.

## 8. Acceptance checklist for the implementation agent

- [ ] Notes are project-specific bullets with durable add/edit/delete/reorder/complete behavior.
- [ ] Saving a note does not dispatch it; queue removal and note deletion have distinct behavior.
- [ ] Each queued item uses its selected valid model and reasoning effort in the correct project folder.
- [ ] Background dispatch does not activate Codex, type into a window, or use the clipboard.
- [ ] A selected external predecessor can gate the first item; subsequent items follow their exact owned predecessor.
- [ ] The tracker displays `Recently completed: [name]` and actual `Starting task: [name]`, then transitions to Running.
- [ ] Pause queue and Pause all prevent subsequent dispatch; Stop current is separate from pause, audio, hide, and Clear.
- [ ] Repeated events, app restarts, unknown status, and failed startup do not duplicate or incorrectly advance work.
- [ ] Blockers, approvals, questions, failures, and interrupted tasks remain visible and suspend advancement.
- [ ] Recent activity, notes, queue data, and execution receipts remain distinct and preserve existing user settings.
- [ ] Queue ownership and tray/exit behavior are clear and independent from dashboard service shutdown.
- [ ] Active auto-queue work keeps the system awake across task handoffs and tray operation without permanently changing power settings.
- [ ] Keep-awake ownership is released when no active work needs it; pausing future dispatch does not remove protection from a still-running task.
- [ ] Every confirmed task completion produces a retained message and a local notification attempt, including immediate handoffs and the final task, without duplicate messages on refresh/restart.
- [ ] Muted audio or a hidden tracker does not suppress retained completion messages or affect queue execution.
- [ ] Desktop task visibility/project association is verified or its precise limitation is documented.
- [ ] Application build/manual checks and any unverified behavior are reported without adding or running automated tests.

## 9. Existing code map

Paths are relative to `FullStackLauncher`:

| File | Relevant existing behavior |
| --- | --- |
| `Models/LauncherSettings.cs` | `ProjectProfile.Id`, name, root folder, and current project settings. |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Dashboard layout, project selection, monitor controls, and service shutdown. |
| `Services/SettingsStore.cs` | Local settings, atomic save, backups, and concurrent-edit protection. |
| `ProfileEditorWindow.xaml.cs` | Reconstructs project profiles; preserve any data that must survive editing. |
| `Services/ServiceRunner.cs` | Hidden process plumbing; HTTP-service assumptions prevent direct reuse as a task runner. |
| `App.xaml.cs` | Independent monitor process entry point. |
| `CodexMonitor/CodexMonitorWindow.xaml` / `.xaml.cs` | Monitor UI, singleton/tray lifetime, IPC commands, polling, and audio controls. |
| `CodexMonitor/LocalCodexReader.cs` | Experimental read-only lifecycle discovery, project/worktree matching, parent/subagent relationships. |
| `CodexMonitor/ChatProgressState.cs` | Observed-work arming, completion confirmation, and retained completion markers. |
| `CodexMonitor/ChatProgressWindow.xaml` / `.xaml.cs` | Floating tracker rendering, transparent appearance, bounds, visibility, and Clear. |
| `CodexMonitor/ReminderState.cs` | Project-wide audio reminders; never use repeated sound events to schedule tasks. |
| `CodexMonitor/MonitorPreferences.cs` | Separate monitor settings and minimal persisted completion identifiers. |
| `FullStackLauncher.csproj` / `publish.ps1` | Application-only build and single-file publishing with embedded initial settings. |

Resume from the current progress section. Complete the live Step 1 integration checks before introducing automatic execution; keep the existing notes and queue editor usable throughout the next stages.
