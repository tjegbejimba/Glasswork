# ADR 0005: Backlinks index and watcher pipeline

**Status**: Accepted
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

### Why the watcher does NOT register with `SelfWriteCoordinator`

Glasswork only writes to `wiki/todo/`. `BacklinksWatcher` explicitly excludes `wiki/todo/`. The two sets are disjoint by construction — the backlinks watcher will never see one of our own writes, so there is nothing to suppress. Recording this here so a future contributor doesn't add `SelfWrites` plumbing reflexively.

### Why refresh-section-only, never reload the task model

A backlinking edit happens on a wiki page that is **not** the open task. Reloading the task model in response would clobber any unsaved Notes the user is typing on the open task. The artifact pipeline made the same call (ADR pattern, not yet a numbered ADR); backlinks reuses it. The contract: a backlink change refreshes the Backlinks section in place; everything else on the page is untouched.

### Why in-memory rather than persisted

- The full scan of a ~10k-file vault completes well under the PRD's 2s soft target on cold start.
- A persisted index introduces staleness (vault edits made while Glasswork was closed) and a migration surface (schema changes).
- The cost of "rescan on every launch" is paid once per session and is not user-visible behind the existing splash flow.
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

- Cold-start does pay the full scan cost. **Mitigation**: scan is wrapped in try/catch and runs synchronously during `App.OnLaunched`; failures degrade to "no backlinks shown" rather than blocking startup.
- Three watchers means three `FileSystemWatcher` handles open against the vault. On Windows this is cheap; on a constrained system it is something to be aware of.

### Reversible?

The watcher topology is reversible — `IBacklinkIndex` is the durable contract. Persistence, on-demand scanning, or a unified watcher could be slotted in later without touching TaskDetail.

## Why this ADR exists

- **Hard to reverse**: once three pipelines exist with established subscribers, merging them later means rethreading every consumer.
- **Surprising without context**: a future contributor will reasonably ask "why a third watcher instead of extending the one we already have?" — this file is the answer.
- **Real trade-off**: option A (single watcher) is genuinely less code, and option B (persisted index) is genuinely faster on cold start at large vault sizes. We chose against both for specific reasons recorded above.
