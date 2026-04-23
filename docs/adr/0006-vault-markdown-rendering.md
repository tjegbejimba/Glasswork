# ADR 0006: Vault markdown rendering — `VaultMarkdownView`, Notes read/edit toggle, Obsidian launch primitive

**Status**: Accepted
**Context slice**: resolves issue #71 (Obsidian-fidelity markdown rendering); supersedes parts of ADR 0003; sets foundation for issue #68 (Open-in-Obsidian everywhere)

## Context

ADR 0003 committed Glasswork to a native Markdig + multi-element WinUI renderer for **artifact** bodies, with a strict `ArtifactLinkPolicy`. That decision still stands. However, two things have shifted since:

1. **The ADR-0003 renderer was never built as specified.** The current `Controls/MarkdownTextBlock.cs` is the option-D shape that ADR 0003 explicitly rejected — a single `RichTextBlock` with an attached `Source` property, ~290 LOC. It works for the limited block set it supports (headings, paragraphs, inline links, inline code, plain bullet/number lists) but cannot host the code-block scroll containers, tables, or callouts that a fuller renderer needs. Every fidelity complaint about artifacts today bottoms out in this constraint.

2. **Notes is agentic too.** At the time of ADR 0003, `Task.Notes` was implicitly "primarily human-written." That assumption no longer holds — agents now edit Notes in the vault alongside humans. Notes content is therefore **untrusted on the same footing as artifacts** and must apply the same link policy, the same image policy, and the same malformed-input fallback. A plain `<TextBlock>` (the current binding at `TaskDetailPage.xaml:210`) is the wrong shape for agent-produced markdown.

Both surfaces now need:

- Full v1 Obsidian-fidelity rendering (GFM tables, task lists, strikethrough, callouts, syntax highlighting, wiki-links).
- Safe handling of untrusted content (link allowlist, blocked images, graceful malformed-input fallback).
- Live refresh when the underlying file changes — with a **conflict policy** for the Notes edit-mode case that ADR 0003 never had to consider.

In parallel, issue #68 wants "Open in Obsidian" affordances everywhere (context menus, hotkeys, header buttons). That work shrinks considerably if there is already a single primitive for launching Obsidian to a vault-relative path, which the new renderer needs anyway for wiki-link click behavior.

## Decision

### 1. Renderer component: single `VaultMarkdownView` UserControl

Build one renderer, used by both artifacts and Notes read mode. ADR 0003's `MarkdownArtifactView` name is retired — the type is renamed `VaultMarkdownView` because "vault-aware markdown" is the right scope (wiki-links, Obsidian launch, vault-relative paths).

- **Type**: `UserControl` in `Glasswork.App.Controls`.
- **Public API**:
  - `Markdown` — dependency property, `string`. Setting it triggers parse + rebuild of the internal `StackPanel`.
  - `LinkClicked` — event, `RoutedEventHandler<LinkClickedEventArgs>`. Fires after `ArtifactLinkPolicy` has decided. The page routes the click (URL launch, Obsidian launch, in-app navigation). The renderer does not know about the navigation stack.
- **No `RenderComplete` event** in v1. Not worth the surface area.
- **Delete** `Controls/MarkdownTextBlock.cs` in the implementation slice — it has no C# callers, only XAML bindings at `TaskDetailPage.xaml:359`, which the new renderer replaces.

The attached-property-on-`RichTextBlock` pattern of today is rejected: a multi-element renderer emits *blocks*, not inlines, so it needs a container it owns. A `UserControl` is the right grain for "a widget that generates a subtree from a string" and is far easier to debug in the Visual Tree than a custom `Control` with a `ControlTemplate`.

### 2. Renderer v1 feature set

| Feature | v1 | v2 | Notes |
|---|---|---|---|
| Headings, paragraphs, inline links, inline code | ✅ | | Already works today |
| Bullet / ordered lists | ✅ | | Native WinUI bullet/number markers (ADR 0003 path) |
| Code blocks (fenced + indented) | ✅ | | Themed `Border` → horizontal `ScrollViewer` → monospace `TextBlock` |
| GFM tables | ✅ | | Wide tables get a horizontal `ScrollViewer`; do not shrink-to-fit (collides with code inside cells and long URLs) |
| Task lists `- [ ]` / `- [x]` | ✅ (read-only) | toggle-through | Checkbox is visually disabled in v1. Clicking to rewrite the file is v2 |
| Strikethrough `~~foo~~` | ✅ | | |
| Wiki-links `[[stem]]` / `[[stem\|display]]` | ✅ | anchors `[[stem#section]]` | See §3 |
| Callouts `> [!note]` / `warning` / `tip` / `important` | ✅ (always-expanded) | foldable, more types | Unknown callout types fall back to plain blockquote |
| Syntax highlighting in code blocks | ✅ (ColorCode-Universal) | | Acceptable fallback if ColorCode proves heavy: themed background + language label; rich highlighting in v2 |
| Inline images `![alt](url)` | ❌ blocked | | Render `[image: alt]` placeholder. Image policy stays identical to ADR 0003 |
| Footnotes `[^1]` | ❌ | ✅ | |
| Heading anchors / ToC | ❌ | ✅ | |

### 3. Wiki-link behavior

Wiki-link parsing **reuses `BacklinkIndex`'s canonical regex verbatim**: `\[\[([^\]\|]+?)(?:\|([^\]]+))?\]\]`. Case-sensitive, supports aliases. Anchors `[[stem#section]]` are out for v1. No divergence — parsing the exact same string shape in two places is a bug waiting to happen.

Click routing at the page (via `LinkClicked`):

| Target resolves to… | Behavior |
|---|---|
| A Glasswork task (`*.md` directly under `wiki/todo/`, non-`_*` prefix) | Navigate in-app to TaskDetail for that task |
| Any other vault page | Open in Obsidian via `IObsidianLauncher` (see §5) |
| Nothing (no matching file) | Muted, non-interactive rendering — style as unresolved, no click handler. **We do not create pages on click.** Glasswork only writes inside `wiki/todo/`. |

Resolution uses the same `BacklinkIndex` lookup path for task detection; non-task resolution is a direct file probe against the vault root.

### 4. Notes edit UX — explicit toggle

Notes is read-only-rendered by default. An **Edit** button swaps the rendered view for a `TextBox` (the current edit surface at `TaskDetailPage.xaml:324`). **Done** swaps back and saves. Also:

- **`Ctrl+E`** keyboard shortcut toggles the mode when Notes has focus.
- **`Esc`** while in edit mode cancels (reverts the `TextBox` to the last saved value and swaps back). No "unsaved changes" prompt in v1 — the debounced autosave already fires on typing, so cancel-without-save is a genuinely unusual case.
- **Autosave preserved**: edits in the `TextBox` continue to flow through the existing debounced write path. "Done" flushes the debounce, "Esc" discards the in-memory buffer.

Rationale for the toggle (rather than always-rendered-click-to-edit): rendered markdown with clickable wiki-links cannot coexist with a text cursor on the same surface. Every inline-edit markdown widget that tries this ends up with janky affordances. The toggle is explicit, discoverable, and each mode gets a single clear set of interactions.

### 5. Live-file-change reflow + conflict policy

Both surfaces can change while the user is looking at them: artifacts via `ArtifactWatcherService`, Notes via agents or Obsidian writing to the task file. External changes debounce at **250ms** to match `BacklinksWatcher`.

| Surface | User state | Behavior |
|---|---|---|
| Artifact body | read-only in app | Silent re-render (the current watcher pattern, pointed at `VaultMarkdownView`). No conflict possible. |
| Notes | read mode | Silent re-render. No edit state, no conflict. |
| Notes | edit mode, **no unsaved user changes** | Silent re-render of the underlying content; `TextBox` buffer refreshed to match. |
| Notes | edit mode, **with unsaved user changes** | `InfoBar` conflict banner: *"Notes was changed externally."* Three actions: **Discard mine and reload**, **Keep mine and overwrite on save**, **Open Obsidian to merge**. |

The conflict case exists precisely because Notes is now agent-writable. Status-quo behavior (user buffer silently clobbers the agent write on next save) is a real data-loss bug under agent workloads. The three-button banner is a deliberately-lightweight alternative to a full 3-way merge UI — that's v2 if needed.

Implementation: the page tracks a hash of Notes-at-edit-start; when the watcher fires, compare against the current-file hash. Only show the banner if the hashes disagree **and** the `TextBox` buffer diverges from edit-start.

### 6. Obsidian launch primitive

Build `IObsidianLauncher.Open(string vaultRelativePath) : Task<bool>` in `Glasswork.Core.Services`.

- **URL shape**: `obsidian://open?vault=<name>&file=<url-encoded-path>`.
- **Vault name**: leaf of the configured vault-root path (e.g., `C:\Users\toegbeji\Wiki` → `Wiki`). Works for the conventional case where the folder name matches the registered vault name. A configurable `vault.obsidianName` override is a **future item** for users who rename vaults in Obsidian without renaming the folder.
- **Launch mechanism**: `Windows.System.Launcher.LaunchUriAsync`.
- **Fallback** when Obsidian isn't installed (Launcher returns `false`): surface a one-time-per-session `InfoBar` — *"Install Obsidian to open vault files externally"* — with a link to obsidian.md. No default-handler fallback (launching Notepad at the user unasked creates two editors racing for the same file with no conflict detection → silent data loss).
- **Return value**: `Task<bool>`. `true` = Launcher accepted, `false` = fallback triggered. Callers may branch (e.g., a context-menu click can show a dialog where an inline wiki-link click might be content to suppress further feedback).

This primitive is shared by wiki-link clicks (§3), the existing Backlinks section, and the future #68 affordances. Building it here shrinks #68 to pure UI work.

### 7. Untrusted-content policy — unchanged from ADR 0003, now also applies to Notes

- `ArtifactLinkPolicy.Decide(url)` applies to both surfaces with no per-surface override.
- Inline images are blocked identically on both surfaces in v1.
- On Markdig parse failure or an unexpected AST shape, fall back to a monospace `<TextBlock>` with the raw text and a subtle `(render failed)` caption. Visible but not alarming — parse failures should not feel like the app is broken.

## Alternatives considered

### A. Keep `MarkdownTextBlock` attached-property shape
- Adapt the existing `RichTextBlock` renderer to handle more block types.
- ❌ Code blocks need horizontal `ScrollViewer`, tables need a `Grid`, callouts need `Border` — none of which `RichTextBlock` can host as a block. The constraint is load-bearing, not incidental.
- **Rejected** — this is the option-D ADR 0003 already dismissed; the two years since have not changed `RichTextBlock`'s ceiling.

### B. WebView2 with generated HTML
- Full GFM / syntax highlighting / future mermaid out of the box.
- ❌ Same rejection as ADR 0003 §C. The size and safety-interception costs are still wrong for ≤10KB files that need to participate in page-level focus and selection.
- **Rejected for v1.** Reconsider only if requirements outgrow the native renderer (e.g., mermaid, LaTeX, embedded audio).

### C. Two UserControls — `ArtifactMarkdownView` and `NotesMarkdownView`
- Clean boundary if their behaviors ever diverge.
- ❌ Their behaviors are identical today. Divergence (if any) will be Notes-specific affordances — which can be a DP on the shared control, not a fork.
- **Rejected** — double the surface area for zero behavioral divergence.

### D. Inline-editable rendered markdown (always-rendered, click to edit)
- No explicit toggle; caret appears on focus.
- ❌ Wiki-link clicks and text-cursor placement compete on the same surface. Every widget that attempts this (Obsidian's own edit view included) has janky affordances. Mode separation is honest.
- **Rejected** — explicit toggle is clearer, simpler, and matches how every other editable-but-rendered surface in Glasswork works today.

### E. Silent last-write-wins for Notes conflicts (status quo)
- Zero implementation cost.
- ❌ Silent agent-write loss is a real bug once agents regularly touch Notes. Violates "vault is source of truth."
- **Rejected** — the conflict banner is a small line of code against a real correctness problem.

### F. 3-way merge dialog on Notes conflict
- Correct UX for the conflict case.
- ❌ 3–5× the implementation cost of a banner with three buttons. No merge-UI primitive exists in the app.
- **Deferred to v2** if the three-button banner proves insufficient in practice.

## Consequences

### Good

- **One renderer, two surfaces, one safety contract.** All markdown in the app goes through `VaultMarkdownView` and therefore through `ArtifactLinkPolicy`. Notes becomes agent-safe by construction.
- **Closes the ADR 0003 gap.** The renderer shape ADR 0003 described will actually exist.
- **#68 shrinks.** With `IObsidianLauncher` in place, "Open in Obsidian everywhere" becomes a UI-affordance-only issue.
- **Live-edit data-loss bug closed.** The Notes conflict banner replaces silent clobbering of agent writes.
- **Wiki-link UX aligns with the vault-first mental model.** Tasks navigate in-app; everything else defers to Obsidian; unresolved links don't create pages.

### Bad / accepted trade-offs

- **Meaningful implementation cost.** Expect ~800–1200 LOC across the renderer, link-routing, launcher, conflict detection, and tests. Larger than ADR 0003's ~400–600 LOC estimate because this slice does tables, callouts, syntax highlighting, wiki-links, and the Notes toggle.
- **Deferred interactivity.** Task-list toggle-through and foldable callouts are punted to v2. Both are real v2 items in the backlog.
- **Syntax highlighting dependency risk.** ColorCode-Universal is the plan; if it proves heavy or stale, fall back to themed background + language label and revisit in v2. This is the only third-party-library risk added by this slice.
- **Configurable vault name is future work.** The leaf-of-path heuristic can break for users who rename a vault in Obsidian without renaming the folder. Documented as a v2 item.

### Relationship to ADR 0003

ADR 0003 is **superseded in part** by this ADR:

- The **renderer choice** (Markdig + multi-element WinUI) is retained, but the type is renamed `VaultMarkdownView` and the scope expanded to Notes.
- The **block-to-element mapping table** in ADR 0003 §"Decision" is obsolete; see §2 above.
- The **safety policy** (allowlist, image block, malformed-input fallback) is retained and reinforced — now applies to Notes as well.

ADR 0003 is left in place as the historical record of the original artifact-rendering decision; future readers should start here.

### Reversible?

- The `VaultMarkdownView` UserControl boundary keeps the renderer swappable (to CT Markdown, to WebView2, to Labs `MarkdownTextBlock`) without touching consumers. That reversibility is the same commitment ADR 0003 made.
- The **safety contract, the wiki-link routing, and the Obsidian launcher** are the durable commitments — a future replacement renderer must satisfy the same contract, route wiki-links the same way, and use the same launcher.
- The **Notes edit toggle** is a UI affordance; replacing it with inline editing later would be a straightforward page-level change, not an architectural one.

## Why this ADR exists

- **Hard to reverse**: once the renderer, conflict detection, and launcher are wired into every page, swapping them means re-implementing the link policy, the wiki-link routing, the conflict policy, and the launcher contract. These become entangled with every consumer.
- **Surprising without context**: future readers will ask "why didn't we just extend `MarkdownTextBlock`?" and "why is Notes a toggle instead of inline-editable?" — this file is the answer.
- **Real trade-off**: Option B (WebView2) genuinely scales further. Option D (inline edit) is genuinely more convenient when it works. We chose native + toggle for specific reasons documented above.
