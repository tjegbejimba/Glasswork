# ADR 0007: `glasswork-mcp` — standalone MCP server for typed agent vault access

**Status**: Accepted
**Amended**: 2026-07-31 — the Resource Mutation Module is now the enforced
mutation boundary; the mtime and runtime-negotiation text below is superseded.
The shipped tool surface has grown well beyond the v1 four; see the
[2026-07-06 Amendment](#amendment--2026-07-06-shipped-tool-surface).
Task-bearing MCP queries now translate to the stateless fresh-Vault Task Query
adapter recorded by ADR 0010.
**Context slice**: resolves issue #67 (MCP server); depends on a new prerequisite issue (file-based `SelfWriteCoordinator`); loosely related to #84 (vault settings) and #69 (quick-capture).

## Context

Glasswork is agentic by design — most task content (descriptions, notes, artifacts) is written by AI agents working alongside the user. Today, agents reach the vault by either (a) the user copy-pasting prose into the app, or (b) the agent writing markdown files directly with no schema awareness. Both are lossy. Agents that *know* the vault schema would unlock:

1. **Mid-session task capture** (highest-frequency value): in a Copilot CLI session, "log a follow-up about X" becomes a tool call instead of an interruption.
2. **Correct artifact placement**: agents drop research notes / design docs into the right task's artifact folder with the right frontmatter, instead of leaving the user to file them.

Both happen multiple times a day. The remaining use cases (status updates, subtask edits, search) are real but less frequent and can be added incrementally.

The central design tension is that today's `SelfWriteCoordinator` is **in-process state** — a `ConcurrentDictionary` inside the running WinUI process. Any external writer (a separate-process MCP server, a future CLI, etc.) cannot consult it, so every external write would trip the app's "external change" banner via `TaskFileWatcher`. This must be solved before MCP exists.

## Decision

### 1. Process model: standalone executable

`glasswork-mcp` is a **separate-process console app** speaking MCP over stdio. It does not depend on the running Glasswork app and works whether the app is open or not.

- **Why not in-process inside the app**: Copilot CLI sessions happen at any time, including before the app is open. An in-process server would fail those calls.
- **Why not standalone + IPC handshake**: doubles failure modes (pipe broken, app starting up) for marginal benefit.

### 2. Self-write coordination: vault-local marker file (prerequisite)

`SelfWriteCoordinator` is refactored from in-memory `ConcurrentDictionary` to a vault-local file (e.g. `<vault>/.glasswork/recent-writes.json`) with the same TTL semantics. Both the running app's `TaskFileWatcher` and any external writer (MCP server, future tools) consult and update this file.

- **Tracked as a prerequisite issue**, separately mergeable, ships in the app alone before MCP depends on it.
- **TTL stays at 1500ms** — same as today. JSON file is rewritten on every self-write; entries past TTL are pruned on read.
- **Atomic writes**: temp file + `File.Move` with replace, same pattern the existing `TaskFileService` uses.
- **Not a long-term coordination protocol** — explicitly *not* a lock manager. Just a "this writer wrote this file recently, don't fire external-change for it" hint. Same semantics as today, different storage.

### 3. Tool surface: 4 tools at v1

Ship the minimum that powers use cases #1 and #3:

| Tool | Purpose |
|---|---|
| `add_task` | Create a new task file with frontmatter (title, description, status, optional parent) |
| `list_tasks` | Return summaries (id, title, status, parent) — agent uses this to find a parent task for #3 |
| `get_task` | Return full task content (frontmatter + Description + Notes + artifact filenames) for context |
| `add_artifact` | Create a sibling markdown file under the task's artifact folder |

**Explicitly deferred to a later milestone**: `update_task`, `add_note`, `set_status`, `list_subtasks`, `add_subtask`, `search_tasks`. The Core services for all of these already exist — they're cheap to add when the agent friction surfaces. *(Status: most of these shipped post-v1 — see the [2026-07-06 Amendment](#amendment--2026-07-06-shipped-tool-surface).)*

Tool implementations are **thin wrappers over `Glasswork.Core` services** (`TaskService`, `ArtifactPathResolver`, `FileSystemArtifactStore`). MCP is a transport layer, not a domain layer.

### 4. Vault discovery: env var → app state file → error

Lookup order on startup:

1. `GLASSWORK_VAULT` environment variable. If set, use it.
2. Fall back to reading the app's `IUiStateService` persisted vault path (`%LocalAppData%\Glasswork\` on Windows; equivalents on other OSes).
3. If neither resolves to an existing directory, exit with a clear error message naming both attempted sources.

**`vault.path` semantic (Issue #132 fix):**  
Both `GLASSWORK_VAULT` and `vault.path` refer to the **vault root** — the top-level directory Obsidian opens when selecting that vault. The task directory is always `<vault root>/wiki/todo`. MCP validates that the task directory exists at startup and exits with a clear diagnostic if it does not, preventing silent task-misplacement.

This works zero-config in the common case (user picked a vault in the app), and supports cross-machine / cross-OS / no-app scenarios via the env var. When #84 (configurable vault setting) ships, the "app state file" leg gets updated to read the new location — single-line change.

### 5. Concurrent edit semantics: Resource Revisions

**Amended by #404/#405/#413:** the original mtime-based write precondition below
is superseded by the Resource Mutation Module contract. Task-bearing reads
expose an opaque, versioned digest of the exact bytes used for the managed read.
Every public mutation requires a client-generated `mutation_id` and exactly one
applicable precondition: `if_absent: true` for creation or `if_revision` for an
existing resource. Missing inputs fail closed with `precondition_required`;
there is no unconditional compatibility fallback.

The only approved unmanaged bootstrap exceptions are the existing V1-to-V2
frontmatter migrations in `VaultService.MigrateToV2` and
`VaultService.MigrateAllToV2`. They run before managed access, register
Self-write markers, are idempotent, and are not part of the MCP mutation
surface. All normal Task and task-owned-file writes use the Resource Mutation
Module.

For any tool that modifies an existing file (initially none in v1; future `update_task` etc.):

- The tool reads the exact managed bytes and captures their Resource Revision.
- The write only succeeds if the supplied Revision still matches.
- On conflict, the tool returns a structured error containing the expected and
  current Revisions plus the current snapshot where applicable.

For `add_task` and `add_artifact` (creating new files), the equivalent check is "file does not exist." All writes go through the temp-file-then-rename pattern.

### 6. Read consistency: stateless, re-read every call

No in-process index, no caching. Task Query tools execute through
`FreshVaultTaskQuery`, which acquires one fresh coherent managed Vault snapshot
per call. Other reads such as `get_task` remain direct managed Vault reads.

- Single-user vaults are unlikely to have thousands of tasks; per-call YAML parsing should be sub-100ms.
- Agent call volume is bursty but low (5-20 calls per session, not per second).
- MCP processes are short-lived per agent session — caching has marginal benefit.
- Stateless eliminates "what if the cache and the marker disagree?" failure modes.

If profiling later shows `list_tasks` is hot, a short TTL cache can be added without breaking tool contracts.

### 7. Distribution: `dotnet tool install -g`

`Glasswork.Mcp` is packaged as a .NET global tool (`<PackAsTool>true</PackAsTool>`).

- For single-user development: `dotnet pack` + `dotnet tool install -g --add-source ./nupkg glasswork-mcp`. Wrapped in `scripts/install-mcp.ps1`.
- No NuGet.org publish required for v1. If shareable distribution is wanted later, `dotnet nuget push` is one command and the package is already in the right shape.
- Agent MCP config references the tool by name (`glasswork-mcp` on PATH) — no absolute paths, no install-dir brittleness.

### 8. Schema versioning: semver on binary, stay in 0.x

`glasswork-mcp` follows semver. The project remains in `0.x` indefinitely; breaking tool-shape changes require a minor bump and a `CHANGELOG.md` entry. Tool input/output JSON shapes are documented in `src/Glasswork.Mcp/README.md`.

**Amended by #404/#405/#413:** `get_capabilities` is the read-only contract
handshake. It reports contract version `1.0` and the complete stable capability
set: relation-aware queries, Resource Revisions, read assertions, typed
transactions, complete-set relationships, idempotency, and recoverable
all-or-none commit. There is no runtime downgrade or negotiation; clients must
fail before querying or mutating when the complete set is absent.

**Amended by #407:** `transact_tasks` is now the first shipped Resource
Mutation Module adapter. It accepts one ordered `set_task_fields` operation,
requires a client `mutation_id` and opaque `resource_revision`, and returns a
structured envelope containing the applied/no-op/error outcome and the
post-read Task snapshot. Request hashes are canonicalized for replay, stale
revisions are checked before no-op detection and immediately before commit, and
contradictory transaction/operation revisions are rejected.

The mutation boundary persists an atomic journal under
`<vault root>/wiki/todo/.glasswork/mutation-journal.json` before replacement.
An uncommitted journal restores the original bytes; a committed journal
reconstructs the idempotency outcome before it is removed. Replay state is
retained for 30 days. Managed reads recover pending journals before reading,
and the vault coordinator combines an in-process reader/writer gate with a
vault-scoped named mutex so the desktop app and standalone MCP process do not
race one another. Semantic no-ops do not rewrite hand-formatted Markdown.

### 9. Auth & scope

- **No network listener.** Stdio transport only.
- **No authentication.** Anyone who can spawn the binary already has filesystem access to the vault — auth would be theater.
- **Vault is the only writable surface.** MCP cannot touch app UI state, cannot shell out, cannot read or write files outside the vault root.
- **Path-traversal guard**: a single helper `EnsurePathInVault(path)` is used by every tool that takes a path-like input (artifact filenames, future task ids that map to paths). It resolves `..` and absolute paths and rejects anything not under the vault root.

### 10. Project layout

New project: `src/Glasswork.Mcp/Glasswork.Mcp.csproj`.

- Pure .NET 10 console app. Cross-platform (Windows/Linux/Mac), matching `Glasswork.Core`.
- References `Glasswork.Core` only — never `Glasswork.App` (Windows-only, would break Linux/Mac builds).
- Tests in `tests/Glasswork.Mcp.Tests/` (MSTest, separate project).
- Solution gains a third app project: `Glasswork.Core`, `Glasswork.App`, `Glasswork.Mcp`.

## Amendment — 2026-07-06: shipped tool surface

The v1 four-tool surface (§3) was the *starting* point, not the ceiling. As of
2026-07-06 `glasswork-mcp` ships **19 tools**; the "deferred" list in §3 has largely
landed. Current surface, grouped by verb:

- **Create**: `add_task` (full field coverage — description, parent, status, priority,
  due date, scheduled, my_day, notes, blocked_reason for blocked creation — plus `if_exists` idempotency), `add_artifact`,
  `add_link`
- **Read**: `list_tasks`, `get_task`, `search_tasks`, `list_subtasks`, `get_my_day`,
  `get_artifact`, `list_backlinks`, `list_overdue`, `get_activity`,
  `get_task_context`, `load_context`
- **Update**: `update_task` (title / status / description / notes [with append] /
  priority / parent / ado_link / ado_title / blocked_reason / blocked_from_status),
  `move_task`, `set_my_day`,
  `toggle_my_day`
- **Delete**: `remove_link`

### Remaining gaps (tracked as scoped children of umbrella #277)

- **Subtask writes** — `add_subtask`, `update_subtask` (status / title / notes),
  `promote_subtask`, `delete_subtask`. The Core methods (`VaultService.AddSubtask`,
  `TaskService.PromoteSubtask` / `DeleteSubtask`, plus the UI's subtask status-toggle
  path) already exist; only the MCP wrappers are missing. This is the largest gap —
  agents can read subtasks but not create or check them off.
- **`update_task` date fields** — `due_date` and `scheduled` are settable at creation
  (`add_task`) but have no update path, so an agent can't reschedule or clear a date.
- **`type` on create/update** — the model carries `type` (`task` / `pbi` / `bug`,
  ADR 0016) and `get_my_day` surfaces it, but `add_task` / `update_task` can't set it,
  so agents can't create a PBI or convert task↔PBI.
The boundaries in §9 (stdio-only, vault-only writes, path-traversal guard, Resource
Revision preconditions, self-write marker) continue to govern **every** tool added
above and every tool still to come.

### Amendment — guarded Hard deletion

`preflight_delete_task` returns the complete impact plus an opaque
`preflight_revision`. `delete_task` requires `mutation_id`, the latest
`if_revision`, exact `confirm_title`, explicit `cascade_children: true` when
descendants exist, and that reviewed preflight revision for cascade. It delegates
preflight, descendant resolution, Artifact ownership, exact inbound Wiki-link
repair, staged backups, rollback, startup recovery, and replay to the Core
Resource Mutation Module. MCP only serializes the structured preflight, complete
mutation report, and stable error codes. Capability discovery advertises
`guarded_hard_deletion`.

### Amendment — blocked task contract

Task-level status `blocked` is now first-class across the MCP surface. The
transport still delegates the transition rules to `Glasswork.Core`:

- `blocked_reason` is required whenever a task is newly moved to `status: blocked`.
- `blocked_from_status` is only agent-settable when repairing malformed blocked
  metadata (`status: blocked` with missing / invalid blocker fields on disk).
- Resuming a blocked task (to `todo` / `doing`) or completing it directly (`done`)
  clears all blocker metadata.
- Full task/context reads surface blocked metadata so agents can see when a task
  is blocked versus when it merely **Needs blocker details**.

### Amendment — relation-aware Task queries

The MCP server exposes relation-aware Task queries as a thin transport adapter.
The `query_tasks` tool maps parameters to `ITaskQuery.Execute` through
`FreshVaultTaskQuery`; Core applies typed parent/status/Task type/Tag and
`blocked_by` predicates, validates relationships, performs bounded deterministic
paging, owns the opaque cursor, and returns typed Tasks plus a deduplicated
`read_basis`. MCP only preserves the established JSON and error contracts.

## Consequences

### Positive

- Agents can now mutate the vault with full schema fidelity, not just text.
- The marker-file refactor permanently removes the "external writers trip the change banner" problem for *any* future external tool.
- MCP is a thin transport layer. Adding tools is mostly a matter of binding to an existing Core service.
- `Glasswork.Core` continues to do all the real work; the surface area for review on the MCP PR is small.

### Negative

- Disk I/O per write for the marker file. Single-user, single-machine — negligible in practice.
- Resource Revisions remove filesystem mtime from the concurrency contract.
- Vault discovery has two sources. Mitigated by env-var-first ordering and a clear error message on miss.

### Neutral

- One more project in the solution. Build times unaffected (Linux runner already builds Core; Windows runner adds ~2s).
- `glasswork-mcp` is its own versioned artifact independent of the app version. Acceptable for a transport-layer tool; documented in the README.

## Implementation milestones

Filed as sub-issues of #67:

- **Prerequisite**: refactor `SelfWriteCoordinator` to vault-local marker file (separate parent issue, blocks #67).
- **M1**: scaffold `Glasswork.Mcp` project + stdio MCP transport + vault discovery (env var → state file → error).
- **M2**: implement `add_task` + `list_tasks` (use case #1 end-to-end).
- **M3**: implement `get_task` + `add_artifact` (use case #3 end-to-end).
- **M4**: profiling / structured per-call logging (separate sub-issue, can ship in parallel).

## Out of scope

- Multi-vault support. Single active vault remains the model.
- Sync. Vault is a folder on disk; sync is the user's problem (OneDrive, Dropbox, git, etc.).
- Network transport (HTTP, named pipe, TCP). Stdio only.
- Linearizable compare-and-swap against arbitrary unmanaged writers remains out
  of scope; cooperating writers use Resource Revisions and the Vault-scoped
  mutation boundary.
- Auth, ACLs, sandboxing beyond the path-traversal guard.
- Tool surface beyond the four listed in §3 — explicitly deferred until friction is observed.
