# ADR 0001: UI state lives outside the vault, in `%LocalAppData%\Glasswork\ui-state.json`

**Status**: Accepted
**Context slice**: visual polish pass — adaptive task rows with manual collapse override

## Context

The visual-polish slice introduces **adaptive task rows**: active tasks render as cards, quiet tasks as single lines. Users can manually collapse an active task to single-line form. This collapse state must persist across navigation and across app restarts.

Glasswork's existing persistence pattern is the **vault**: every meaningful piece of data lives as YAML frontmatter or markdown body in `.md` files inside the user's Obsidian folder. The vault is the source of truth — it survives reinstall, syncs across machines via Obsidian Sync / git, and is fully readable in plain text.

The collapse state is the first piece of Glasswork data that **clearly does not belong in the vault**:
- It describes the user's *view*, not the *task*.
- It is single-machine (different displays may want different collapse defaults).
- It pollutes git diffs and Obsidian Sync if stored in frontmatter.

We need a place for this kind of data, and we need it now. We also expect more such data soon (sidebar pane state, dismissed tips, last-viewed page, default sort order).

## Decision

UI state lives in **a JSON file at `%LocalAppData%\Glasswork\ui-state.json`**, accessed through a new service `IUiStateService` exposed via `App.UiState` (matching the existing service-locator pattern).

The service is **generic key/value** from day one:

```csharp
public interface IUiStateService
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
    void Save();    // explicit flush; usually called via Debouncer
}
```

Concrete impl `JsonFileUiStateService` lives in `Glasswork.Core.Services` (uses only `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` — pure .NET, no Windows-specific APIs, keeping Core portable).

**Writes are debounced ~500ms** via the existing `Debouncer` class.

**Stale entries are GC'd on app launch**: any keys referencing task ids that no longer exist in the vault are dropped during `App.OnLaunched`. Keeps the file from growing indefinitely as tasks come and go.

## Alternatives considered

### A. Store in vault frontmatter (`collapsed: true` per task)
- ✅ One source of truth, survives reinstall, syncs across machines.
- ❌ Pollutes the vault file with view state. Diffs become noisy. Obsidian Sync replicates per-machine view choices to other machines, which is *wrong* — different displays want different defaults.
- ❌ Sets a bad precedent. Once one UI bit is in frontmatter, others will follow, and the vault stops being a clean task store.
- **Rejected.**

### B. Per-task companion file (e.g., `task-id.glasswork.json` next to each `.md`)
- ✅ Keeps task metadata adjacent to the task.
- ❌ Doubles the file count in the vault folder.
- ❌ Obsidian shows these in its file tree as noise.
- ❌ Same "syncs across machines" problem as A.
- **Rejected.**

### C. SQLite database in `%LocalAppData%`
- ✅ Atomic writes, queryable, scales to large state.
- ❌ Massive overkill for what is currently <1KB of data.
- ❌ Adds a dependency (Microsoft.Data.Sqlite or equivalent) and a schema migration story.
- ❌ Harder to inspect/edit by hand if something goes wrong.
- **Rejected for now**; revisit if UI state grows past trivial.

### D. Collapse-specific service (`ICollapseStateService` with `IsCollapsed(taskId)` only)
- ✅ Tighter API, single purpose.
- ❌ Foreseeable next-use cases (sidebar pane state, dismissed welcome tip, "reduce motion" pref) would each need a new service or new method, leading to either many tiny services or feature-creep into this one.
- ❌ Same storage decision still has to be made — option D doesn't reduce risk, it just reduces flexibility.
- **Rejected** in favor of generic key/value.

## Consequences

### Good
- Vault stays clean. Source of truth for tasks remains pure markdown + frontmatter.
- New UI prefs cost ~1 line of code each (`App.UiState.Set("foo", true)`).
- Inspection / manual edit is trivial — just open the JSON file.
- No new package dependencies.
- Service is testable: `InMemoryUiStateService` fake for unit tests.

### Bad / accepted trade-offs
- Collapse state does **not** sync between machines. If you collapse a card on your laptop, it stays expanded on your desktop. Acceptable — different displays may genuinely want different defaults, and the user can re-collapse in 2 clicks.
- Reinstalling Glasswork loses UI state. Acceptable — the collapse state is recoverable from glance.
- Risk of misuse: someone could store *task data* in UI state, hiding it from the vault. Mitigation: name the service `IUiStateService` (not `IPreferencesService` or `IAppSettingsService`), document the boundary in `CONTEXT.md` ("if it describes the task, vault wins"), and code-review against the rule.

### Reversible?
Partially. The storage location and file format are easy to migrate (read old, write new). The decision to keep UI state out of the vault is the durable one — switching to "frontmatter for everything" later would re-pollute the vault. We expect to keep the boundary indefinitely.

## Why this ADR exists

The skill rule for ADRs: hard to reverse + surprising without context + real trade-off. This decision qualifies on all three:
- **Hard to reverse**: once users have collapse state in `%LocalAppData%`, migrating to frontmatter would silently re-sync arbitrary view choices to other machines — surprising for users.
- **Surprising without context**: a future contributor would reasonably ask "why isn't this in the vault like everything else?" — this file is the answer.
- **Real trade-off**: option A (frontmatter) is genuinely tempting for the "one source of truth" argument and we're explicitly choosing against it.
