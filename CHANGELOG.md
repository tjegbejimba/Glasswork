# Changelog

All notable changes to Glasswork are documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project broadly follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Glasswork ships two versioned components:

- **Glasswork.App** — the WinUI 3 desktop app (`src/Glasswork.App/Glasswork.csproj`)
- **Glasswork.Mcp** — the MCP server packaged as a dotnet tool (`src/Glasswork.Mcp/Glasswork.Mcp.csproj`)

Each component is versioned independently. Entries below note the component when changes are scoped.

## [Unreleased]

## [App 1.3.0 / Mcp 0.5.0] — 2026-05-26

First versioned release since `v1.2.0` (2026-04-20). This release covers ~35 merged PRs across the
Backlog board view, structured-links system, Index aggregate refactor, and a substantial MCP
surface expansion.

### Added

#### App
- **Backlog board view** — kanban-style read-only board with drag-to-change-status and undo for
  mark-done operations (#148, #149, #150).
- **Structured links** — first-class `TaskLink` model with a `LinkUriPolicy` deep module that
  generates per-type URIs, a read-only `## Links` section on `TaskDetailPage`, and full add/delete
  UI for all link types. Includes lazy `ado_link` migration from legacy frontmatter (#127, #128,
  #129, #162, #163, #164, #169).
- **Markdown rendering in cards** — Backlog and My Day card descriptions render markdown and
  surface `glasswork://` deep-links (#123).
- **Sticky reload/conflict banners** — banners on `TaskDetailPage` now pin to the top instead of
  scrolling away with content (#176).
- **Sun icon feedback** + quick agent-command menu on My Day (#152, #156).
- **`scripts/launch-ralph.ps1`** — wrapper that lets Ralph's TDD loop launch reliably from any
  PowerShell-spawned bash on Windows, plus agent-facing docs (#158).

#### Mcp
- **`load_context` tool** — gives agents the same project context the in-app skill loads (#137,
  #177).
- **`search_tasks` tool** — topic discovery across task content (#191).
- **`glasswork-import-sprint` skill** for importing ADO sprint items as todos (#179).
- **Detection-before-display pattern** applied across MCP tools so agents see error states before
  rendering (#141, #178).

### Changed

#### App
- **`IndexService` deepened into an in-memory aggregate with a delta channel** — central
  architectural refactor per ADR 0010. Migrated all consumers (`MainWindow`, `SettingsPage`,
  UI-state GC, `BacklogViewModel`, `MyDayViewModel`, `WorkLogService`, `TaskService`,
  `TaskDetailPage`) to read from `Index.Tasks` and subscribe to `Index.Changed` instead of
  re-loading the vault. Deleted `TaskFileChangedExternally`, `_indexDebouncer`, and the
  `Index.Refresh()` shim (#184, #186, #187, #188, #189, #190, #192, #194, #196, #197, #198, #199).
- **Card blurbs render as plain text** instead of full `VaultMarkdownView` for performance and
  visual consistency (#185, #193).
- **`copilot-instructions.md`** updated with the XAML init-order architectural hard rule (#154).
- **Triage workflow** stopped auto-assigning Copilot to user-report issues (#171).
- **`glasswork-start-work` skill** now transitions task status to `in-progress` at kickoff (#168).

### Fixed

#### App
- **XAML init-order crashes** on Backlog navigation — handlers that reach for sibling named
  elements during `InitializeComponent` now gate on null; full defensive sweep across pages
  (#153).
- **Board view empty-state regression** that left the board permanently blank after a status
  change via context menu, hardened against refresh-order races (#170, #181).
- **Scroll position preservation** across mark-done refresh on the Backlog (#183, carried forward
  through the Index migration in #197).
- **Flaky `Debouncer.Trigger_FiresAgainAfterQuietPeriodElapses`** — replaced `Thread.Sleep` with
  signal-based waits (#155, #195).
- **Build of `Glasswork.App` on main** after a regression (#151).
- **Launcher POSIX path conversion** and **detach behavior** for Git Bash (#159, #160).
- **GitHub Actions workflows** fail loudly when `COPILOT_ASSIGN_TOKEN` is missing or rejected
  instead of silently no-op'ing (#167).

### Internal / Docs
- ADR 0010 — Index in-memory aggregate with delta channel.
- Structured links design recorded as an ADR (#130).
- Backlog scroll-preservation and board empty-state regressions documented inline.

## [App 1.2.0] — 2026-04-20

Initial tagged release. Pre-1.2.0 history is preserved in `git log`.

[Unreleased]: https://github.com/tjegbejimba/Glasswork/compare/v1.3.0...HEAD
[App 1.3.0 / Mcp 0.5.0]: https://github.com/tjegbejimba/Glasswork/compare/v1.2.0...v1.3.0
[App 1.2.0]: https://github.com/tjegbejimba/Glasswork/releases/tag/v1.2.0
