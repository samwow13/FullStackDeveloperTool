# Codex launch and command access

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

Queue-owner protocol 11 requires the corrected access policy and command preflight. Dashboard replacement deliberately preserves the independent queue owner. An older owner continues using its original immutable EXE and is rejected by the new dashboard before queue commands are applied.

When the old owner has no active work, use its tray menu **Exit queue owner**, then reopen **Notes & queue**. Launcher never force-terminates it for an upgrade. Original blocked or uncertain attempts retain their exact receipts and require the existing explicit review/retry controls. Updating the dashboard or continuing the Codex conversation does not clear a queue hold, change a recorded outcome, or enable a queue.

The connected MCP adapter still supports Notes saving but has no saved-task enqueue operation. Saving a follow-up Note is not queue membership or dispatch. The existing **Add guarded MCP task queue handoff** Note covers that separate missing capability.

## Evidence boundaries

The focused application build and single-file publish succeeded with zero warnings and errors. The exact published EXE passed the full-access command diagnostic. Normal dashboard handoff closed the previous dashboard and preserved the running SimNow backend and frontend process identities. The verified build was reopened after the user manually closed the dashboard; Launcher MCP and service status remained available.

The independent older queue owner remained alive and answered read-only status with `hasActiveWork = false` after the reported tray exits. The new dashboard displayed its protocol compatibility guard and preserved task data. The current source exposes the guarded exit through the queue tray menu; closing the dashboard does not exit that process. Native window inspection read the guard, but screenshot capture and UI input were unavailable, so no guarded tray exit was completed by the diagnostic task. No process was force-terminated.

The Windows sandbox file lock remains unresolved. Full queue execution and the new WPF command-check button remain unverified. Activating the corrected runner requires the older queue owner to complete its guarded exit; any held attempt still requires explicit review. No queue was enabled, hold cleared, or task resent during diagnosis.

References: [official App Server command execution](https://learn.chatgpt.com/docs/app-server) and [official Windows sandbox troubleshooting](https://learn.chatgpt.com/docs/windows/windows-sandbox).
