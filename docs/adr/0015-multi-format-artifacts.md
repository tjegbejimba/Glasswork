---
status: accepted
---

# Multi-format artifacts with per-kind rendering and sandboxed HTML preview

Glasswork artifacts were markdown-only, identified by the `.md` extension and
rendered through `VaultMarkdownView`. We are extending artifacts to be
**agent-produced work-product files of any format**, rendered per **Artifact
kind** (Markdown / Html / Image / Text / Other), so agents can deliver
screenshots, generated HTML reports, data files, and logs — not just markdown.
The defining axis of an Artifact shifts from *format* to *authorship + access*
(agent-produced, read-only in the app); user-uploaded files (Attachments) stay
out of scope.

## Key decisions

- **Kind is derived from extension**, then cheaply validated (size before any
  text read; text/binary sniff for text-ish kinds; image-decode + pixel caps).
  Extensionless or unrecognized → `Other`.
- **`Body` becomes text-only and nullable.** Text content is read only for
  Markdown/Text kinds and only under a size cap; Image/Html(preview)/Other
  carry no string body. The model exposes `Kind`, `Size`, and a load-error
  signal so no surface can silently bind an empty body to a markdown renderer.
- **Completion signal moves off `.md`.** Previously the `.md` extension was
  doing double duty as both the type filter and the atomic-write completion
  signal. A committed artifact is now any file that is **not** transient, where
  transient = dotfiles, `*.tmp`/`*.part`, `~$*`, OS/junk files (`Thumbs.db`,
  `desktop.ini`, `.DS_Store`), hidden/system-attributed files, and files whose
  size/mtime have not been stable across a short quiet window. **All** writers
  (external agents *and* the MCP `add_artifact` tool) use atomic
  write-temp-then-rename.
- **Render strategy per kind:** Markdown → `VaultMarkdownView` (unchanged);
  Image → inline (`SvgImageSource` for SVG, which rasterizes and does not
  execute embedded script) with a source toggle for SVG; Text/code → inline
  inert text (no auto-linking), size-capped; HTML → **Source** view (default)
  plus an opt-in **Preview**; Other / over-cap → by reference with an
  Open-externally action.
- **Sandboxed HTML preview via WebView2** — see Considered Options. Lazy
  (created only on Preview click), **single live instance app-wide** owned by a
  dedicated UI-thread controller (opening another tears down the prior),
  script disabled, **all** navigation and resource requests blocked except the
  initial content, runtime-missing degrades to Source + Open-in-browser.
- **Open-externally is a trusted user action** via `Launcher.LaunchFileAsync`,
  outside `ArtifactLinkPolicy` (which continues to govern untrusted in-markdown
  links unchanged, including its `file:` block). Executable/script extensions
  (`.exe`, `.cmd`, `.bat`, `.ps1`, `.lnk`, `.url`, `.hta`, `.msi`, …) route to
  **Show in folder** instead of launching. "Open in Obsidian" stays scoped to
  Markdown kind.
- **MCP inlines by kind:** `get_task`/`load_context` inline bodies only for
  Markdown/Text under the cap; Html/Image/Other (and over-cap text) are listed
  by **vault-relative reference** (path + kind + size), never base64.
  `add_artifact` accepts text extensions (binary rejected; written directly to
  the filesystem by the agent) and returns `{ path, kind, size, inline,
  reason }` so agents know whether the artifact renders inline. Artifact body
  content stays out of task search.

## Considered options

- **No WebView2 (Source + Open-in-browser only).** Zero new dependency, fully
  consistent with ADRs 0003/0006. Rejected because the user explicitly wants
  inline rendering options for HTML; a source dump plus an external bounce is a
  weak answer for what is usually a finished visual report.
- **WebView2 with JavaScript enabled.** Rejected outright: agent-produced HTML
  is untrusted, and enabling script + network is exactly the remote-fetch /
  exfiltration surface `ArtifactLinkPolicy` exists to deny.
- **WebView2, sandboxed (chosen).** Script off, network/navigation blocked,
  lazy single-instance, runtime-missing fallback. Accepts the dependency for a
  tightly-scoped, inert preview only.

## Consequences

- **This reverses the explicit WebView2 rejection in ADR 0003 and ADR 0006**,
  but only for the scoped HTML-preview case; those ADRs' decision to keep
  *markdown* off WebView2 (via `VaultMarkdownView`) stands.
- Glasswork ships unpackaged self-contained, so the WebView2 Runtime may be
  absent — Preview must always have a working fallback, never a hard failure.
- Accepted losses: no in-app JS interactivity (JS-dependent charts/mermaid run
  only via Open-in-browser), and app theme/dark-mode does not propagate into
  agent HTML.
- Only one HTML preview is live at a time; a torn-down preview shows an
  explanatory placeholder with a re-activate affordance rather than vanishing.
