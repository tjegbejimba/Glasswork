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
