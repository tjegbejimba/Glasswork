# ADR 0018: Task cancellation is a first-class terminal archive lifecycle

**Status**: Accepted
**Context slice**: Task Model, Vault Sync, Task Query, MCP Resource Mutation
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

### 4. MCP exposes lifecycle verbs

MCP exposes `cancel_task` and `restore_task`, not delete/undelete aliases.
Both use the Resource Revision and idempotency conventions from ADR 0007.
`cancel_task` requires a reason at the Core mutation seam; an omitted or blank
manual MCP reason is normalized to `Cancelled by agent`.

Generic Task status mutation does not accept `cancelled`, and a Cancelled Task
must be restored before ordinary mutation. This keeps lifecycle invariants
behind the cancellation module rather than duplicating them across callers.

### 5. Hard deletion is a later dependent layer

No hard-delete guarantee ships with this decision. A later layer may define
explicit guarded removal of the Task file, Artifact folder, and optional child
Task cascade, including self-write coordination, dangling-link policy, and
crash recovery. That work depends on the cancellation/archive lifecycle but is
not implied by it.

Deleting an in-file checklist Subtask remains a separate operation backed by
`TaskService.DeleteSubtask`; it does not delete a Task.

## Consequences

### Positive

- Abandoned work is reversible and does not pollute actionable or completion
  surfaces.
- Backlinks, relationships, prose, and Artifacts remain intact.
- Lifecycle validation, timestamping, Resource Revision, idempotency, and
  self-write notification stay concentrated behind one mutation seam.

### Negative

- Every default Task selection must deliberately exclude the archive state.
- Consumers that need archived Tasks must request `status: cancelled`
  explicitly.

### Neutral

- Vault scans still encounter cancelled files; cancellation changes selection
  policy rather than moving files to a separate folder.

## Out of scope

- Hard deletion and cascade policy.
- Bulk cancellation or bulk restore.
- A dedicated app UI for browsing or changing Cancelled Tasks.
- Undo history beyond explicit restore.
