# ADR 0019: Automation Review Queue Core persistence and recovery

**Status**: Accepted

**Context slice**: `AutomationReviewQueueService`, `<vault root>/.glasswork/review-queue.json`, queue projection/backup/corruption files, future desktop and MCP review composition.

## Context

Meeting-derived automation needs a durable place to stage Task proposals without silently mutating Tasks. The queue must survive restarts, support both desktop and MCP composition, avoid losing state during concurrent access, and recover cleanly from local file corruption. The queue also owns source cursors and source health, so persistence bugs can create skipped meetings as well as lost review work.

The vault remains the user's inspectable source of truth, but queue state is not task content. It belongs under the vault root's app-owned `.glasswork` area, alongside other local automation artifacts, and must stay out of Git history.

## Decision

### One deep Core module at the queue seam

`AutomationReviewQueueService` is the single public Core seam for queue behavior. Callers submit source runs, load snapshots, analyze coherent selections, approve/reject/refresh items, acknowledge recovery incidents, and run cleanup through that module. Persistence layout, projection regeneration, dedupe rules, source registry trust, typed proposal validation, task-fingerprint staleness checks, apply-failure metadata, and backup/corruption handling stay behind the seam.

This keeps desktop and MCP transport layers thin: both compose the same Core workflow instead of reimplementing queue rules.

### Canonical storage under `<vault root>/.glasswork`

The canonical durable document is:

- `<vault root>/.glasswork/review-queue.json`

Supporting files are:

- `<vault root>/.glasswork/review-queue.md` — disposable generated projection
- `<vault root>/.glasswork/review-queue.json.bak` — one prior validated backup
- `<vault root>/.glasswork/review-queue.corrupt-<timestamp>.json` — preserved unreadable canonical copies

`.glasswork/.gitignore` includes `review-queue*` so queue files, backups, and preserved corruption files do not pollute vault Git history.

### Source registry trust is code-defined

Review sources are not open-ended configuration. The source registry and proposal-type matrix are code-defined in Core. In v1 the only registered source is `meeting-transcript-sync`. Unknown source ids and disallowed proposal types are explicitly rejected at submit time.

This keeps provenance trust local and auditable: callers cannot smuggle new source capabilities into durable state by configuration drift alone.

### Serialized cross-process ownership via mutex + reload-inside-lock

Desktop and MCP are separate OS processes, so an in-process `lock` is insufficient. Queue read-modify-write operations use a vault-scoped named mutex, reload the canonical document inside the lease, and replace the file atomically using a unique temp path plus `File.Replace`/`File.Move`.

This choice optimizes for locality over a more abstract adapter seam because queue persistence is a local filesystem concern, not a true remote dependency.

### Backup rotation and corruption recovery

Before replacing the canonical file, the service rotates exactly one **validated** prior canonical copy into `review-queue.json.bak`. Invalid canonicals never overwrite the backup.

If the canonical file becomes unreadable:

1. Preserve it as `review-queue.corrupt-<timestamp>.json`.
2. Recover the canonical file from the validated backup when available.
3. If both canonical and backup are unreadable, recover to an empty queue document rather than leaving the queue unusable.
4. Persist a recovery warning in canonical state and require explicit acknowledgement before source cursors may advance again.

Non-source queue actions remain available during the recovery gate so the user can still reject/withdraw/expire existing items.

### Dedupe is disposition-aware

The queue keeps compact 30-day History plus permanent seven-field dedupe records:

- `source_id`
- `source_item_id`
- `task_id`
- `proposal_type`
- `change_fingerprint`
- `disposition`
- `disposed_at`

Two suppression rules coexist:

1. **Rejection finality** uses logical identity (`source_id`, `source_item_id`, `task_id`, `proposal_type`) and suppresses all future variants of that logical item.
2. **Exact terminal suppression** uses the same identity plus `change_fingerprint` and suppresses repeats for other terminal dispositions while still allowing materially different later fingerprints.

### Approval is one-task, one-save, conflict-checked

Approval operates on a **coherent selection** for exactly one Task. Core rejects selections that mix multiple effective state outcomes (`status-change`, `block`, `unblock`) or multiple due-date outcomes. Related meeting-note items can be suggested alongside a stateful proposal, but approval applies exactly the item ids the caller selected.

Core mutates the target Task in memory first, then persists one task-file write. If that write fails, the whole selection remains Pending and each selected item records **Apply failed** metadata (`last_apply_failure_*`) instead of moving to History. Retry uses the same item ids and must therefore be idempotent.

### Stateful proposals fingerprint only relevant task fields

Stateful proposal types (`status-change`, `block`, `unblock`, `blocker-reason-change`, `due-date-change`) store a Core-computed fingerprint over only the task fields that matter to that mutation. Unrelated edits (for example, Notes changes during a block proposal) do not invalidate the proposal; relevant edits move the item to **Needs refresh** on the next queue load/analysis.

Meeting-note proposals do not use that stateful fingerprint gate. They remain independently approvable and append under the managed **Meeting updates** subsection in Notes.

## Consequences

- **Leverage**: one Core seam owns submission, durable state, history, metrics, recovery, and projection behavior for every future caller.
- **Locality**: queue bugs and invariants live in one module instead of being split between app code, MCP tools, and ad hoc JSON edits.
- **Operational safety**: a preserved corruption file plus acknowledgement-gated cursor advancement prevents silent meeting skips after rollback/recovery.
- **Git hygiene**: queue state stays local and inspectable without touching task markdown or repository history.

## Alternatives considered

- **Store queue state beside task files under `wiki/todo/`** — rejected. Queue state is not task content and should not participate in task Git history or task file watching.
- **Expose persistence/projection helpers directly to callers** — rejected. That would create shallow seams and duplicate recovery/dedupe logic.
- **Configuration-driven source registry** — rejected for v1. Trust and allowed proposal authority belong in code, not mutable settings.
- **Process-local locking only** — rejected. Desktop and MCP can both touch the queue.
