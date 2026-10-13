# Codex launch and command access

## Start Agent fails before sending

On first use, **Start Agent** loads the effective access configuration for the selected project through an owned native App Server connection. It initializes the connection, then sends `config/read` with the project folder as `cwd`. Model discovery follows that lookup. These operations create no chat and submit no prompt.

Older Launcher diagnostics replace an unclassified RPC error with advice to open an exact chat. That advice is misleading at this stage because the composer has not created a chat. Startup failures retain the operation (`initialize`, `config/read`, or `model/list`), numeric RPC code when reported, and fixed setup guidance. Raw server messages, configuration values, and credentials remain private. Configuration and permission failures still block submission. An explicitly unsupported `config/read` method permits only the manual composer's conservative Workspace default, which the user reviews before sending; the command preflight and returned thread permissions must still pass. Automatic dispatch never uses that fallback.

The reported work-machine failure was not reproduced on this machine. Its installed `codex-cli 0.162.0-alpha.2` accepts the project-scoped configuration lookup with both stable and experimental initialization. This does not establish compatibility or configuration health on the work machine.

Possible machine differences include an older native executable on `PATH`, different user/project/managed configuration, and an already-saved access preference on the working machine that bypasses default discovery. These are investigation leads, not confirmed causes. Launcher prefers the runnable native CLI extracted by desktop Codex before `PATH` and recognized official npm installations. It resolves npm's bundled native executable without parsing or executing the shell shim.

## Older models in the picker

The picker requests `model/list` from the selected native Codex installation; it does not match a Codex release number or maintain a model-name whitelist. An older standalone CLI can return an older catalog even when the desktop app offers newer models. Discovery prefers the native runtime launched by the official desktop app, then its extracted native CLI locations, before the existing PATH/npm fallback. Executable discovery and startup run off the WPF dispatcher.

Do not launch the packaged `WindowsApps\...\app\resources\codex.exe` directly. On this machine it is readable and byte-identical to the extracted CLI, but starting it outside the package fails with Windows error 5. Codex's running native runtime under `%LOCALAPPDATA%\OpenAI\Codex\bin` starts normally. The corrected resolver uses the runnable runtime without copying packaged binaries, changing permissions, elevating, or altering Codex configuration.

On October 13, 2026, a manual read-only call through the rebuilt Launcher's production executable resolver, access lookup, and model catalog selected the desktop's extracted native CLI, loaded Full Access, and returned seven visible models including `gpt-6.1-sol`, `gpt-6-astra`, and `gpt-6-sol`. This exercised the startup code that failed before discovery, without creating a thread or submitting a prompt. Native dropdown interaction and the work-machine catalog remain unverified; automated tests remain deferred.

Start Agent discovers models in its project folder. Notes & queue and the connection window use the saved assigned folder. This keeps ordinary picker discovery aligned with the project configuration used when submitting work. The shared Notes & queue catalog still uses the assigned folder when Automatic Loop Mode selects a different subfolder; the actual loop folder's model and effort are revalidated before dispatch.

Refresh reads Codex's returned picker-visible models, wire names, defaults, and supported thinking levels without hardcoding model generations. Saved selections remain intact when a model becomes unavailable. The list depends on the selected Codex client, account, provider, and its catalog/cache; discovery alone does not prove a successful model turn or work-machine access. Launcher does not inspect credential files, alter Codex configuration, or connect to the existing desktop daemon. See the [official model discovery protocol](https://learn.chatgpt.com/docs/app-server#list-models-modellist).

## CLI compatibility

Compatibility follows installed capabilities and documented protocol shapes, rather than a hardcoded minimum version or a guessed model list. The owned child starts with `codex app-server`, whose default transport is stdio in older and current CLIs. Only the optional initialization capability field may be retried after an explicit invalid-parameters rejection. Thread creation, prompt submission, permission failures, and failed command checks are never replayed for compatibility.

Model discovery sends the shared `cursor` and `limit` parameters. Wire model names and supported thinking levels remain mandatory and are revalidated before submission. Older thread responses may omit the newer approval-reviewer field or return a null thinking level. Supplied values must still match; the exact thinking level is explicitly sent with the turn. Folder, model, sandbox, and `never` approval echoes remain required.

Older servers can report an unknown method as RPC `-32600` with an exact `Invalid request: unknown variant` prefix, rather than `-32601`. Launcher recognizes only the bounded prefix naming its pending method. An unsupported turn-pagination read can fall back to `thread/read(includeTurns: true)` without resuming a thread. Reads remain bounded to 500 turns with exact saved identities and duplicate-ID checks. Missing history or ambiguous terminal evidence does not establish completion.

Some older `config/read` implementations accept but ignore `cwd`. Before resolving automatic access, Launcher runs the selected native executable's local schema generator and verifies that `ConfigReadParams` declares a string `cwd`. The generator creates no server, thread, or provider request, and its temporary files are removed. Capability evidence is cached only for the unchanged executable. Unsupported, unreadable, or changed schema evidence blocks automatic dispatch before any prompt.

Crew monitoring supports older state schemas and pre-SQLite session headers. It uses the highest present store and validates required columns; unreadable newer stores never fall back to stale older stores. Installations without the newer turn projection use bounded explicit rollout lifecycle records. Current projection databases retain their required queue and question checks. Legacy start positions scope excerpts and working-folder evidence when no turn ID is available. Partial records, missing lifecycle evidence, and failed or interrupted work cannot trigger successful completion. Codex metadata remains read-only.

Current native CLI inspection reports `codex-cli 0.162.0-alpha.17.2`. Historical protocol and npm layouts were reviewed against official source tags `rust-v0.50.0`, `rust-v0.70.0`, `rust-v0.77.0`, `rust-v0.80.0`, `rust-v0.90.0`, `rust-v0.100.0`, and `rust-v0.110.0`. This is source compatibility evidence, not an end-to-end run on every release or the work machine. Keep awake still suspends when the installed CLI cannot supply its required exact lifecycle evidence.

On the failing machine, collect the updated red startup error and the following non-secret output under the same Windows account:

```powershell
Get-Command codex -All | Select-Object CommandType, Source
codex --version
codex login status
```

Do not share tokens, credential files, or raw configuration. Compare the selected native installation and reported RPC operation/code before changing settings or attempting another submission.

## October 7, 2026 investigation

The Luna **Implement next step** task reached Codex after **Start Queue**. Its first command failed before process startup with `helper_unknown_error: setup refresh had errors`. This was not a failed Notes save or a missing Launcher MCP connection.

The Windows sandbox log at `C:\Users\samwo\.codex\.sandbox\sandbox.2026-10-07.log` records runtime read/execute validation failing for `...\cua_node\3dd31cfff853001c\bin\node_repl.exe` at 17:07 UTC. The decisive error is `The process cannot access the file because it is being used by another process. (os error 32)`. Granting access to the Luna workspace succeeded; validating the Codex runtime did not. The same failure occurred in other folders before the queue attempt and in the later diagnostic conversation. The exact process holding the file was not established.

The original queue used `workspace-write` and `on-request`. Launcher hardcoded both settings even though its independent App Server client cannot answer approval requests. The effective user configuration observed through a bounded App Server `config/read` was `sandbox_mode = danger-full-access`, `approval_policy = never`, and `windows.sandbox = elevated`. Launcher did not change that configuration.

## Corrected behavior

Queue dispatch reads the effective access configuration for the assigned folder once. It selects full access only when both `danger-full-access` and `never` are explicitly configured. An explicitly configured workspace sandbox remains workspace access; full access with an interactive approval policy is conservatively limited to workspace access. Read-only, missing, and unsupported access profiles stop automatic dispatch rather than gaining workspace writes. The selected mode remains fixed for the attempt. Noninteractive Launcher tasks use `never` approvals, so they do not request an approval channel Launcher cannot display.

Queue and **Start Agent** run a fixed PowerShell command through the documented `command/exec` operation before creating a chat. A successful exit and the exact expected marker are both required. This check creates no thread or turn, loads no PowerShell profile, writes no project data, and performs no network operation. A failed workspace check never switches to full access, repairs ACLs, changes Codex settings, or stops a process.

Queue receipts retain the chosen access mode, approval policy, and command-access result in their existing connection-details field. This evidence cannot change after submission starts. Attempt details show it alongside the original task and turn identities. Failed turns, protocol errors, and approval requests retain fixed, actionable failure categories without exposing arbitrary server payloads or provider messages.

Image-bearing queue submission and exact-turn recovery now use the same bounded image-capable transport as **Start Agent**. This avoids rejecting permitted images at the ordinary 1 MiB message limit. Predecessor history also permits bounded image payloads.

The existing fixed reply-only **Run connection check** remains separate. A model reply does not verify shell startup. **Codex connection → Check command access** verifies the command path without submitting a task.

## Command diagnostic

The same production diagnostic is available from the published EXE:

```powershell
.\FullStackLauncher.exe --codex-command-check --folder "C:\absolute\project"
.\FullStackLauncher.exe --codex-command-check --folder "C:\absolute\project" --workspace
```

The first command uses the effective configured access. `--workspace` requests only the more restrictive workspace check. Output is one JSON object containing `commandAccess`, `accessMode`, and a safe `summary`. Exit code 0 means command access was verified; exit code 2 means it was not verified. Neither command changes Notes, queue settings, holds, or Codex configuration.

Live diagnosis using the newly built application and the Luna folder verified full access with exit code 0. The workspace check reproduced the setup-refresh rejection with exit code 2. This establishes the command-access distinction; it does not prove application edits or a complete queued agent task.

## Activating the new queue runner

Queue-owner protocol 12 requires project-aware access capability evidence, the command preflight, and the compatible history reader. Dashboard replacement deliberately preserves the independent queue owner. An older owner continues using its original immutable EXE and is rejected by the new dashboard before queue commands are applied. Dashboard start permits are protocol-scoped, so a surviving older owner cannot borrow the replacement dashboard's permit for later work. Active owned work continues.

When the old owner has no active work, use its tray menu **Exit queue owner**, then reopen **Notes & queue**. Launcher never force-terminates it for an upgrade. Original blocked or uncertain attempts retain their exact receipts and require the existing explicit review/retry controls. Updating the dashboard or continuing the Codex conversation does not clear a queue hold, change a recorded outcome, or enable a queue.

The connected MCP adapter still supports Notes saving but has no saved-task enqueue operation. Saving a follow-up Note is not queue membership or dispatch. The existing **Add guarded MCP task queue handoff** Note covers that separate missing capability.

## Evidence boundaries

The focused application build and single-file publish succeeded with zero warnings and errors. The exact published EXE passed the full-access command diagnostic. Normal dashboard handoff closed the previous dashboard and preserved the running SimNow backend and frontend process identities. The verified build was reopened after the user manually closed the dashboard; Launcher MCP and service status remained available.

The independent older queue owner remained alive and answered read-only status with `hasActiveWork = false` after the reported tray exits. The new dashboard displayed its protocol compatibility guard and preserved task data. The current source exposes the guarded exit through the queue tray menu; closing the dashboard does not exit that process. Native window inspection read the guard, but screenshot capture and UI input were unavailable, so no guarded tray exit was completed by the diagnostic task. No process was force-terminated.

The Windows sandbox file lock remains unresolved. Full queue execution and the new WPF command-check button remain unverified. Activating the corrected runner requires the older queue owner to complete its guarded exit; any held attempt still requires explicit review. No queue was enabled, hold cleared, or task resent during diagnosis.

References: [official App Server command execution](https://learn.chatgpt.com/docs/app-server) and [official Windows sandbox troubleshooting](https://learn.chatgpt.com/docs/windows/windows-sandbox).
