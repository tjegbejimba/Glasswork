# ADR 0018: MCP task deletion is soft-delete-first (`status: cancelled`); hard delete is an explicit, guarded opt-in

**Status**: Proposed
**Context slice**: resolves #207 (`delete_task`); `VaultService`, `SelfWriteCoordinator`, `IndexService` (backlinks), `TaskService`; new `delete_subtask` MCP tool (tracked separately, *not* governed by this ADR)
**Relates to**: ADR 0007 (MCP boundaries — stdio, vault-only writes, self-write marker, optimistic concurrency), ADR 0005 (backlinks watcher + index — deletion breaks link targets), ADR 0016 (task `type` / status enum), ADR 0002 (task prose + artifact siblings)

## Context

Agents create tasks during planning sessions — and therefore create duplicates,
mistakes, and cancelled work. Without a deletion path, the vault accumulates stale
`.md` files that pollute `list_tasks`, mislead the agent, and never get cleaned up.
This is the motivation behind #207, which currently sits in `needs-triage` precisely
because "delete" is not a clean file operation in Glasswork:

- **Backlinks break.** Other tasks and wiki pages may link to the target. Hard
  removal orphans those links (ADR 0005). The app tolerates unresolved links, but
  the information is lost.
- **Artifacts are owned, not referenced.** A task owns a sibling
  `<id>.artifacts/` folder (ADR 0002). Hard delete must decide whether that folder
  and its agent-produced work-products die with the task.
- **Cascade is cross-file.** In-file checklist subtasks travel with the task, but
  child *tasks* (`parent:` frontmatter) live in separate files. Deleting a parent
  PBI must decide those children's fate.
- **Self-write coordination.** Every deletion must register with the
  `SelfWriteCoordinator` marker or the running app fires a spurious "external
  change" banner (ADR 0007). A multi-file cascade must hold the coordinator across
  *all* deletions, not per-file.
- **Crash safety.** A mid-cascade crash can orphan children. #207 proposes a
  `_pending_operation.json` recovery log.

There is currently **no delete-a-task method in `Glasswork.Core` at all** — only
`TaskService.DeleteSubtask` for in-file checklist items. So this is a genuine green
field, and the safe default matters more than raw parity with other tools.

## Decision (proposed)

### 1. Default is SOFT delete

`delete_task(task_id)` sets `status: cancelled` (a new value on the status enum) and
does **not** remove the file.

- Preserves backlink targets, keeps the artifact folder, and is trivially
  reversible (edit status back).
- Removes the task from default `list_tasks` / My Day / Backlog views, which already
  filter by status — this satisfies the "don't pollute the agent's list" goal
  *without destroying data*.
- Lowest-risk interaction with the self-write marker (single-file write).

### 2. HARD delete is an explicit, guarded opt-in

`delete_task(task_id, hard: true, cascade_subtasks: bool)`:

- If `hard: true` and child **tasks** exist and `cascade_subtasks: false` → **fail**
  with a structured error listing the child IDs (mirrors the #207 spec).
- Hard delete removes the `.md` file **and** its `<id>.artifacts/` folder.
- Returns `{ deleted_id, title, deleted_children: [...], deleted_artifacts: [...] }`.
- The entire cascade **acquires the `SelfWriteCoordinator` once and holds it** across
  every file operation, and writes a `_pending_operation.json` before starting
  (cleared on completion) for crash recovery.

### 3. Dangling backlinks are tolerated by design

Backlinks to a hard-deleted target are left dangling; the app already tolerates
unresolved links (ADR 0005). A "target missing" affordance may follow but is out of
scope here.

### 4. `delete_subtask` is a separate, low-risk tool

Removing an **in-file checklist subtask** is backed by the existing
`TaskService.DeleteSubtask` and is **not** governed by this ADR's soft/hard/cascade
machinery. It ships on its own track.

## Consequences

### Positive
- Safe default: reversible, link- and artifact-preserving, and it already reads as
  "gone" in every status-filtered view.
- Agents can still hard-delete when they genuinely mean it.
- Cascade risk (the dangerous part) is contained behind an explicit flag with a
  guard, a held coordinator, and a recovery log.

### Negative
- Adds a `cancelled` status value — a small surface change across views, filters,
  and the status enum/normalizer.
- Two deletion semantics to document and test.

### Neutral
- The hard-delete crash-recovery log (`_pending_operation.json`) is new infra,
  exercised only on the rare hard cascade.

## Open questions (resolve before moving to Accepted)

1. **Archive by MOVE or by STATUS?** Should soft delete *move* the file to a
   `<vault>/wiki/todo/.archive/` folder, or only flip `status: cancelled` and leave
   it in place? Move keeps `list_tasks` scans small; status-only is simpler and keeps
   Obsidian links resolvable.
2. **Reversal ergonomics.** Is "edit status back" enough, or do we want an explicit
   `restore_task` / `undelete` tool?
3. **PBIs.** Does hard-deleting a PBI container ever make sense, or should containers
   be soft-delete-only?

## Out of scope

- Bulk deletion.
- A UI affordance for dangling backlinks (ADR 0005 follow-up).
- Undo history beyond the reversible `cancelled` status.
