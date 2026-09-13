# Codex integration findings

Updated September 12, 2026 after the second implementation increment. The launcher now has an explicit bounded connection-check workflow and a reusable owned App Server transport. Automatic queue execution remains unavailable. See the [live protocol record](LIVE_CHECK.md) for identity-matching evidence and observations; personal paths, project names, and identifiers are omitted from the public record.

## Supported transport and local schema

Use a separate launcher-owned `codex app-server --listen stdio://` process. The documented protocol uses newline-delimited JSON, an `initialize`/`initialized` handshake, and model discovery via `model/list`. No experimental opt-in is needed for the used methods. [Official App Server documentation](https://learn.chatgpt.com/docs/app-server).

Installed CLI version: `codex-cli 0.154.0-alpha.6.2`. Help was rechecked during this increment. The earlier investigation generated the installed non-experimental JSON schemas in a temporary folder outside the repository; those schemas were inspected again. No test project, harness, or UI script was created.

| Interface | Schema and live evidence |
| --- | --- |
| `model/list` | Paginated data/nextCursor; preserve the wire model rather than catalog entry ID. Efforts are discovered strings with descriptions and an advertised default. |
| `thread/start` | Accepts cwd/model/sandbox/approval policy/config. Returns thread ID, resolved cwd/model/effort, instruction source paths, sandbox and approval settings. Stable request schema has no desktop project-assignment field. |
| `thread/started` | Exact thread acknowledgment, possibly interleaved before the response. |
| `turn/start` | Returns exact turn ID; accepts model/effort/cwd and a structured outputSchema. |
| `turn/started`, `turn/completed` | Exact thread and turn IDs; in-progress, completed, interrupted, or failed status. Final text and RPC acknowledgment are separate evidence. |
| `turn/interrupt` | Requests interruption of an exact thread/turn pair. Its empty acknowledgment is not terminal confirmation. |
| `thread/read`, `thread/turns/list`, `thread/items/list` | Read-only retrieval for future reconciliation. The installed server deprecates full-history hydration via includeTurns:true for paginated threads; use the paginated methods. |

## Implemented code

`CodexAppServerConnection.cs` resolves a native executable from absolute PATH directories, then the user's versioned Windows installation. It uses hidden window settings, redirected streams, ArgumentList, serialized writes, and numeric response-ID matching. Stderr is drained without retention. Notification buffering is bounded by both count and characters; individual lines are bounded. Server requests receive an unsupported-request response and stop the check; the client never approves or executes a requested tool. Connection loss, malformed protocol, and buffer overflow fail closed. Disposal closes stdin and terminates only the owned child tree after a bounded grace period.

`CodexModelCatalog.cs` uses this transport for explicit refresh. Its public LoadAsync(CancellationToken) contract and model/effort records remain unchanged. It has a 30-second deadline, pagination with repeat-cursor detection and a 20-page limit, and sanitized setup/compatibility/access errors. Discovery creates no tasks and does not prove model execution access.

`CodexConnectionCheck.cs` is a single-use diagnostic service, not a general runner. CheckAsync(folder, modelId, effort, persistUpdate, token) revalidates the model combination and sends only its fixed prompt. The caller saves a frozen intent first. The service awaits each persistence callback before further submission: Starting, thread identity, turn identity, and exact Running evidence. It checks effective folder/model/effort and read-only/no-network/never-approval settings before sending the prompt. Stop and submission claims share a lock at the serialized write gate; a submission already claimed can still arrive and remains tracked.

Successful completion requires the expected start events, exact terminal turn, and one structured final response containing outcome and summary. A model-reported completed outcome is protocol evidence, not independent proof of project correctness. Blocked/input outcomes, unexpected tool/child items, server requests, unknown states, and lost evidence cannot advance anything. TerminalConfirmed distinguishes actual terminal events from earlier attention states. The work deadline is 90 seconds, followed by bounded interrupt and cleanup. No retry or reconciliation is automatic.

`CodexConnectionViewModel.cs` and `CodexConnectionWindow.xaml` expose explicit refresh, Run connection check, Stop check, retained history, IDs, resolved settings, and manual desktop-placement observations. Project/folder are frozen when opening the modal window. A per-folder Windows-session mutex prevents concurrent checks using the same normalized path; it does not resolve junction aliases or replace future checkout/coordinator ownership. Fresh store loads preserve unrelated edits; a save race fails closed. Closing during activity requests Stop and leaves the window open while the bounded operation records its result.

## Storage and manual review

The task store writes version 2. Version 1 loads through an in-memory migration and is replaced only by an ordinary atomic save, retaining the previous file as backup. Version 2 requires receipt purpose and connection evidence fields; old application copies reject it rather than dropping them. Existing notes, drafts, queue settings, snapshots, and notification-attempt protection are retained.

Each diagnostic uses detached synthetic note/item identities and Purpose=ConnectionCheck; it neither creates a note nor claims a queue item. Receipt purpose, snapshot, creation time, and established thread/turn identities are immutable. Evidence and manual observations stay in the separate task store, never launcher/monitor preferences or embedded publish defaults.

Prepared, starting, running, recovering, attention, and known-turn-without-terminal attempts hold further checks in that folder. Manual confirmation that the attempt is no longer running can release the diagnostic hold; the receipt and original outcome remain unchanged. Desktop placement is a separate manual observation. Neither action enables queue execution. Future scheduling/tracker code must filter diagnostic receipts out of real queue completion/notification processing.

## Live verification and desktop association limitation

Manually entered supported requests under the user's Windows account confirmed handshake, model discovery, thread creation, resolved folder/model/effort/permissions, exact start events, one structured completion, and exact interruption of a second turn. Owned processes exited on stdin EOF. Read-only retrieval after reconnect returned the retained completed and interrupted turns. No backlog note was submitted and no project work/tool/child activity was observed. These were manual protocol operations, not invocation of the WPF adapter or an automated harness.

The diagnostic task used the selected saved desktop project's exact cwd. It became discoverable automatically through the desktop's read-only task list, but had `projectId: null` and appeared in the general **Tasks** section. Cwd matching alone did **not** assign the desktop project in this observed run. No private API, stored-session edit, or database write was used to force placement. Native visual inspection was unavailable. Folder context is not a public guarantee of assignment through cwd. [Official projects documentation](https://learn.chatgpt.com/docs/projects).

The response's instruction-source list was empty for this folder. Loaded project instructions/settings in a real queued checkout remain to be verified. The fixed prompt requests no tools and the adapter stops on observed tools/child work, but the stable turn schema has no universal tools-off switch.

## Remaining work before automatic execution

1. Manually use the actual WPF connection window: refresh, successful check, Stop, timeout/close, receipt history, conflict handling, and note-draft preservation. The Release build passed with zero warnings/errors; native WPF interaction and publishing were not exercised.
2. Resolve a supported desktop assignment path or agree a clear behavior for general Tasks placement. Preserve the observed limitation instead of claiming the folder implies association.
3. Implement and verify fail-closed recovery for an active/uncertain submission using exact identities and bounded paginated history. Successful post-disconnect terminal reads do not prove active-run continuation or exactly-once submission. RPC IDs are not idempotency keys.
4. Add a general work adapter and independent tray coordinator with versioned IPC/store identity, owner-mediated mutations, exact dependencies, checkout exclusion, pause/stop, approval/input handling, child aggregation, and keep-awake ownership.
5. Add shared task activity and retained/deduplicated completion messages and notifications. Current passive monitoring, audio, and overlay behavior remain unchanged.
