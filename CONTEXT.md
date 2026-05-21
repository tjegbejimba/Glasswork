# Glasswork Context

> Domain map for Glasswork — a Windows-native (WinUI 3) todo + work-tracking
> app backed by an Obsidian vault. This file describes the bounded contexts,
> what each owns, and how they communicate. Pair with `UBIQUITOUS_LANGUAGE.md`
> for term definitions.

## High-level

Glasswork is a single-user desktop app that treats an Obsidian vault folder
as the source of truth for tasks. The vault is also the user's personal wiki,
so the app must coexist with arbitrary `.md` files it didn't author.

The app is **agentic by design** — most task content (summaries, subtasks,
notes) is expected to be written or assisted by AI agents working in the
vault. The UI surfaces this content; it does not own it.

## Bounded contexts

### 1. Vault Sync

Owns the on-disk truth. Reads and writes `.md` files in the vault folder,
parses YAML frontmatter, watches for external changes (Obsidian editing,
agent edits, git pulls), and serializes back without losing user content.

- **Source of truth for**: every `GlassworkTask`, every subtask, every note.
- **Key services**: `VaultService`, `FileWatcherService`, `SelfWriteCoordinator`.
- **Speaks to**: Task Model (parses files into models), Presentation (raises
  `TaskFileChangedExternally` events).
- **Does not own**: anything ephemeral, anything UI.

### 2. Task Model

The in-memory shape of a task and its subtasks. Pure C# in
`Glasswork.Core.Models`. No I/O, no UI dependencies.

- **Owns**: `GlassworkTask`, `SubTask`, status enums, derived helpers
  (`IsRich`, `ShowAsCard`, `IsEffectivelyDone`, etc.).
- **Speaks to**: Vault Sync (deserialized from), Presentation (bound to).
- **Does not own**: persistence, file paths, watch state.
- **Three-tier task prose model** (see ADR 0002):
  - `Task.Description` — stable framing prose, source of `Blurb`. Edited in-app.
  - `Task.Notes` — free-form scratch. Written by both humans and agents
    (agent-writable since #71). Edited in-app via an explicit read/edit
    toggle; rendered via `VaultMarkdownView` in read mode.
  - `Artifacts` — agent-produced markdown work-products in a sibling
    `<taskId>.artifacts/` folder. **Read-only in the app**; rendered via
    `VaultMarkdownView`.
- **Structured links** (see ADR 0009): `links:` is a typed frontmatter list of
  outbound task pointers (`ado`, `pr`, `incident`, `doc`, `build`, `other`) with
  `value` and optional `label`. Links are not a fourth prose tier; they are
  machine-readable task metadata adjacent to Description, Notes, and Artifacts.
  The v1 app surface is read-only, with editing in Obsidian or YAML.
- **Markdown rendering** (see ADR 0006, supersedes parts of ADR 0003):
  every rendered-markdown surface in the app (Artifacts, Notes read mode)
  goes through a single `VaultMarkdownView` UserControl in
  `Glasswork.App.Controls`. One renderer, one safety policy
  (`ArtifactLinkPolicy`), one wiki-link routing contract. All rendered
  content is treated as **untrusted** — agents produce it.

### 3. Index

In-memory aggregate over all tasks. Hydrated once at startup from
`VaultService.LoadAll()`; kept fresh thereafter via two parallel channels:

- **Same-process writes** → `VaultService.TaskWritten` / `TaskDeleted` domain
  events.
- **External edits (Obsidian / agents / MCP)** → `FileWatcherService.TaskFileChange`
  routed to `IndexService.OnFileChangedOnDisk`, which re-parses just the
  affected file and replaces that one entry. Parse failures keep the prior
  snapshot intact so partial in-flight writes don't blow away valid state.

Both paths emit a single typed `TasksChanged` delta carrying `Old` + `New`
snapshots per affected task, so filtered views (My Day, Backlog) can detect
removal-from-set as well as add and replace. Every query method returns
**defensive clones** (`GlassworkTask.Clone()`); the canonical store is never
exposed by reference. The `_index.md` / `_today.md` agent-facing markdown
surfaces are subscribers on the delta channel (debounced ~500ms), not
initiators. See ADR 0010 and issue #184.

- **Owns**: `IndexService`, the `TasksChanged` delta channel, the
  `_index.md` / `_today.md` writers, and the carryover / completed-between /
  by-id query surface that view models share.
- **Speaks to**: Task Model (consumes), Vault Sync (subscribes to events,
  reads on demand), Presentation (queried by pages, raises deltas to them).
- **Does not own**: the tasks themselves (Vault Sync owns disk truth), nor
  any UI state.

### 4. UI State *(new — this slice)*

Non-task user preferences that should persist across app restarts but
**must not pollute the vault**. Examples: which task cards the user has
manually collapsed, sidebar pane width, last-selected page.

- **Owns**: `IUiStateService`, JSON file in `%LocalAppData%\Glasswork\`.
- **Speaks to**: Presentation (read/write key-value).
- **Does not own**: anything in the vault, anything in the task model.
- **Boundary rule**: if the data describes a *task*, it lives in the vault.
  If it describes the *user's view of tasks*, it lives here. When in doubt,
  vault wins.
- **Lifecycle**: GC stale entries on app launch (drop entries whose taskId
  no longer exists in vault).

### 5. Presentation

WinUI 3 pages, controls, and view-state. Lives in `Glasswork.App`. Holds
no domain logic — composes the other contexts into screens.

- **Owns**: `MainWindow`, all `Pages/*`, all `Controls/*`, navigation,
  page-local view state, the `App` service-locator entry point.
- **Speaks to**: every other context (consumes services).
- **Default landing**: `MyDayPage` (Home Dashboard is a future concept).
- **Wiki view**: out of scope for now. Vault is also the user's personal
  wiki, but Glasswork only renders task `.md` files. A future Home Dashboard
  may surface non-task wiki notes — that's a deliberate extension point.

## Cross-cutting

- **Service locator pattern** — `App.Vault`, `App.Tasks`, `App.Index`,
  `App.UiState` etc. exposed as static properties on `App`. No DI container.
  When adding a new service, follow this shape.
- **Debouncing** — `Debouncer` class (500ms) is the standard for batching
  writes. Reused for both index regen and UI state writes.
- **Self-write tracking** — `SelfWriteCoordinator` suppresses watcher echoes
  from our own writes. Any new code that writes the vault must register
  with it, or watcher events will fire spuriously.
- **Virtual My Day promotion** — a task can be "in My Day today" without
  `task.MyDay` being set. Sources: task due-date, flagged subtask, or
  subtask due-date. Computed by `MyDayViewModel`; the vault is never
  written to reflect the promotion. Dismiss-for-today is the only
  per-day override and lives in `IUiStateService`. See ADR 0008.

## Out of scope (for this design slice)

- **Inbox** — common in todo apps; deliberately deferred.
- **Home Dashboard** — future surface that may aggregate tasks + wiki notes.
  `HomePage` is being deleted as part of this slice; a true dashboard would
  be a fresh design when revisited.
- **Wiki rendering** — Glasswork remains task-only for now.
- **Multi-vault / multi-user** — single-user, single-vault assumption holds.
