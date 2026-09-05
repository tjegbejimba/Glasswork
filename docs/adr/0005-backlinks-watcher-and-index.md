# ADR 0005: Backlinks index and watcher pipeline

**Status**: Accepted
**Amended**: 2026-09-04 — Backlink and Research hydration now run as
generation-scoped supplemental initialization with explicit readiness,
failure, cancellation, retry, and lossless watcher handoff.
**Amended**: 2026-08-16 — guarded Hard deletion and the live Research Catalog
now observe precise writes outside `wiki/todo/`; the historical disjoint-write
assumption below is superseded by the same-process/cross-process rules in these
amendments.
**Context slice**: Backlinks feature (PRD #54, slices #55–#58); wiki PRD `wiki/decisions/glasswork-backlinks-prd.md`

## Context

A user working a Glasswork task accretes knowledge in the surrounding wiki: concepts, decisions, incidents, systems. The convention (`wiki/concepts/glasswork-task-linking.md`) is that those wiki pages link **back** to the task with `[[task-id]]`. Until this slice, those incoming links were invisible from Glasswork — only Obsidian's backlinks panel showed them.

The Backlinks feature surfaces those incoming wiki references on TaskDetail (PRD #54). Three implementation shapes were on the table:

1. Extend the existing `FileWatcherService` (which watches `wiki/todo/` for task changes) to also notice the rest of the vault.
2. Persist a backlink index to disk and load it on startup.
3. Build the index in-memory at startup with a dedicated watcher pipeline for incremental updates.

## Decision

**Option 3.** Three tightly-scoped components, all in `Glasswork.Core`:

- **`IBacklinkIndex`** — pure in-memory index keyed by task id. `Build(vaultRoot)` does a full recursive scan; `UpdateForFile`/`RemoveForFile`/`Rename` apply incremental updates and return the affected task ids. No I/O outside the supplied vault root, no UI dependencies, no persistence.
- **`BacklinksWatcher`** — its own `FileSystemWatcher` rooted at the vault, recursive, `*.md` filter, `wiki/todo/` excluded. Debounces (~250ms) per-file, applies the matching index call, and raises `BacklinksChanged(affectedTaskIds)`. Does **not** reload the task model.
- **TaskDetail glue** — subscribes to the App-level `BacklinksChangedExternally` event and refreshes only the Backlinks section (and only when the open task is in the affected set).

### Why a separate watcher pipeline

`FileWatcherService` watches `wiki/todo/` only. The backlinks watcher must watch the **entire vault** recursively. Merging the two would mean:

- The task watcher starts firing for every concept/decision/incident edit, forcing every task-pipeline subscriber to filter.
- The artifact watcher (which already watches `<taskId>.artifacts/`) would overlap with a vault-wide watcher.
- Self-write coordination (which exists to suppress echoes from Glasswork's own writes inside `wiki/todo/`) would have to grow a second concept for "writes Glasswork did NOT make but should still notify on."

Three independent pipelines (task / artifact / backlinks), each with a single concern, is cleaner than one pipeline with mode-switching.

### Self-write coordination after guarded Hard deletion

The original implementation deliberately omitted `SelfWriteCoordinator`
because Glasswork wrote only under `wiki/todo/`. Hard deletion changes that
boundary: exact inbound Wiki links are rewritten in arbitrary Markdown pages
inside the Vault before their Task targets are removed.

`BacklinksWatcher` therefore receives the shared coordinator and applies the
same split established by ADR 0010:

- a **same-process** rewrite is suppressed at the watcher because the Resource
  Mutation Module refreshes the injected `IBacklinkIndex` directly and emits one
  coherent affected-ID event after commit;
- a **cross-process** MCP rewrite exists only in the marker file, not the
  desktop process's in-memory set, so the watcher still consumes it and refreshes
  the desktop Backlink index.

This prevents duplicate same-process updates without hiding MCP changes. The
task watcher remains scoped to top-level Task files, and Artifact watching
remains independent.

### Live Research Catalog watcher

The Research Catalog adds another independent vault-wide watcher because its
unit of refresh is a schema-governed Wiki Page, not a Task or Backlink entry.
It hydrates once, batches only the changed paths after a quiet period, and emits
stable Research Topic IDs for the resulting add, replace, rename, move, or
delete delta. A rename expressed by the file system as delete-plus-create is
applied as one batch so the stable Wiki Page ID remains continuous.

The watcher consumes only same-process registrations from
`SelfWriteCoordinator`; marker-only cross-process writes from agents or MCP
remain visible. Same-process event bursts are bounded to the watcher quiet
period so a later Obsidian or agent write to the same path is not hidden for the
coordinator's full TTL. Malformed or unreadable refreshes preserve the last
valid Topic snapshot and attach a dated diagnostic until a valid replacement
arrives.

### Supplemental initialization and readiness

Backlink and Research hydration are supplemental: neither is part of the
required Task-readiness boundary and neither may delay first-window creation.
`SupplementalInitializationCoordinator` starts both scans independently on
worker threads while preserving the stable `BacklinkIndex` and
`FileSystemResearchCatalog` instances used by existing consumers.

Each component publishes an immutable state:

- `Pending` — no attempt has started.
- `Loading` — a scan plus watcher handoff is in progress.
- `Ready` — a complete snapshot has been published and watcher changes observed
  during the scan have been reconciled.
- `Failed` — initialization did not publish an empty success; the error is
  available to the caller and the component may be retried independently.
- `Cancelled` / `Disposed` — late work from that attempt cannot publish.

Readiness reads are lock-free. Research callers use
`TryCaptureResearch(queryDate, out snapshot)` while the supplemental boundary
is active; it never enters the catalog scan lock and returns `false` until a
Ready snapshot exists for that freshness date. Existing synchronous
`IResearchCatalog.Capture` and `IBacklinkIndex.Build` contracts remain for
stateless or non-UI consumers.

`StateChanged` is raised after the immutable state is published. Completion
notifications arrive on a worker thread, so Presentation generation-checks and
dispatches them. A Ready transition is emitted even for a genuinely empty
snapshot; Presentation uses that transition to refresh Backlink-dependent
counts/ranking and Research surfaces rather than interpreting Pending as zero.

### Lossless scan-to-watcher handoff

The Backlink watcher is enabled before the baseline scan. Create, update,
delete, and rename events are buffered while the scan runs, then replayed
against the atomically published baseline before Ready. During this handoff,
same-process writes are buffered too: the normal suppression rule assumes the
mutation module already refreshed a live index, which is not safe while the
baseline snapshot is still private. Steady-state suppression remains unchanged,
so same-process writes do not echo and marker-only cross-process writes remain
visible.

Research likewise enables its watcher before hydration and drains queued paths
before Ready. Its published snapshot is replaced atomically and remains live as
later watcher batches arrive; malformed-page last-valid-snapshot behavior is
unchanged.

If either watcher reports an overflow during hydration, the incomplete buffered
delta set is discarded and one full reconciliation runs before Ready. A
steady-state Backlink overflow uses the same bounded rebuild and emits a broad
affected-ID notification after publication.

Cancellation and disposal never wait for a held scan. Disposal publishes
`Disposed`, cancels active attempts, and defers watcher/catalog cleanup until
active callbacks finish. Attempt numbers prevent cancelled, retried, disposed,
or prior-Vault completions from publishing over the current generation.

### Why refresh-section-only, never reload the task model

A backlinking edit happens on a wiki page that is **not** the open task. Reloading the task model in response would clobber any unsaved Notes the user is typing on the open task. The artifact pipeline made the same call (ADR pattern, not yet a numbered ADR); backlinks reuses it. The contract: a backlink change refreshes the Backlinks section in place; everything else on the page is untouched.

### Why in-memory rather than persisted

- The index remains reconstructible from the Vault without a persisted cache.
- A persisted index introduces staleness (vault edits made while Glasswork was closed) and a migration surface (schema changes).
- The cost of "rescan on every launch" is paid once per session as supplemental
  background work and is no longer on the first-window or required Task path.
- If the scan ever stops being fast enough, persistence can be added behind the same `IBacklinkIndex` interface without touching the watcher or the UI.

## Alternatives considered

### A. Extend `FileWatcherService` to vault-wide

Rejected — see "Why a separate watcher pipeline." Would entangle the task pipeline with the backlinks pipeline and force every task subscriber to grow a filter.

### B. Persisted index (e.g., SQLite or JSON snapshot)

Rejected for v1. Adds a staleness window (edits while the app is closed), a migration surface, and a cache-invalidation question — none of which buy us anything on a vault size where the in-memory scan is fast.

### C. On-demand scan when TaskDetail opens

Rejected — would make every task-open touch the entire vault, pushing latency into the foreground click path. Also defeats the live-update story (story #7: backlinks update within a second or two of saving), since on-demand has no way to push.

## Consequences

### Good

- Three single-concern watchers are easier to reason about than one multi-concern watcher.
- The index is pure C# in `Glasswork.Core`, fully testable against a temp-folder vault fixture (see `BacklinkIndexTests`, `BacklinkIndexIncrementalTests`, `BacklinksWatcherTests`).
- Refresh-section-only protects unsaved Notes — same property the artifact pipeline already established.
- Replaceable: swapping in a persisted index later is an `IBacklinkIndex` reimplementation, no caller changes.

### Bad / accepted trade-offs

- Each launch still pays the full scan cost in background work. Until Ready,
  Backlink-dependent counts/ranking and Research content are explicitly
  unavailable rather than represented as final empty results.
- Independent Task, Artifact, Backlink, and Research pipelines mean four
  `FileSystemWatcher` handles open against the vault. On Windows this is cheap;
  on a constrained system it is something to be aware of.

### Reversible?

The watcher topology is reversible — `IBacklinkIndex` is the durable contract. Persistence, on-demand scanning, or a unified watcher could be slotted in later without touching TaskDetail.

## Why this ADR exists

- **Hard to reverse**: once three pipelines exist with established subscribers, merging them later means rethreading every consumer.
- **Surprising without context**: a future contributor will reasonably ask "why a third watcher instead of extending the one we already have?" — this file is the answer.
- **Real trade-off**: option A (single watcher) is genuinely less code, and option B (persisted index) is genuinely faster on cold start at large vault sizes. We chose against both for specific reasons recorded above.
