# Changelog — glasswork-mcp

All notable changes to the `glasswork-mcp` MCP server are documented here.
This project follows [Semantic Versioning](https://semver.org/).

---

## [0.11.1] — 2026-08-26

### Breaking

- None.

### Features

- None.

### Fixes

- None.

### Maintenance

- Add Ralph TDD loop infrastructure ([#334](https://github.com/tjegbejimba/Glasswork/pull/334)) — @tjegbejimba
- Resolve Research provenance through source summaries ([#492](https://github.com/tjegbejimba/Glasswork/pull/492)) — @tjegbejimba
- Authenticate MCP metadata requests when available ([#494](https://github.com/tjegbejimba/Glasswork/pull/494)) — @tjegbejimba
- Release Glasswork 1.4.8 ([#495](https://github.com/tjegbejimba/Glasswork/pull/495)) — @tjegbejimba
- Persist Planner sizes and define actionable scope ([#496](https://github.com/tjegbejimba/Glasswork/pull/496)) — @tjegbejimba
- Clarify locked MCP update failures ([#497](https://github.com/tjegbejimba/Glasswork/pull/497)) — @tjegbejimba
- Release Glasswork 1.4.9 ([#498](https://github.com/tjegbejimba/Glasswork/pull/498)) — @tjegbejimba
- Add Planner profiles and Not today recovery ([#499](https://github.com/tjegbejimba/Glasswork/pull/499)) — @tjegbejimba
- Enable side-by-side MCP updates ([#500](https://github.com/tjegbejimba/Glasswork/pull/500)) — @tjegbejimba
- Release Glasswork 1.4.10 ([#501](https://github.com/tjegbejimba/Glasswork/pull/501)) — @tjegbejimba
- Fall back from GitHub API rate limits ([#503](https://github.com/tjegbejimba/Glasswork/pull/503)) — @tjegbejimba
- Release Glasswork 1.4.11 ([#504](https://github.com/tjegbejimba/Glasswork/pull/504)) — @tjegbejimba
- Fix Ralph enqueue handling for Windows line endings ([#517](https://github.com/tjegbejimba/Glasswork/pull/517)) — @tjegbejimba
- Make Ralph coordinator preflight safe for worktrees ([#518](https://github.com/tjegbejimba/Glasswork/pull/518)) — @tjegbejimba
- Improve WinUI task scrolling and navigation performance ([#519](https://github.com/tjegbejimba/Glasswork/pull/519)) — @tjegbejimba
- Refresh Ralph's default worker model ([#520](https://github.com/tjegbejimba/Glasswork/pull/520)) — @tjegbejimba
- Improve Ralph linked-worktree startup ([#521](https://github.com/tjegbejimba/Glasswork/pull/521)) — @tjegbejimba
- Fix My Day crashes from stale row revisions ([#522](https://github.com/tjegbejimba/Glasswork/pull/522)) — @tjegbejimba
- Refresh Ralph launcher path handling ([#523](https://github.com/tjegbejimba/Glasswork/pull/523)) — @tjegbejimba
- Improve Ralph stale PRD recovery ([#524](https://github.com/tjegbejimba/Glasswork/pull/524)) — @tjegbejimba
- Refresh Ralph Windows startup lifecycle scripts ([#525](https://github.com/tjegbejimba/Glasswork/pull/525)) — @tjegbejimba
- Fix Research context file resolution ([#526](https://github.com/tjegbejimba/Glasswork/pull/526)) — @tjegbejimba
- Add one-click stable ID copying ([#527](https://github.com/tjegbejimba/Glasswork/pull/527)) — @tjegbejimba
- Refresh Ralph zero-item recovery scripts ([#528](https://github.com/tjegbejimba/Glasswork/pull/528)) — @tjegbejimba
- Automate App and MCP releases ([#530](https://github.com/tjegbejimba/Glasswork/pull/530)) — @tjegbejimba
- Refresh Ralph PRD branch recovery scripts ([#531](https://github.com/tjegbejimba/Glasswork/pull/531)) — @tjegbejimba
- Queue the next PRD slice ([#532](https://github.com/tjegbejimba/Glasswork/pull/532)) — @tjegbejimba
- Fix release evaluator native output parsing ([#534](https://github.com/tjegbejimba/Glasswork/pull/534)) — @tjegbejimba
- Queue the next PRD wave ([#535](https://github.com/tjegbejimba/Glasswork/pull/535)) — @tjegbejimba

---

## [0.11.0] — 2026-08-19

Version `0.10.0` remained a development-only local build. Version `0.11.0` is
the first package prepared for immutable GitHub Release publication and
includes all of its changes.

### Added

- **Immutable MCP publication**: a dedicated `Publish MCP` workflow validates
  the committed version and changelog, runs serial MCP tests plus Release
  build/pack gates, and creates an `mcp-vX.Y.Z` GitHub Release containing the
  exact package and SHA-256 assets. A recoverable draft is verified before an
  annotated tag anchors the source revision and checksum and the release is
  published. The app updater filters its own `vX.Y.Z` stream so MCP releases
  never become app updates.
- **Exact install verification**: `scripts/install-mcp.ps1 -Version X.Y.Z`
  downloads an exact published package, checks its tag-backed checksum and
  source revision, stages the tool in an isolated NuGet cache, and verifies the
  `X.Y.Z+commit` build identity before replacing an existing global install.
- **Build identity switch**: `glasswork-mcp --version` reports the package
  version plus source commit, allowing same-version stale local builds to be
  detected and replaced.
- **Authoritative ADO reconciliation**: `reconcile_ado_task` validates the
  matching imported ADO identity and applies only exact `Removed` Cancellation
  or exact resumed-active restoration directly to `doing`. The named
  `authoritative_ado_reconciliation` capability lets clients fail closed when
  this unreleased contract is not installed. Resource Revisions, idempotency,
  journal recovery, Self-write registration, and done-wins remain enforced by
  the Core mutation module.

---

## [0.9.0] — 2026-07-31

### Breaking

- **Fail-closed mutation contract** (issue #413): all public Task and
  task-owned-file mutations require a client `mutation_id` plus `if_absent`
  for creation or a Resource Revision for updates. Missing preconditions return
  `precondition_required` without side effects; the legacy `if_exists`
  compatibility modes are rejected. The MCP package version is now `0.9.0`.

### Added

- **Explicit-ID `transact_tasks` creation** (issue #408): agents can create a
  complete Task under a validated intended ID with `if_absent: true`. Creation
  returns the Task and Resource Revision, conflicts on an existing ID, replays
  exact mutation requests, rejects mutation-ID reuse, and recovers safely from
  replacement failures or lost responses.
- **Dependency-aware `query_tasks`** (issue #406): Tasks now round-trip the
  generic `blocked_by` relationship, and the new bounded query supports typed
  parent, status, Task type, Tag, and dependency-readiness predicates. Results
  use deterministic ordering and opaque continuation cursors, evaluate one page
  against one managed snapshot, and include a deduplicated `read_basis` with
  Resource Revisions. Invalid self-edges and missing dependency targets return
  structured validation diagnostics. This is the breaking `0.8.0` contract bump.
- **Resource Revisions and capability discovery** (issue #405): every Task-bearing
  read now includes an opaque `resource_revision` derived from the exact bytes
  used for that read, and the vault-independent `get_capabilities` tool reports
  contract version `1.0` plus the implemented and future workflow guarantees.
  This is the breaking `0.7.0` contract bump; clients must treat revision tokens
  as opaque equality values.
- **`update_task` tool** (issue #136): enables agents to modify existing tasks. Accepts `task_id` plus a `fields` object containing any of `title`, `status`, `description`, `notes`, `priority`, `parent_task_id`, `ado_link`, and `ado_title`. Only provided fields are written; omitted fields remain untouched on disk. Returns `{ task_id, updated_fields[] }` listing field names that actually changed (no-op fields excluded). Special handling: `notes` supports `{ value, append }` to append with a blank-line separator (`existing.TrimEnd() + "\n\n" + new`) instead of replacing; empty/null `parent_task_id` clears the parent. Error cases: `not_found` (task doesn't exist), `invalid_status` (unknown status value), `invalid_parent` (parent task doesn't exist). Write registered with `SelfWriteCoordinator` to suppress App's external-change banner. Trace phase `write` instrumented under `GLASSWORK_MCP_TRACE=1`.
- **`add_artifact` overwrite mode** (issue #134): optional `mode` parameter (`"create"` | `"overwrite"`). When `mode: "overwrite"`, the tool replaces the content of an existing artifact file instead of returning `{error: "conflict"}`. Defaults to `"create"` (create-only) for backward compatibility. Use `"overwrite"` for iterative agent workflows that refine artifacts (e.g., `plan.md`) across multiple turns without inventing `plan-v2.md` filenames. Path-traversal guards, `SelfWriteCoordinator` registration, and write-phase trace instrumentation still apply.

---

## [0.7.0] — 2026-06-03

### Added

- **`list_backlinks` tool** (issue #139): exposes backlink metadata for a given task to agents. Returns `{ backlinks[] }` where each entry contains `linking_page_path` (vault-root-relative with forward slashes), `linking_page_title`, `page_type` (`concept`|`decision`|`incident`|`system`|`other`), and `last_modified_utc` (ISO 8601). Reuses `Glasswork.Core.Services.BacklinkIndex` from ADR 0005 — the same scanner that powers the App's Backlinks section on TaskDetail. The index is built fresh on every call (stateless, per ADR 0007 §6).
  - Returns an empty `backlinks` array (not an error) when the task exists but has no incoming references.
  - Returns `{ "error": "not_found", "message": ... }` when `task_id` does not exist.
  - Per ADR 0005, the index excludes `wiki/todo/` — backlinks are **strictly incoming references from non-task wiki pages**. Task→task references are not returned (if wanted later, that belongs in a separate tool with a separate index tied to #172/#173's Children work).
  - Per-page deduplication: if a wiki page mentions a task 5 times, only 1 entry appears. Display text in wikilinks (`[[id|label]]`) is stripped by `WikiLinkParser` — the link still counts.
  - Under `GLASSWORK_MCP_TRACE=1`, emits the `backlinks_scan` trace phase.
- **`GlassworkToolsTests.ListBacklinks_*`** — MSTest coverage: empty backlinks, single backlink from a concept page, nonexistent task returning `not_found`, wikilinks with display text (`[[id|label]]`), and `ListBacklinks_WithTrace_EmitsBacklinksScanPhase` verifying the trace phase under `GLASSWORK_MCP_TRACE=1`.

---

## [0.5.0] — 2026-05-26

### Breaking

- **Paths in MCP tool output are now todo-relative and use forward slashes** (issue #133). All `path` fields returned by `add_task`, `list_tasks`, `get_task`, `add_artifact`, and every artifact path inside `load_context` (both root and subtree levels) are relative to `<vault>/wiki/todo/` and always use `/` as the separator — `add_task` no longer returns absolute paths like `C:\Users\…\wiki\todo\foo.md` and `get_task`/`add_artifact` no longer emit OS-shaped paths like `foo.artifacts\plan.md` on Windows. **Migration**: prepend `$GLASSWORK_VAULT/wiki/todo/` to recover the previous absolute form. The single exception is `load_context.backlinks[].source_path`, which stays relative to the **vault root** (e.g. `wiki/concepts/foo.md`) because backlinks point at pages outside `wiki/todo/`. Per ADR 0007 §8, this drives the minor-version bump.

### Added

- **Uniform structured error envelope across every tool** (issue #133). No tool throws `ArgumentException` (or any other exception triggered by user input) to the MCP transport. `add_task`, `list_tasks`, and `search_tasks` now return the structured `{ "error": "<code>", "message": "<text>" }` envelope on invalid input — matching the shape `get_task` / `add_artifact` / `load_context` already used.
  - `add_task` validates an empty title (`invalid_title`) and invalid status (`invalid_status`).
  - `list_tasks` validates invalid status (`invalid_status`).
  - `search_tasks` validates empty/too-long query (`invalid_query`), invalid `in` field (`invalid_in_field`), and invalid status filter (`invalid_status`); pre-validation runs in the MCP layer so error codes do not depend on `Glasswork.Core` exception messages.
  - `add_artifact` validates an empty filename (`invalid_filename`), a null `content` (`invalid_content`), and a `filename` containing a path separator (`path_traversal`). The path-separator check sits above `VaultPathGuard.EnsurePathInVault` because the guard only verifies the resolved path stays inside the artifact folder — a value like `nested/plan.md` passes that check but would then crash `File.WriteAllText` with `DirectoryNotFoundException` and escape the structured envelope.
- **Trace phase coverage for the remaining tools** (issue #133). Under `GLASSWORK_MCP_TRACE=1`:
  - `get_task` now emits `load_task` (the vault read) and `scan_artifacts` (the artifact-folder enumeration) phases.
  - `add_artifact` now emits a `write` phase around `File.WriteAllText`.
- **`list_tasks.fields[]` projection parameter** (issue #133). Optional `fields: string[]` argument. When omitted, null, or empty, the default summary shape (`id`, `title`, `status`, `parent_id`, `path`) is preserved byte-for-byte. When supplied, the summary contains the listed fields plus `id` (always included). Allowed values: `title`, `status`, `parent_id`, `path`, `created`, `priority`. Field names are case-folded, whitespace-trimmed, and de-duplicated; unknown names are silently dropped. `created` is rendered as `yyyy-MM-dd`.

### Internal

- `MapToInternalStatus` refactored to `TryMapToInternalStatus` with a try-pattern signature so call sites can early-return the structured envelope rather than throwing.
- All output-path construction now flows through `TodoRelativeTaskPath` / `TodoRelativeArtifactPath` / `NormalizeOutputPath` helpers. `Path.Combine` is reserved for filesystem operations only.

---

## [0.4.0] — 2026-05-15

### Added

- **`get_artifact` tool** (issue #138): reads a single artifact by `task_id` + `filename`. Returns `{ content, path }` (path is todo-relative, matching `add_artifact`'s output). On miss, returns `{ error: "not_found", message }`. On path-traversal attempt, returns `{ error: "path_traversal", message }`. `filename` must be a simple name with no path separators. Reads via `File.ReadAllText` after `VaultPathGuard.EnsurePathInVault`. Built for agent-to-agent handoff when only one artifact needs to be read. Under `GLASSWORK_MCP_TRACE=1`, emits `read_artifact` phase.
- **`get_task` artifact body embedding** (issue #138): optional `include_artifact_bodies: bool = false` parameter. When `true`, each entry in the `artifacts[]` response array includes a `content` field containing the full markdown body. When `false` or omitted, output is byte-identical to v0.3.0 (no `content` field). Built on top of the same `VaultPathGuard`-protected read path as `get_artifact`. Files that fail the guard are silently skipped (not fatal). `read_artifact` phase instrumented under `GLASSWORK_MCP_TRACE=1`.
- **`GlassworkToolsTests.GetArtifact_*`** and **`GlassworkToolsTests.GetTask_IncludeArtifactBodies_*`** — MSTest coverage: happy path, task-not-found, filename-not-found, path-traversal, and the three modes of `include_artifact_bodies` (true embeds content, false omits, default omits).
- **`McpLoggerTests.TraceEnabled_GetArtifact_IncludesReadArtifactPhase`** — MSTest test that verifies `read_artifact` phase appears in trace output under `GLASSWORK_MCP_TRACE=1`.
- **`load_context` tool** (M4, issue #137): single-call replacement for chaining `get_task` + N artifact reads + `list_tasks(parent_id)` + backlink discovery. Returns `{ task, artifacts[], subtasks[], backlinks[] }` for the given `task_id`:
  - `artifacts[]` — every artifact in the task's `<task-id>.artifacts/` folder, with `filename`, vault-relative `path`, and full `content` body inlined.
  - `subtasks[]` — every direct child task (and recursively their children to `depth`, default `1`, clamped to `[0, 3]`). Each subtree entry has the same `{ task, artifacts[], subtasks[] }` shape. `depth > 3` is silently clamped, not errored.
  - `backlinks[]` — every wiki page outside `wiki/todo/` that links to this task via `[[task-id]]`. Reuses `Glasswork.Core.Services.BacklinkIndex` (ADR 0005); no scanner re-implementation. Built per call against the vault root (stateless, ADR 0007 §6). v1 tradeoff: backlinks are root-only — subtree payloads do NOT carry a `backlinks` field, to keep latency and payload size bounded.
  - Returns the structured `{ "error": "not_found", "message": ... }` shape when `task_id` does not exist; the not-found check runs BEFORE the expensive backlink `Build` to keep misses cheap.
  - Cycle-safe BFS via a visited-id set, so hand-edited vaults with `a.parent = b ∧ b.parent = a` do not stack-overflow.
  - Under `GLASSWORK_MCP_TRACE=1`, emits per-phase timings `load_task`, `load_artifacts`, `load_subtasks`, `load_backlinks`.
- **`GlassworkToolsTests.LoadContext_*`** — MSTest coverage: leaf task, artifact body inlining, depth=1/2/0/clamp-to-3, backlinks via a wiki-concept page, `not_found` short-circuit, cycle safety, subtree shape (artifacts + nested subtasks but no `backlinks` field), and `McpLoggerTests.TraceEnabled_LoadContext_PhasesContainExpectedKeys` for the four phase keys.

---

## [0.3.0] — 2026-04-25

### Fixed

- **Bug 1 (stdout corruption)**: `Host.CreateApplicationBuilder` registered a default console logger that wrote to stdout, corrupting the stdio JSON-RPC transport. All Microsoft.Extensions.Hosting log providers are now cleared on startup and replaced with a console provider configured to write exclusively to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`). MCP clients (Copilot CLI, Claude Desktop) now receive only valid JSON-RPC frames on stdout.
- **Bug 2 (wrong task directory)**: `GlassworkTools` was initialising `VaultService` with the vault root path supplied by `GLASSWORK_VAULT`, causing `list_tasks`, `add_task`, `get_task`, and `add_artifact` to scan/write in the vault root instead of the `wiki/todo/` subdirectory where Glasswork tasks actually live. `GlassworkTools` now computes the task directory as `<GLASSWORK_VAULT>/wiki/todo/` and passes that to `VaultService` and `SelfWriteCoordinator`.
- **Bug 3 (stale version)**: Hard-coded server version in `Program.cs` and `Glasswork.Mcp.csproj` was stuck at `0.2.0` after M3 shipped. Both have been updated to `0.3.0`.

---

## [0.3.0-preview] — 2026-04-24

### Added

- **`get_task` tool** (M3): returns full task content — id, title, status, parent_id, description, notes, and an `artifacts` array listing filename + vault-relative path for every `.md` file in the task's artifact folder. Re-reads from disk on every call (no cache). Returns a structured `{ "error": "not_found", "message": ... }` response when the task ID does not resolve.
- **`add_artifact` tool** (M3): creates a new markdown artifact file under `<vault>/<task-id>.artifacts/<filename>`. `filename` must end in `.md`; `..`, absolute paths, and any path resolving outside the artifact folder are rejected with a structured `path_traversal` error. Returns a structured `conflict` error if the file already exists (create-only — no overwrite in v1). Registers the write with `SelfWriteCoordinator` so the running app's watcher does not fire a spurious "external change" banner.
- **`GlassworkToolsTests`** — MSTest coverage for both new tools: happy paths, `not_found` for missing tasks, `conflict` on duplicate artifact, `path_traversal` for `..` and absolute filenames, `invalid_filename` for non-`.md` extensions, SelfWriteCoordinator marker-file assertion, and an end-to-end round-trip (`add_artifact` → `get_task` sees the artifact).

---

## [0.2.0] — 2026-04-24

### Added

- **`add_task` tool** (M2): creates a new task file in the vault with correct frontmatter (id, title, status, parent, created timestamp). `description` (optional) becomes the Description body section per ADR 0002. Status defaults to `todo`; accepts `todo`, `doing` (mapped to `in-progress` internally), or `done`. Registers the write with `SelfWriteCoordinator` (vault-local marker file) so the running app's watcher does not fire a spurious "external change" banner.
- **`list_tasks` tool** (M2): re-reads the vault on every call (no cache, per ADR 0007 §6). Returns `{ tasks: [{ id, title, status, parent_id?, path }] }` sorted by created date ascending. Optional `status` and `parent_task_id` filters.
- **`GlassworkToolsTests`** — MSTest coverage for both tools: happy paths, optional fields, status mapping, SelfWriteCoordinator marker-file assertions, all filters, and empty-vault edge case.

---

## [0.1.0] — 2026-04-24

### Added

- **M1 scaffold**: new `Glasswork.Mcp` project targeting .NET 10, packaged as a `dotnet` global tool (`glasswork-mcp`).
- **MCP stdio transport** wired up via the official [ModelContextProtocol 1.2.0](https://www.nuget.org/packages/ModelContextProtocol) C# SDK. Server starts, advertises zero tools, and responds to the MCP `initialize` handshake.
- **Vault discovery** on startup: `GLASSWORK_VAULT` env var → `IUiStateService` persisted vault path → exit with a clear error message naming both attempted sources (see ADR 0007 §4).
- **`VaultPathGuard.EnsurePathInVault`** path-traversal guard used by all future tool implementations to reject `..` traversal and absolute paths outside the vault.
- **`VaultContext`** DI singleton carrying the resolved vault path to tool implementations.
- **`tests/Glasswork.Mcp.Tests`** MSTest project with unit tests for `VaultPathGuard`.
- **`scripts/install-mcp.ps1`** — one-command pack + global tool install / update.
- **`src/Glasswork.Mcp/README.md`** — installation guide, vault discovery order, Copilot CLI and Claude Desktop configuration examples.