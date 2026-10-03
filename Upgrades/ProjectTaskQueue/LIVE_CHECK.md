# Manual App Server connection verification — September 12, 2026

This is a record of manually entered documented protocol requests, not a test program or harness. No backlog notes are submitted. The application code's WPF interaction remains a separate manual check.

Personal folder paths, the saved project name, and diagnostic task/turn/project identifiers are omitted from this public record. Exact identity matching was verified during the recorded run.

## Intent recorded before submission

- Client: separate, owned `codex app-server --listen stdio://`, native CLI `0.154.0-alpha.6.2`.
- Working folder: the selected saved desktop project's exact folder, returned by the read-only project list.
- Model: `gpt-5.6-sol`; reasoning effort: `low`. This combination was returned by live `model/list`.
- Task: fixed connection reply only, no project work, tools, commands, file edits, delegation, or messages.
- Requested policies: read-only sandbox, network access false, approval policy never.
- Expected reply: structured outcome and a short connection summary. Success is protocol evidence only.
- No automatic retry. An uncertain submission is retained for review.

## Observations

- The initial restricted shell ran under a separate sandbox Windows account. Its handshake succeeded, but it was not evidence about the user's signed-in Codex context. No task was submitted through it.
- A separately owned process under the user's Windows account returned a successful `initialize` response. `initialized` and `model/list` returned model/effort choices and a pagination cursor.
- `thread/start` returned a diagnostic task ID and matching folder, `gpt-5.6-sol`, `low`, `approvalPolicy: never`, and sandbox `readOnly` with `networkAccess: false`. Its exact `thread/started` notification followed. `instructionSources` was empty; project instruction loading is not established by this folder check. The returned thread had `projectId: null`.
- `turn/start` returned the first diagnostic turn ID. Matching `turn/started` (`inProgress`), final `item/completed`, and `turn/completed` (`completed`) were observed. The final response reported `outcome: completed` and `summary: Connection check completed; no project work requested.` No tool/child work was observed for this turn.
- The desktop's read-only task list discovered this exact task automatically, with the expected `cwd`, but `projectId: null`. Its sidebar section was the general **Tasks** section. Matching the saved project's folder did **not** associate this created task with that project in the observed run. No native visual inspection was available.
- A second bounded turn on the same diagnostic task confirmed interruption. The exact `turn/started` notification was observed, `turn/interrupt` acknowledged the exact task/turn pair, and `turn/completed` reported `interrupted` for that pair. No tool/child work was observed.
- Both original owned App Server processes exited successfully after stdin EOF. The existing desktop daemon was not touched.
- A fresh owned connection retrieved both exact turns and their completed/interrupted states. `thread/read includeTurns:true` returned a deprecation notice for full-history hydration, so `thread/turns/list` was also called successfully with a bounded page and `itemsView: summary`. It returned both IDs/statuses and `nextCursor: null`. The fresh process then exited successfully on stdin EOF. No resume request or additional work was submitted. Active-run recovery remains unverified.

This verification does not modify Codex configuration or stored databases and does not attach to its desktop daemon.

## Experimental project placement check — September 23, 2026

- Installed CLI: `codex-cli 0.155.0-alpha.16.3`. Its opt-in experimental App Server schema exposes `project/list` and a `projectId` input on `thread/start`.
- A read-only `project/list` call found a unique project root matching the selected saved desktop project's folder. App Server project IDs and desktop saved-project IDs differed for the same folders.
- A prompt-free durable `thread/start` accepted the experimental project ID and exact folder, but the desktop did not list that empty thread. After its owned server closed, a new connection could not resume it because no rollout existed. No turn was sent to it.
- A second, bounded diagnostic created a new thread and sent one fixed reply-only turn in the same owned server connection. It used the unique App Server project ID, exact folder, discovered default model/effort, read-only sandbox, no network, and never-approve policy. Exact thread/turn start and completed terminal IDs matched. The structured final reply reported completion; no tool or child work was observed.
- The desktop discovered the second exact task and completed turn under general **Tasks** with `projectId: null`, rather than the saved desktop project. The experimental project ID did not establish desktop sidebar assignment in this observed run.
- No prepared queue note or project work ran. No private desktop API, Codex database write, clipboard, typing simulation, or focus change was used. The user subsequently authorized general **Tasks** placement for future automatic queue work, while retaining each queue's saved working folder.
