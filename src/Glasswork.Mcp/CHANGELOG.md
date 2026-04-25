# Changelog — glasswork-mcp

All notable changes to the `glasswork-mcp` MCP server are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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
