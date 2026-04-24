# Changelog — glasswork-mcp

All notable changes to the `glasswork-mcp` MCP server are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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
