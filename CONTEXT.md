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

- **Source of truth for**: every `GlassworkTask`, every subtask, every note,
  every Research Topic opt-in, and every Research Change Log.
- **Key services**: `VaultService`, `ResourceMutationService`,
  `FileWatcherService`, `SelfWriteCoordinator`.
- **Speaks to**: Task Model (parses files into models), Presentation (raises
  `TaskFileChangedExternally` events).
- **Does not own**: anything ephemeral, anything UI.
- **Research write boundary** *(future)*: Glasswork may mutate only the
  namespaced `glasswork.research` metadata on eligible Wiki Pages and files
  under `wiki/research-logs/`. Topic synthesis prose remains LLM-maintained
  under the Wiki's governance, not app-edited.

### 2. Task Model

The in-memory shape of a task and its subtasks. Pure C# in
`Glasswork.Core.Models`. No I/O, no UI dependencies.

- **Owns**: `GlassworkTask`, `SubTask`, status enums, derived helpers
  (`IsRich`, `ShowAsCard`, `IsEffectivelyDone`, etc.), and computed
  Task actionability signals (`Ready`, `Urgency score`).
- **Blocked task metadata**: task-level blocking is first-class and lives in
  task frontmatter (`status: blocked`, `blocked_reason`, `blocked_at`,
  `blocked_from_status`). This is independent from blocked Subtasks: a blocked
  Subtask still drives the card's blocker row, but only a task whose own
  `status` is `blocked` is a **Blocked Task**.
- **Cancellation lifecycle**: `status: cancelled` is a terminal archive state,
  distinct from successful completion. `cancelled_at` and
  `cancellation_reason` live in Task frontmatter. Cancellation clears only
  `my_day` among user scheduling fields and preserves Task prose, dates, Links,
  Artifacts, and relationships. A Cancelled Task can be restored to `todo`;
  Authoritative ADO reconciliation is the only MCP automation seam that may use
  Core's guarded direct `in-progress` restore target. It validates the matching
  ADO identity and exact state, never reopens `done`, and never Hard-deletes.
  See ADR 0018.
- **Hard deletion**: a separate irreversible operation, never a Cancellation
  mode. It is available for every Task type only after a preflight and exact
  title/Resource Revision guards. Descendant Tasks require explicit full-subtree
  acknowledgement; owned Artifacts are removed and exact inbound Wiki links are
  rewritten before the Task files disappear. Cascades are bound to the opaque
  preflight revision the caller reviewed. The operation is journaled, backed up,
  all-or-none, and restart-recoverable; recovery fails closed rather than
  overwriting post-crash Vault changes. See ADR 0018.
- **Speaks to**: Vault Sync (deserialized from), Presentation (bound to).
- **Does not own**: persistence, file paths, watch state.
- **Three-tier task prose model** (see ADR 0002):
  - `Task.Description` — stable framing prose, source of `Blurb`. Edited in-app.
  - `Task.Notes` — free-form scratch. Written by both humans and agents
    (agent-writable since #71). Edited in-app via an explicit read/edit
    toggle; rendered via `VaultMarkdownView` in read mode.
  - `Artifacts` — agent-produced work-products in a sibling
    `<taskId>.artifacts/` folder, of **any format** (markdown, HTML, image,
    text/data, other), not just markdown. **Read-only in the app**; rendered
    per **Artifact kind** (see Markdown rendering below and ADR 0015). The
    defining boundary is authorship + access (agent-produced, read-only),
    not file format. User-uploaded files (Attachments) are out of scope.
- **Structured links** (see ADR 0009): `links:` is a typed frontmatter list of
  outbound task pointers (`ado`, `pr`, `incident`, `doc`, `build`, `other`) with
  `value` and optional `label`. Links are not a fourth prose tier; they are
  machine-readable task metadata adjacent to Description, Notes, and Artifacts.
  The v1 app surface is read-only, with editing in Obsidian or YAML.
- **Markdown rendering** (see ADR 0006, supersedes parts of ADR 0003):
  every rendered-**markdown** surface in the app (Markdown artifacts, Notes
  read mode) goes through a single `VaultMarkdownView` UserControl in
  `Glasswork.App.Controls`. One renderer, one safety policy
  (`ArtifactLinkPolicy`), one wiki-link routing contract. All rendered
  content is treated as **untrusted** — agents produce it.
- **Multi-format artifact rendering** (see ADR 0015): non-markdown Artifact
  kinds render by their own strategy — images inline, text/code inline (size-
  capped), HTML via a source view plus an opt-in **sandboxed WebView2**
  preview (script off, network blocked, single live instance, runtime-missing
  fallback), and Other kinds by reference with an Open-externally action.
  This deliberately reverses the WebView2 rejection in ADRs 0003/0006, scoped
  to untrusted agent-produced HTML only. `ArtifactLinkPolicy` is unchanged;
  Open-externally is a trusted user action via `Launcher.LaunchFileAsync`,
  outside that policy.

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

Hard deletion emits `TaskDeleted` for every removed Task and `TaskWritten` for
every surviving task page whose exact inbound Wiki links were rewritten. The
external-page Backlink index is refreshed directly for same-process deletion;
cross-process MCP rewrites converge through `BacklinksWatcher`.

- **Owns**: `IndexService`, the `TasksChanged` delta channel, the
  `_index.md` / `_today.md` writers, the carryover / by-id Index accessors,
  and **Task Query**, the
  canonical typed structural and relationship-aware retrieval module exposed
  through `ITaskQuery.Execute`. Task Query concentrates filtering, validation,
  deterministic ordering and paging, projections, actionability, Backlink
  counts, Resource Revisions, read basis, and completed-work windows behind one
  policy implementation.
- **Task Query adapters**: the warm Index adapter captures one defensive
  in-memory snapshot per execution; the stateless fresh-Vault adapter captures
  one managed disk snapshot per execution. Continuation cursors are opaque and
  Core-owned, but do not pin a historical snapshot: every page executes against
  a newly acquired coherent snapshot.
- **Task Query boundary**: explicit query time is required. Presentation shaping
  (PBI container grouping, Backlog rows, collection reconciliation, and Work Log
  markdown grouping) remains outside the module. Free-text Task search is a
  separate Task Model capability; transports may feed a Task Query prose
  projection into its matching/ranking policy without moving text matching
  into Task Query.
- **Blocked-task contract**: `_index.md` represents blocked state explicitly;
  `_today.md`, carryover, and other actionable projections exclude Blocked
  Tasks even when they have stale direct pins or overdue dates.
- **Cancelled-task contract**: exact-ID reads retain Cancelled Tasks, while
  default Task Query, free-text search, My Day, Backlog, Ready, Suggestions,
  overdue, carryover, and completed-work selections exclude them. Callers must
  request `status: cancelled` explicitly to enumerate the archive.
- **Speaks to**: Task Model (consumes), Vault Sync (subscribes to events,
  reads on demand), Presentation (queried by pages, raises deltas to them).
- **Does not own**: the tasks themselves (Vault Sync owns disk truth), nor
  any UI state.

### 4. Research *(future)*

The durable knowledge-return model over schema-governed Wiki Pages. A Wiki Page
becomes a Research Topic only through explicit Vault metadata; the original page
remains the source of truth and is never copied into a Research-owned document.

- **Owns**: Research Topic eligibility and opt-in, bounded Research context,
  Research freshness, Research Change Logs, and Research handoff semantics.
- **Research context**: the Topic plus one direct hop of outgoing Wiki links,
  provenance references, and Backlinks, adjusted by durable include/exclude
  overrides. It never expands transitively or includes Research Change Logs.
- **Agent boundary**: a Research Session starts with visibly selected Research
  context, may discover new cited primary evidence, and writes durable learning
  through the Wiki's existing governance. Chat history is not domain state.
- **Work boundary**: Tasks and Wayfinder maps/tickets remain Related Work with
  their own lifecycle. Research may hand off to them explicitly but is never
  converted into work or completed by it.
- **Speaks to**: Vault Sync (current Wiki Pages, opt-in metadata, Change Logs),
  Index (related Tasks), and Presentation (Research Page).
- **Does not own**: Wiki synthesis prose, Task workflow state, Wayfinder state,
  Copilot session history, or general Vault browsing.

### 5. UI State *(new — this slice)*

Non-task user preferences that should persist across app restarts but
**must not pollute the vault**. Examples: which task cards the user has
manually collapsed, sidebar pane width, last-selected page.

- **Owns**: `IUiStateService`, JSON file in `%LocalAppData%\Glasswork\`,
  and app-local **Saved Task views** (named filters over Tasks).
- **Speaks to**: Presentation (read/write key-value).
- **Does not own**: anything in the vault, anything in the task model.
- **Boundary rule**: if the data describes a *task*, it lives in the vault.
  If it describes the *user's view of tasks*, it lives here. A Wiki Page's
  explicit opt-in as a **Research Topic** describes that durable knowledge
  page, not one machine's view of it, so the opt-in lives in the vault. When
  in doubt, vault wins.
- **Lifecycle**: GC stale entries on app launch (drop entries whose taskId
  no longer exists in vault).

### 6. Presentation

WinUI 3 pages, controls, and view-state. Lives in `Glasswork.App`. Holds
no domain logic — composes the other contexts into screens.

- **Owns**: `MainWindow`, all `Pages/*`, all `Controls/*`, navigation,
  page-local view state, the `App` service-locator entry point.
- **Blocked-task surfaces**: Backlog renders a dedicated blocked board column /
  list section; Task Detail owns the user-facing actions to mark blocked, edit
  blocker details, repair malformed blocked metadata, resume, override the
  resume target, or complete directly.
- **Cancellation surfaces**: Work Log remains the top-level Page and separates
  successful work from the archive with Completed and Cancelled tabs. Cancelled
  is newest-first and restores Tasks to Backlog through the guarded lifecycle
  seam. Task Detail exposes manual Cancellation only for active Tasks.
- **Hard-deletion surface**: Task Detail owns a distinct danger zone for every
  Task type and lifecycle state. A preflight previews descendant, Artifact, and
  inbound-link impact; exact title entry and explicit cascade acknowledgement
  gate the irreversible action. Cancelled rows in Work Log open Task Detail so
  archived Tasks remain eligible without weakening Restore. Cancelled Task
  Detail is otherwise read-only; ordinary edits require Restore first.
- **Speaks to**: every other context (consumes services).
- **Default landing**: `MyDayPage` (Home Dashboard is a future concept).
- **Research Page** *(future)*: a focused, two-pane reading library over
  explicitly opted-in **Research Topics**. It surfaces schema-governed Wiki
  knowledge, Research freshness, bounded Research context, Related Work, and
  explicit Research Session actions. It is not a general Wiki browser or a
  revival of the deleted Home Dashboard.

### 7. App Update

Keeps the installed app current with its GitHub releases. Owns nothing in the
vault and nothing in the task model — it is a self-contained capability that
spans Core (pure version logic) and App (network, process orchestration, UI).
See ADR 0020 (supersedes ADR 0011's apply mechanism).

- **Owns**:
  - **Detection** — an unauthenticated HTTPS client that reads the latest
    release `tag_name` from the public GitHub API, plus the pure SemVer
    comparison in `Glasswork.Core` that yields whether an **Update Check**
    found a newer **Available Version** than the **Installed Version**.
  - **Apply** — the **Self-Update** orchestration: spawn the bundled detached
    **Updater** (`Updater\release-update.ps1`), self-close, and let the updater
    download the matching Windows **Release package**, verify its SHA-256
    sidecar, swap the install with rollback, and relaunch behind an "Updating
    Glasswork…" progress window.
  - The Settings "Updates" section (the action surface) and the My Day
    update-available **InfoBar** + Settings nav dot (the announce surface).
- **Speaks to**: Presentation (renders the badge + Settings section) and
  **Release publication** (consumes the GitHub Release tag and assets it
  creates).
- **Does not own**: the vault, the task model, or any task data. The updater
  writes only outside the vault (UI State + install dir), so it does **not**
  interact with `FileWatcherService` / `SelfWriteCoordinator`. It also does
  not decide when a merged PR becomes an app-visible update; that is owned by
  **Release publication**.
- **Boundary rule**: detection must never block launch or fail loudly; apply
  must never leave the user without a working app — failed verification or
  installation preserves and relaunches the Installed version. See ADR 0020.

### 8. Release publication

Turns an intentionally chosen commit on `main` into the GitHub Release tag that
App Update consumes as the **Available version**.

- **Owns**: the agent-prepared **Release PR** that bumps the app version in
  `src\Glasswork.App\Glasswork.csproj` and commits **Release notes** at
  `docs\releases\vX.Y.Z.md`, and the
  **Release workflow** whose only input is `version` in `X.Y.Z` form. The
  workflow derives tag `vX.Y.Z`, validates the requested tag matches the
  committed version, reads `docs\releases\vX.Y.Z.md`, and creates the GitHub
  Release for the current `main` HEAD with those notes, a stable-named
  `Glasswork-win-x64.zip` asset, and its SHA-256 sidecar.
  The workflow also runs Core tests and a Windows Release x64 app publish before
  tagging, so `/releases/latest` never advertises a version that cannot build.
- **Speaks to**: App Update by publishing the release tag and Release package
  consumed by an **Update check** and **Self-update**.
- **Does not own**: installing the app on the user's machine; that remains
  **Self-update**.
- **Boundary rule**: merging a normal PR to `main` is not an app-visible update.
  Only an explicit **Release publication** creates a new **Available version**.
  Existing release tags are immutable: the **Release workflow** fails if the
  requested tag or release already exists, rather than moving a tag or
  rewriting the app-visible version. When a user asks an agent to release
  without specifying a version, the agent prepares a patch bump by default;
  minor or major bumps require explicit user instruction. The agent may
  auto-merge a clean **Release PR** after required checks; it must stop if
  checks fail or the PR diff contains anything beyond version and Release
  notes changes. If there are no substantive changes since the latest release
  tag, the agent stops with "nothing to release" instead of creating a no-op
  app-visible update. **Release notes** summarize the range from the latest
  published release tag to `main`, preferring merged PRs and linked issues when
  available and falling back to commit messages for direct commits; the Release
  PR itself is excluded. The notes file uses a concise template: release title,
  short summary, grouped `Changes`, and `Validation`; raw commit dumps are
  avoided.

## Cross-cutting

- **Service locator pattern** — `App.Vault`, `App.Tasks`, `App.Index`,
  `App.UiState` etc. exposed as static properties on `App`. No DI container.
  When adding a new service, follow this shape.
- **Debouncing** — `Debouncer` class (500ms) is the standard for batching
  writes. Reused for both index regen and UI state writes.
- **Self-write tracking** — `SelfWriteCoordinator` distinguishes same-process
  writes from cross-process MCP writes. Any new code that writes the vault must
  register with it, including Hard-deletion backlink rewrites outside
  `wiki/todo/` and the future narrow Research writer. Task and Backlink watcher
  pipelines suppress same-process echoes while still consuming cross-process
  writes needed to refresh desktop indexes.
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
- **General Wiki browsing** — the future Research Page is limited to explicitly
  opted-in, schema-governed Wiki Pages rather than exposing every Vault file.
- **Multi-vault / multi-user** — single-user, single-vault assumption holds.
