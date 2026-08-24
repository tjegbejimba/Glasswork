# Copilot instructions for Glasswork

> These instructions are read by GitHub Copilot (chat, code completion, cloud
> coding agent). They sharpen investigations and drafts by pointing at the
> canonical sources of truth rather than re-describing them here.

## What this project is

Glasswork is a **single-user Windows-native (WinUI 3 / .NET 10) todo + work-tracking
app** backed by an Obsidian vault. Tasks are markdown files with YAML frontmatter
in the user's vault. The app is **agentic by design**: most task content
(summaries, subtasks, notes, artifacts) is written by AI agents; the UI surfaces
this content, it does not own it.

## Canonical references — read these before proposing changes

- **[`CONTEXT.md`](../CONTEXT.md)** — bounded contexts, three-tier task prose
  model, cross-cutting rules (service locator, debouncer, self-write tracking).
- **[`UBIQUITOUS_LANGUAGE.md`](../UBIQUITOUS_LANGUAGE.md)** — glossary. When
  discussing a domain concept, use the term exactly as defined here. If you
  need a term that isn't defined, flag it and propose a definition.
- **[`docs/adr/`](../docs/adr/)** — decisions already made. Always read the
  relevant ADRs before proposing anything that touches:
  - Artifact markdown rendering → ADR 0003 (partially superseded by 0006)
  - Vault markdown rendering / Notes edit model / Obsidian launcher → ADR 0006
  - Subtask row behavior → ADR 0004
  - Backlinks → ADR 0005
  - Task prose fields (Description / Notes / Artifacts split) → ADR 0002
  - UI state storage → ADR 0001
  - App Release publication → ADR 0012
  - Independent app/MCP GitHub Release streams → ADR 0023
  - Side-by-side MCP updates and Copilot command migration → ADR 0024

### WinUI 3 internals (when chasing platform behavior)

The WinUI repo's [`design-notes/`](https://github.com/microsoft/microsoft-ui-xaml/tree/winui3/main/src/docs/design-notes)
folder is the closest thing to authoritative documentation for WinUI's
internal model. Reach for these only when investigating platform behavior
that public docs don't explain — they're not required reading:

- [`loading-loaded-unloaded-events.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/loading-loaded-unloaded-events.md)
  — exact timing of `Loading` / `Loaded` / `Unloaded` (see hard rule 6).
- [`customtitlebar.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/customtitlebar.md)
  — the "glass window" model behind `TitleBar` + `ExtendsContentIntoTitleBar`.
  Useful when debugging drag regions, caption-button hit-test, or NC messages.
- [`xaml-object-lifetime.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/xaml-object-lifetime.md)
  — CCW/RCW reference-tracker model. Reach for this if a Page/Control isn't
  getting collected after navigation (common cause: a long-lived service
  closes over a short-lived UI element).
- [`unpackaged-apps.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/unpackaged-apps.md)
  — activation/lifetime quirks for unpackaged self-contained apps (Glasswork
  ships unpackaged); relevant context for the silent `STOWED_EXCEPTION` mode
  noted in hard rule 6.
- [`focus.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/focus.md),
  [`popup.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/popup.md),
  [`text-controls.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/text-controls.md)
  — useful for focus traversal, dialog behavior, and read-only text rendering
  (`VaultMarkdownView`).

## Build & test constraints

- **`Glasswork.Core`** — pure .NET 10, no Windows dependencies. **Builds and
  tests cleanly on Linux**, including the Copilot cloud agent runner. This is
  where domain logic lives.
- **`Glasswork.App`** — WinUI 3, **Windows-only**. Cannot be built or run on
  Linux runners. For cloud-agent triage, investigate statically (read code,
  trace call sites); do not attempt `dotnet build` on this project in cloud.
- **Tests** — MSTest. Command: `dotnet test tests/Glasswork.Tests/`. Do not
  add xUnit/NUnit or other test frameworks. **When implementing a feature
  or fix, follow [`.github/skills/tdd.md`](skills/tdd.md)** — vertical-slice
  red-green-refactor, not horizontal slices.
- **.NET SDK** — 10.x. Preinstalled in cloud agent via
  `.github/workflows/copilot-setup-steps.yml`.
- **MCP publication** — follow
  [`.github/skills/mcp-release.md`](skills/mcp-release.md). `mcp-vX.Y.Z`
  GitHub Releases are created only by the `Publish MCP` workflow from `main`;
  they never participate in the app `vX.Y.Z` release stream.

## Running Ralph (TDD loop) from PowerShell on Windows

Ralph (the autonomous TDD loop in `ralph-loop-dashboard`) drives one issue at
a time through red→green→refactor and merges the resulting PR. Locally on
Windows, **always use [`scripts/launch-ralph.ps1`](../scripts/launch-ralph.ps1)
to start it from any agent context** — including Copilot CLI agents running
in PowerShell.

```powershell
pwsh -File scripts\launch-ralph.ps1                  # default: status (read-only)
pwsh -File scripts\launch-ralph.ps1 -Action Launch   # start the loop detached
pwsh -File scripts\launch-ralph.ps1 -Action Stop     # stop launcher + active worker
```

**Why this exists**: `.ralph/launch.sh` background mode crashes Cygwin's fork
emulation when Git Bash is spawned from a non-Bash parent (PowerShell,
conhost, `Start-Process`):

```
bash 1026 dofork: child 1027 - died waiting for dll loading, errno 11
```

The wrapper sidesteps this by spawning `bash --foreground` via `Start-Process`
with a hidden window — no fork required. The Windows process is detached, so
the loop survives the agent session ending. See the script header comment for
the full rationale.

**When NOT to use it**: if you are typing in a real interactive Git Bash
window (not a PowerShell-spawned bash), `.ralph/launch.sh` works directly
and supports `RALPH_PARALLELISM>1`. The wrapper is foreground-only (one
worker). For real parallelism on Windows, install WSL2 and launch from
inside Ubuntu.

## Architectural hard rules

1. **Three-tier task prose model** (ADR 0002):
   - `Description` — stable framing, edited in-app.
   - `Notes` — free-form, edited in-app via explicit read/edit toggle,
     also writable by agents (since #71).
   - `Artifacts` — agent-produced sibling markdown files, **read-only in the
     app**. Never add a UI path that edits an artifact.

2. **Single markdown renderer** (ADR 0006). Every rendered-markdown surface
   goes through `VaultMarkdownView` (`Glasswork.App.Controls`). Do not
   resurrect `MarkdownTextBlock` or introduce a second renderer. All rendered
   content is **untrusted** (agent-produced); links go through
   `ArtifactLinkPolicy`.

3. **Vault is the source of truth.** If the data describes a *task*, it lives
   in the vault. If it describes the *user's view of tasks*, it lives in
   `IUiStateService` (`%LocalAppData%\Glasswork\`). When in doubt, vault wins.

4. **Service locator over DI.** `App.Vault`, `App.Tasks`, `App.Index`,
   `App.UiState` — new services follow this shape. No DI container.

5. **Any code that writes the vault must register with `SelfWriteCoordinator`**
   or `FileWatcherService` will fire spurious external-change events.

6. **WinUI XAML event handlers must not unconditionally dereference other
   named XAML elements.** Initial-state attributes (`IsChecked="True"`,
   `SelectedIndex="0"`, `IsSelected="True"`, `Value=`, `IsOn="True"`, etc.)
   fire their corresponding `Changed`/`Checked`/`SelectionChanged` events
   *during* `InitializeComponent`, in document order — sibling controls
   declared *later* in the XAML are still `null` at that point. A handler
   that pokes another named control crashes with `XamlParseException`
   (surfaces in self-contained Release as a silent `STOWED_EXCEPTION
   0xc000027b`). Either gate cross-references with `if (Other is not null)`
   / `?.`, or do the cross-control sync in a method called *after*
   `InitializeComponent`. See PR #153 and the audit it carries. Upstream
   reference: WinUI's [`loading-loaded-unloaded-events.md`](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/main/src/docs/design-notes/loading-loaded-unloaded-events.md).

   Related forward-looking guidance from that same doc, in case we ever
   add `Loaded`/`Unloaded` handlers (we currently have none):
   - `Loaded` and `Unloaded` are on **different** async queues and can fire
     **out of order** and unpaired when an element churns in/out of the tree.
   - Don't trust `FrameworkElement.IsLoaded` to disambiguate — it's gated on
     whether the Loaded event is still pending in the queue, so it can read
     `false` on an element that's already in the live tree. Check
     `element.Parent != null` instead.

7. **Visual verification before declaring a local task done.** After any
   change touching `src\Glasswork.App\` (XAML, code-behind, ViewModels,
   `App.xaml.cs`, services that affect what's drawn), you **must** run a
   real render and view the resulting PNG before signing off. The test suite
   cannot catch XAML parse errors, layout regressions, blank screens, or the
   silent `STOWED_EXCEPTION` crashes from hard rule 6 — only a real render can.

   Use [`scripts\invoke-visual-verification.ps1`](../scripts/invoke-visual-verification.ps1)
   for behavior-specific changes. It runs a JSON scenario from
   [`scripts\visual-verification\`](../scripts/visual-verification/), seeds an
   isolated temporary Vault + UI state file, launches a unique dev-build app
   instance, optionally drives UI Automation actions, captures named PNGs, and
   rejects blank/uniform screenshots. Add or update a scenario that covers the
   behavior you changed, then inspect the captured PNG(s). Example:

   ```powershell
   pwsh -File scripts\invoke-visual-verification.ps1 -Scenario scripts\visual-verification\backlog-smoke.json
   ```

   Use [`scripts\verify-app.ps1`](../scripts/verify-app.ps1) for a generic
   startup smoke test when no scenario exists yet. Both scripts launch the
   Debug build with a verification-only AppInstance key and skip protocol
   registration/update checks, so they do not touch the user's installed
   Glasswork, real Vault, normal UI state, or `glasswork://` handler. Pure
   `Glasswork.Core` changes (no UI surface affected) may skip this. **Cloud /
   Linux agents cannot run this** — they must flag UI-touching work for local
   re-verification before merge.

   > **Skill:** the step-by-step playbook for this rule — verifying changes,
   > writing scenarios, and discovering/scaffolding `AutomationId` selectors by
   > inspecting the live UI Automation tree (`scripts\inspect-app.ps1`) — lives
   > in [`.github/skills/visual-verification.md`](skills/visual-verification.md).
   > It also feeds rule 6: a real render is what surfaces the silent
   > `STOWED_EXCEPTION` crash that init-time cross-references cause.

## Investigation guidance (for issue triage & root-cause analysis)

When assigned a user-reported issue (label `user-report`):

1. **Read the issue body.** It was filed from the in-app feedback dialog via
   `gh issue create`. The first line marks the category (`**Bug**`,
   `**Feature Request**`, or `**General Feedback**`).
2. **Locate the subsystem.** Map the user's description to a bounded context
   using `CONTEXT.md`. Then find the concrete file(s) — e.g. feedback dialog
   → `src/Glasswork.App/Pages/FeedbackDialog.xaml.cs` +
   `src/Glasswork.App/Services/GhCliIssueFiler.cs`.
3. **Check related ADRs.** If the issue touches a decision already made, note
   whether it challenges the ADR (requires revisiting) or is just a bug in
   the ADR's implementation.
4. **Post findings as a comment.** Include:
   - **Root cause** (for bugs) or **where this would fit** (for features)
   - **Relevant files + line numbers**
   - **Relevant ADRs** (link them)
   - **Suggested label(s)** from the existing set (`bug`, `feature`,
     `backlinks`, `markdown-rendering`, `artifacts`, `prd`)
5. **Do not open a PR unless the issue is clearly a one-line fix** and no
   ADR-level decisions are involved. Most user reports need human review
   before implementation — the goal of triage is to make that review faster.

## Style — what to avoid

- **Don't rename existing terms** without updating `UBIQUITOUS_LANGUAGE.md`
  in the same change.
- **Don't add comments** on obvious code. Comment only on non-obvious choices,
  trade-offs, or policy boundaries.
- **Don't add new dependencies** without strong justification. Current stack:
  Markdig, YamlDotNet, CommunityToolkit.Mvvm, WinUI 3, MSTest.
- **Don't introduce DI frameworks, xUnit/NUnit, or alternative markdown
  renderers** — these are settled choices.

<!-- ralph-loop-instructions -->
## Ralph Loop

This repo uses Ralph Loop. If an agent needs to understand, install, refresh, operate, or troubleshoot Ralph here, load the `ralph-loop` skill.

- Repo worker prompt: `.ralph/RALPH.md`
- Repo config: `.ralph/config.json`
- Check/stop/cleanup workers: `.ralph/launch.sh --status`, `--stop`, or `--cleanup`

To refresh `.ralph/` scripts from the Ralph source checkout, run `install.sh --scripts-only` against this repo from your local Ralph source.

Do not overwrite `.ralph/RALPH.md` or `.ralph/config.json` unless explicitly asked.
