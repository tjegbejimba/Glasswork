# ADR 0010: Index is an in-memory aggregate with a typed delta channel

**Status**: Accepted
**Context slice**: `IndexService`, `VaultService`, `FileWatcherService`,
`SelfWriteCoordinator`, every view model that used to call `VaultService.LoadAll()`.

## Context

Before this slice, `IndexService.Refresh()` was a pass-through: it called
`VaultService.LoadAll()` and wrote two agent-facing markdown surfaces
(`_index.md`, `_today.md`). The deep behaviour CONTEXT.md §3 already claimed —
"here is the current set of tasks, plus deltas when they change" — was nowhere.

The cost showed up in two ways:

1. **Eleven call sites of `LoadAll()`** — every status-bar refresh, every view
   model `Refresh`, every UI-state GC pass, the MCP server, and the
   `_indexDebouncer` itself each walked the directory, re-read every `*.md`,
   and ran `FrontmatterParser.Parse` on each. At ~100 tasks, hundreds of file
   reads and YAML+markdown parses per call.

2. **Watcher fan-out** — one external task-file change fired
   `FileWatcherService.TaskFileChanged`, which triggered `_indexDebouncer`
   *and* fanned out `TaskFileChangedExternally` to every subscribed page.
   Each page then called its own `Refresh()` → another `LoadAll()`. A single
   subtask checkbox toggle in Obsidian could produce **4–5 full vault
   re-reads** on the UI thread or its continuations, even when nothing
   user-visible had changed.

## Decision

Deepen `IndexService` into the in-memory aggregate it always claimed to be.

### Snapshot store, never shared references

`IndexService` owns `Dictionary<TaskId, GlassworkTask>` guarded by a single
lock. Every query (`All`, `ById`, `Carryover`, `CompletedBetween`) returns
**defensive clones** via the new `GlassworkTask.Clone()` method. `GlassworkTask`
is mutable, `TaskDetailPage` two-way binds to it, and view models hydrate
transient UI fields (`IsManuallyCollapsed`, `TodaysSubtasks`) onto it —
sharing the canonical references would let those mutations corrupt the index
and the delta comparisons. `Clone()` also resets the transient UI fields so
they never pollute the aggregate.

### Dependency inversion: vault → index, not index → vault.Save

`VaultService` raises `TaskWritten` and `TaskDeleted` domain events after each
successful disk write (Save and all five targeted edits, plus delete and
migration). `IndexService` subscribes in app composition (`App.InitVaultServices`).

Persistence is never failed or hung by indexing — `RaiseTaskWritten` /
`RaiseTaskDeleted` catch subscriber exceptions and log via `Debug.WriteLine`.
This direction also keeps `VaultService` decoupled from `IndexService` (no
back-pointer cycle, no nullable injection weakening the boundary).

### Richer watcher seam

`FileWatcherService` now emits a typed `TaskFileChange` payload alongside the
legacy `TaskFileChanged` string event:

```csharp
public sealed record TaskFileChange(
    TaskFileChangeKind Kind, // CreatedOrChanged | Deleted | Renamed
    string? OldFileName,      // populated for Renamed
    string  NewFileName);
```

The Index's `OnFileChangedOnDisk(change)` handles Created/Changed (replace),
Renamed (remove old id + add new id in a single delta), and Deleted (remove).
Parse failures from in-flight writes are caught and the prior snapshot is
left intact.

### MCP coherence — split `SelfWriteCoordinator` predicate

`SelfWriteCoordinator` historically treated **all** recent writes (same-process
in-memory + cross-process marker file) as suppressed. With an in-memory Index
in place, cross-process MCP writes would never reach the desktop UI.

The predicate is now split:

- **`IsOwnProcessWrite(path)`** — checks **only** the in-memory dictionary.
- **`IsSuppressed(path)`** — unchanged broader behaviour (in-memory ∪ marker file).

The Index path consumes the unsuppressed watcher signal so MCP writes refresh
the UI via the watcher round-trip. The existing "external-edit / conflict
banner" path keeps using `IsSuppressed` and so still suppresses banners on
our own cross-process writes.

### Delta payload with `Old` + `New`

```csharp
public sealed record TaskChange(GlassworkTask? Old, GlassworkTask? New);
public sealed class TasksChangedEventArgs : EventArgs
{
    public required IReadOnlyList<TaskChange> Changes { get; init; }
    public IEnumerable<GlassworkTask> Added   => ...;
    public IEnumerable<GlassworkTask> Removed => ...;
    public IEnumerable<TaskChange>    Changed => ...;
}
```

Coarse Added/Changed/Removed lets a filtered view (e.g. My Day) miss a
removal-from-set when a task's `my_day` is cleared — the new snapshot no
longer matches the predicate but the page sees no `Removed`. The `Old` + `New`
shape lets a future optimisation skip pages whose predicate matches neither
side. v1 view models re-run their predicate on any delta.

### Startup order

`App.InitVaultServices` now:

1. Constructs `VaultService` (no Index dependency).
2. Runs `Vault.MigrateAllToV2()` — **before** seeding so the Index never
   holds pre-migration parse artefacts.
3. Constructs `IndexService(vault)` and `EnsureLoaded()`s it. `EnsureLoaded`
   does **not** emit a delta — it's a snapshot, not a change.
4. Constructs `TaskService(Vault, Index)`.
5. UI-state GC iterates `Index.All` (no disk scan).
6. Wires `_indexDebouncer` to fire on `Index.TasksChanged` (debounced
   `_*.md` regeneration).
7. Starts the watcher; `FileWatcherService.TaskFileChange` →
   `Index.OnFileChangedOnDisk`.

The legacy `TaskFileChangedExternally` event still fires (driven by the
string-payload watcher event) so existing page subscribers — `MyDayPage`,
`BacklogPage`, `TaskDetailPage`, `MainWindow` — keep working. Migrating those
subscribers to `Index.TasksChanged` directly is a follow-up.

## Consequences

**Leverage** — eleven `LoadAll()` call sites collapsed to in-memory queries.
Page navigation no longer does disk I/O. The status-bar count is O(1).

**Locality** — "completed-this-week" and "carryover" predicates moved from
duplicated view-model code into named methods on `IndexService`.

**Testability** — the seam for "which tasks are on My Day" can now be
seeded in-memory (`new IndexService(vault); idx.EnsureLoaded();` for
disk-bound tests, or future fixtures that bypass the vault entirely).

**No regression for MCP** — ADR 0007 is unchanged. MCP remains stateless and
disk-bound; cross-process coherence with the desktop Index is deliberately
not attempted.

**Concurrency** — the in-memory dictionary is guarded by a single lock;
queries snapshot + clone under the lock; the `TasksChanged` event is raised
after the lock is released to avoid re-entrancy deadlocks.

## Alternatives considered

- **Index calls into `VaultService.OnSelfWrite(id)`** — rejected. Creates a
  cycle, couples persistence success to indexing success, and makes the
  layering harder to reason about. Domain events go the other way.
- **Coarse Added/Changed/Removed payload** — rejected. Filtered views need
  both `Old` and `New` to detect removal-from-set.
- **Share the Index with MCP via IPC** — rejected (or rather, deferred).
  Out-of-process coherence is a separate problem; ADR 0007's stateless
  re-read-per-call model stays intact.

## References

- Issue #184 — "Deepen Index into an in-memory aggregate with delta channel"
- ADR 0007 — MCP server (kept disk-bound)
- ADR 0008 — My Day virtual promotion (`MyDayPromotionPolicy` consumes
  `Index.All`)
