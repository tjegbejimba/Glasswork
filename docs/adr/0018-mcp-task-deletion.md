# ADR 0018: Task cancellation is a first-class terminal archive lifecycle

**Status**: Accepted
**Context slice**: Task Model, Vault Sync, Task Query, MCP Resource Mutation,
Presentation
**Relates to**: ADR 0002 (Task prose and Artifacts), ADR 0005 (Backlinks),
ADR 0007 (MCP mutation guarantees), ADR 0010 (Index), ADR 0016 (Task type)

## Context

Agents create Tasks during planning sessions and therefore also create
duplicates, mistakes, and work that is intentionally abandoned. Removing the
Task file is not a safe default:

- Backlinks and Task relationships would lose their target.
- The Task owns a sibling `<id>.artifacts/` folder.
- Child Tasks are separate files and require an explicit cascade policy.
- Multi-file removal needs self-write coordination and crash recovery.

The domain also needs to distinguish work that succeeded from work that was
abandoned. Reusing `done` would corrupt Work Log and completed metrics; generic
"delete" language would hide that distinction.

## Decision

### 1. `cancelled` is a canonical terminal Task status

A **Cancelled Task** has `status: cancelled`, `cancelled_at` as an RFC 3339 UTC
timestamp, and a non-empty `cancellation_reason`.

Cancellation is allowed only from `todo`, `in-progress`, or `blocked`. A done
Task is already terminal and must never be reclassified as cancelled.

Cancellation clears `my_day` so the Task cannot remain directly pinned. It
preserves due/start/defer dates, Description, Notes, Links, Artifacts,
`parent`, `blocked_by`, and other relationships. Completion and blocker
metadata are cleared because they describe incompatible lifecycle states.

### 2. Cancellation is archive, not completion

Cancelled Tasks are excluded from My Day, Backlog, Ready, Suggestions,
carryover, overdue, Work Log/completed metrics, generated actionable surfaces,
and default list/search/Task Query results.

The Task file remains the vault source of truth. A Cancelled Task remains
loadable by exact ID and can be enumerated only through an explicit
`status: cancelled` filter.

### 3. Restore is an explicit guarded transition

Restoring a Cancelled Task clears `cancelled_at` and `cancellation_reason`.
User and MCP restore defaults to `todo`.

Core accepts an explicit restore target of `todo` or `in-progress`. The latter
is a guarded seam for a later authoritative automation workflow; it is not
exposed as an arbitrary MCP restore choice in this layer.

### 4. Work Log owns the cancellation archive UI

Work Log remains one top-level Page with two tabs:

- **Completed** preserves the existing weekly completed-work log and metrics.
- **Cancelled** explicitly queries `status: cancelled`, orders newest-cancelled
  first with deterministic fallback ordering, and exposes Restore to Backlog.

The selected tab is UI state and persists outside the vault. Task Detail exposes
manual **Cancel task...** only for `todo`, `in-progress`, and `blocked` Tasks.
Manual cancellation supplies `Cancelled by user` when no reason is entered.
Both UI actions call the dedicated Task cancellation lifecycle seam; generic
status mutation remains unavailable for cancellation or restore.

Cancelled rows open Task Detail so the Hard-deletion danger zone remains
reachable. The rest of Cancelled Task Detail is read-only: title, Task fields,
Notes, Links, and Subtasks cannot be changed until Restore. The shared Task-edit
save controller enforces that policy against the persisted lifecycle state, not
only disabled controls.

### 5. MCP exposes lifecycle verbs

MCP exposes `cancel_task` and `restore_task`, not delete/undelete aliases.
Both use the Resource Revision and idempotency conventions from ADR 0007.
`cancel_task` requires a reason at the Core mutation seam; an omitted or blank
manual MCP reason is normalized to `Cancelled by agent`.

Generic Task status mutation does not accept `cancelled`, and a Cancelled Task
must be restored before ordinary mutation. This keeps lifecycle invariants
behind the cancellation module rather than duplicating them across callers.

### 6. Hard deletion is explicit and irreversible

**Hard deletion** is a separate operation, not a mode of Cancellation. It
applies equally to `task`, `bug`, and `pbi` Task types and never happens as a
default or automatic lifecycle transition.

The Core interface exposes a read-only deletion preflight and one guarded
mutation. The mutation requires:

- a client-generated `mutation_id`;
- the latest Resource Revision for the selected Task;
- `confirm_title` matching the current Task title with ordinal exactness;
- `cascade_children: true` when any descendants exist;
- the opaque `if_preflight_revision` returned by the reviewed preflight whenever
  cascade is enabled.

Missing guards, a stale Revision, title mismatch, descendants without cascade,
or a changed preflight revision fail before any Vault content changes. A
descendant failure returns the complete ordered descendant ID set and the full
preflight for an informed retry. A newly added descendant can therefore never
join an already-approved cascade silently.

### 7. The Task subtree owns its Artifact folders

Child Tasks are separate resources. Preflight resolves the complete descendant
subtree from fresh Task files, including the existing PBI ADO-parent identity
form. Without cascade the operation fails; with cascade it deletes every
descendant and reports each removed ID/title.

Every deleted Task owns `<taskId>.artifacts/`. The complete folder is backed up
and removed, including nested and non-Markdown files. The mutation report lists
every removed Artifact path. PBIs receive exactly the same guards and cascade
policy as other Tasks.

### 8. Exact inbound Wiki links are repaired first

Before deleting files, Core scans Vault Markdown pages, including surviving Task
files rather than assuming the external-only Backlink index is complete. It
uses the canonical Wiki-link parser and replaces only exact supported forms:

- `[[task-id|alias]]` becomes `alias`;
- `[[task-id]]` becomes the deleted Task title.

Multiple occurrences and every Task in a cascaded subtree are handled.
Unrelated targets and surrounding prose remain byte-for-byte unchanged.
Encoding, BOM, and line endings are preserved. Generated `_*.md` Task surfaces
and internal `.glasswork` state are not rewritten. Every edited Vault-relative
page and replacement count appears in the mutation report.

### 9. One recoverable multi-file mutation

Hard deletion deepens the Resource Mutation Module rather than adding a second
filesystem workflow. Under the Vault-scoped exclusive lease it:

1. captures a coherent preflight and revalidates the complete impact;
2. stages durable file/directory backups and intended replacement bytes under
   `wiki/todo/.glasswork/deletion-operations/`;
3. writes the existing atomic mutation journal;
4. applies exact page rewrites, atomically moves Artifact folders into hidden
   staged-deletion paths, then deletes Task files leaf-to-root;
5. marks the journal committed, records the idempotent result, and removes
   hidden staging state.

An ordinary pre-commit failure rolls every path back. Startup recovery rolls an
uncommitted journal back or finishes a committed journal, then reconstructs the
same replayable mutation report with its recovery outcome. Before rollback,
current paths must still match a journal-known original or staged state;
post-crash edits/re-creations block recovery rather than being overwritten.
Invalid/torn deletion journals retain their hidden backups and block managed
access until repaired instead of being archived as ordinary single-file journal
damage. Same-process writes register with `SelfWriteCoordinator`; Index and
Backlink index updates are coherent after commit and rollback. Cross-process MCP
changes remain visible to the desktop watcher paths from ADRs 0005 and 0010.

Deleting an in-file checklist Subtask remains a separate operation backed by
`TaskService.DeleteSubtask`; it does not delete a Task.

## Consequences

### Positive

- Abandoned work is reversible and does not pollute actionable or completion
  surfaces.
- Backlinks, relationships, prose, and Artifacts remain intact.
- Lifecycle validation, timestamping, Resource Revision, idempotency, and
  self-write notification stay concentrated behind one mutation seam.
- Users and agents can deliberately remove mistakes without leaving owned
  Artifacts or parser-supported inbound Wiki links dangling.

### Negative

- Every default Task selection must deliberately exclude the archive state.
- Consumers that need archived Tasks must request `status: cancelled`
  explicitly.
- Hard deletion scans the Vault and stages backups, so it is intentionally
  heavier than Cancellation.

### Neutral

- Vault scans still encounter cancelled files; cancellation changes selection
  policy rather than moving files to a separate folder.

## Out of scope

- Bulk cancellation or bulk restore.
- Undo history beyond explicit restore.
- Bulk Hard deletion.
- Automatic Hard deletion from ADO state or sprint automation.
- Undo after a committed Hard deletion; crash recovery is transactional
  completion/rollback, not user-facing undo history.
