# ADR 0003: Render artifact markdown with Markdig + a custom WinUI block renderer

**Status**: Accepted
**Context slice**: Artifacts feature (PRD `wiki/decisions/glasswork-artifacts-prd.md` §7, §12); resolves issue #48

## Context

Artifacts are markdown work-products attached to a task — typically agent-produced (plans, designs, investigations, summaries). They live as `.md` files in `<vault>/wiki/todo/<taskId>.artifacts/` and are surfaced in the task detail page beneath Notes, inside collapsible Expanders.

Per ADR 0002 and the PRD, **artifact bodies are read-only in the app**. The vault is the editing surface (Obsidian, agents). The app's job is to render them.

Two non-negotiable properties shape the renderer choice:

1. **Artifact content is untrusted.** Agents wrote it, possibly autonomously, and Glasswork is "agentic by design" (CONTEXT.md). The renderer must enforce a strict link allowlist, must not auto-load remote images, and must degrade gracefully on malformed input — never crash.
2. **Glasswork has dark mode** (added in a prior slice). Artifact rendering must theme correctly without per-element hand-tuning.

The PRD's slice-3 acceptance criteria require: headings, paragraphs, lists, code blocks, inline code, links, blockquotes; intercepted `LinkClicked`; remote images blocked or behind opt-in; safety policy under test.

## Decision

Render artifact markdown with **[Markdig](https://github.com/xoofx/markdig) parsing into an AST, then a custom block renderer that emits a `StackPanel` of WinUI elements**, one element per top-level block.

Block-to-element mapping (v1):

| Markdown block | WinUI element |
|---|---|
| Heading 1–6 | `RichTextBlock` with style key `Heading{N}` |
| Paragraph | `RichTextBlock` (inline runs, hyperlinks, code spans) |
| Bullet / ordered list | `StackPanel` of `Grid` rows (bullet/marker column + content column); nested lists indent recursively |
| Code block (fenced or indented) | `Border` (themed background, padding) → `ScrollViewer` (horizontal) → `TextBlock` (monospace, selectable) |
| Blockquote | `Border` (left accent rule) → inner `StackPanel` recursing into its blocks |
| Thematic break (`---`) | `Rectangle` (1px, themed brush) |
| Inline link | `Hyperlink` with explicit `Click` handler routed through the link policy |
| Inline code | `Run` with monospace font + themed background brush |
| Inline image | **Skipped in v1** — render alt text in italics; do not load `src` |
| Table | **Plain-text fallback in v1** — render the source markdown as a code block with a "table omitted" header |
| HTML block / raw HTML | Render literal text (no HTML parsing) |
| Unknown / unsupported block | Render literal source text (never throw) |

The renderer is a `UserControl` (`MarkdownArtifactView`) exposing a `Markdown` dependency property (string). Setting it triggers a parse and a rebuild of the child `StackPanel`.

### Link policy (allowlist, not blocklist)

| Scheme | Treatment |
|---|---|
| `https` | Open via `Launcher.LaunchUriAsync` |
| `obsidian` | Open via `Launcher.LaunchUriAsync` (with confirm dialog the first time per session) |
| `http` | Confirm dialog; if confirmed, launch |
| `file` | **Block** with toast: "links to local files are disabled" |
| `javascript`, `data`, `ms-*`, all other schemes | **Block** silently |
| Relative / no scheme | Treat as `https` if it looks like a hostname; otherwise block |

The policy lives in a `LinkPolicy` static class so it is unit-testable without a UI host.

### Image policy (v1)

- **Remote images (`http`, `https`)**: do not load. Render `*[alt text]*` in italics in place.
- **Local images (`file`, relative paths)**: do not load. Render alt text.
- A future opt-in could allow trusted-folder local images, but not in v1.

### Malformed input policy

- Markdig is wrapped in a `try`/`catch` at the parse boundary. On failure, the renderer falls back to a single `TextBlock` showing the raw source plus a small "could not parse markdown" banner.
- Per-block render errors are caught; the offending block becomes a literal `TextBlock` of its source, the rest of the document continues to render.

## Alternatives considered

### A. `CommunityToolkit.WinUI.UI.Controls.Markdown` 7.1.2
- ✅ Already a built control with `LinkClicked`, `ImageResolving`, `MarkdownText` property.
- ✅ Compatible with WinUI 3 / WindowsAppSDK 1.0+.
- ❌ **Effectively unmaintained** — last release November 2021. No commits to the rendering control in years. No graduation to mainline Toolkit 8+.
- ❌ Default behavior auto-loads remote images unless every hook is wired correctly. One missed `ImageResolving` subscription leaks remote loads.
- ❌ Parser internals are not under our control — fixing a rendering bug means waiting for a release that may never come.
- ❌ Theming is decent but not ideal for our newly added dark mode without overrides.
- **Rejected** on maintenance risk and the agentic-content safety profile, not on incompatibility. (Accuracy correction from initial scan: this control *does* work with WindowsAppSDK; the issue is staleness and safety-hook surface, not framework support.)

### B. CommunityToolkit Labs `MarkdownTextBlock`
- ✅ Newer, actively iterated.
- ❌ Has been Labs-status (experimental, no SLA, can disappear or change shape between releases) for an extended period without graduating.
- ❌ Same issue as A around image-load defaults requiring careful hook-up.
- **Rejected** — Labs dependency is not a safe long-term bet for a primary view.

### C. WebView2 hosting generated HTML
- ✅ Native HTML/CSS rendering, easy tables/syntax highlighting/full GFM.
- ✅ Sandboxed by default.
- ❌ Massive dependency for read-only display of <10KB markdown files.
- ❌ HTML sanitization, CSP, and navigation interception become their own subproject — the safety story is *harder*, not easier, because we have to intercept inside a browser sandbox.
- ❌ Async load, focus integration, and theme propagation are all extra work for an Expander body.
- ❌ Selection/copy across embedded WebView2 is awkward for the rest of the page.
- **Rejected for v1.** Reconsider if requirements grow to full GFM, syntax highlighting, mermaid diagrams, or rich images.

### D. Markdig + a single `RichTextBlock` projection
- ✅ Slightly less code than the multi-element renderer.
- ❌ Code blocks need horizontal scroll, max-height, and a themed background — `RichTextBlock` cannot host a `ScrollViewer`.
- ❌ Nested lists require manual indent computation; native bullet/numbering doesn't exist in `RichTextBlock`.
- ❌ Future tables would need a custom block container anyway.
- ❌ Single-container projection makes per-block error containment harder.
- **Rejected** — pushed past `RichTextBlock`'s natural ceiling. The multi-element shape is more honest about what's being built.

## Consequences

### Good
- Safety policy lives in our code, fully unit-testable. No reliance on a library defaulting to safe behavior.
- Theming is automatic via WinUI brush resources — dark mode "just works" because every element pulls from theme dictionaries.
- Selection and copy work per-element with native WinUI behavior (especially in code blocks, which are critical for agent-produced investigations users will want to copy from).
- Per-block error containment: a malformed code block doesn't take down the rest of the document.
- Markdig is the standard .NET markdown parser, mature and AST-based. Compatible with .NET 10. Pure parser, no UI dependencies.
- No churn risk from a stalled or experimental NuGet package.

### Bad / accepted trade-offs
- More code than slapping a third-party control onto the page. Honest estimate: **~400–600 LOC** for the renderer (block dispatch, element builders, link policy, fallback handlers) plus tests. Worth it given the safety + maintenance properties.
- v1 deliberately omits images, tables, and HTML rendering. **Mitigation**: explicit fallbacks (italic alt text for images, "table omitted" placeholder, literal source for unknown blocks) so unsupported syntax degrades visibly but never crashes.
- Future extension (e.g., adding table rendering when agent docs start using them heavily) is a real implementation cost — but it's *our* implementation cost on a known surface, not "fork a dead library."

### Reversible?
The library choice is reversible — the `MarkdownArtifactView` `UserControl` boundary means we could swap to CT or WebView2 later without touching consumers. The **safety policy** (allowlist, no remote images, malformed-input fallback) is the durable commitment; any replacement renderer must satisfy the same contract.

## Why this ADR exists

The skill rule for ADRs: hard to reverse + surprising without context + real trade-off. This decision qualifies on all three:

- **Hard to reverse**: once the block renderer + safety tests + ADR-0002 prose-tier model are in place, swapping renderers means re-implementing the link policy, the image policy, the malformed-input fallback, and the theming integration. The renderer choice and the safety contract become entangled.
- **Surprising without context**: a future contributor will reasonably ask "why didn't we use the existing CT MarkdownTextBlock?" — this file is the answer (maintenance + safety surface, not compatibility).
- **Real trade-off**: option A (CT stable) is genuinely more turn-key, and option C (WebView2) genuinely scales better to full GFM. We explicitly chose against both for the v1 read-only-artifact use case.
