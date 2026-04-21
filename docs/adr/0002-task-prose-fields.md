# ADR 0002: Task prose lives in three named slots — Description, Notes, Artifacts

**Status**: Accepted
**Context slice**: Artifacts feature (PRD `wiki/decisions/glasswork-artifacts-prd.md`); resolves issue #45

## Context

Before this slice, `GlassworkTask` had a single prose field, `Body`, that the parser populated with "everything between the frontmatter and the `## Subtasks` heading." The serializer emitted three canonical body sections — `## Subtasks`, `## Notes`, `## Related` — but the `## Notes` section was always written empty and never parsed back into the model. It was a sentinel.

The task detail page had a `TextBox` labeled `Header="Notes"` bound to `Task.Body`. That label was a lie: there was no `Task.Notes` to bind to. Anything a user typed went into Body and surfaced on cards as the `Blurb`. The PRD identified this as a slice-zero cleanup blocking the rest of the Artifacts work — adding an Artifacts section underneath a mislabeled "Notes" textbox would compound the confusion, not resolve it.

The Artifacts feature itself adds a third agent-writable surface to a task: markdown files in a sibling `<taskId>.artifacts/` folder. Without resolving the prose-field language first, the user-facing model would have **two ambiguous prose fields** (`Notes` UI label / `Body` semantics) and **one unambiguous one** (Artifacts), which is worse than the starting state.

## Decision

Adopt a **three-tier prose model** at the task level. Each tier has a distinct purpose, owner, and editing surface.

| Tier | Purpose | Primary author | App treatment | On-disk location |
|---|---|---|---|---|
| **Description** | "What is this task about." Stable framing prose. Source of `Blurb`. | Human (or agent on initial create) | Editable in primary text area | Prose between frontmatter and `## Subtasks` |
| **Notes** | Free-form scratch — "remember to ask X", quick decision log, in-flight thinking. | Primarily human | Editable in secondary text area | `## Notes` section |
| **Artifacts** | Agent-produced markdown work-products (plans, designs, investigations). | Primarily agents | **Read-only**, rendered with a custom block renderer | Separate `<taskId>.artifacts/*.md` files |

Concrete code changes:

1. **Rename the model property `GlassworkTask.Body` → `GlassworkTask.Description`** (mechanical rename across parser, serializer, XAML bindings, tests, view-model derivations like `Blurb`).
2. **Add a real `GlassworkTask.Notes` property** (string, default empty). Parser extracts content between `## Notes` and the next `##` heading. Serializer emits the `## Notes` heading followed by the property value (preserves the existing always-emit-the-heading convention so app-written and agent-written files look identical).
3. **Fix the XAML**: the existing `NotesBox` textbox is relabeled to `Header="Description"` and stays bound to `Task.Description`. A second `TextBox` (`NotesEditor`) is added beneath it, labeled `Header="Notes"`, bound to `Task.Notes`.
4. **Update `UBIQUITOUS_LANGUAGE.md`**: add `Description`, task-level `Notes`, and `Artifact` rows; update the `Blurb` row to source explicitly from `Description`.
5. **Update `CONTEXT.md`**: document the three-tier model under the Task Model bounded context.

`Blurb` continues to derive from `Description` — never from Notes, never from Artifacts. Notes is volatile scratch and has no place in a card preview; Artifacts are deliverables, not summaries.

## Alternatives considered

### A. Retire `## Notes` entirely
- ✅ Simplest model — one prose slot per task (`Description`), plus Artifacts as the agent surface.
- ❌ Loses the "lightweight scratch" use case. Users today already type into the (mislabeled) Notes box for that purpose.
- ❌ Forces every working note into either Description (where it pollutes the `Blurb`) or an Artifact file (which is heavyweight overkill for a one-line reminder).
- **Rejected** — too aggressive a removal for a real workflow.

### B. Keep the status quo (sentinel `## Notes`, single `Body` field, mislabeled UI)
- ✅ Zero code change.
- ❌ Continues the language collision that #45 exists to fix.
- ❌ Adding Artifacts on top would entrench the confusion into a new feature.
- **Rejected** — does not address the issue.

### C. Promote `## Notes` to `Task.Notes`, but keep `Body` as the property name
- ✅ Less code churn (no rename).
- ❌ Keeps the `Task.Body` symbol semantically meaning "Description" — the exact ambiguity Ubiquitous Language is supposed to prevent.
- ❌ Future contributor reading `task.Body.FirstNonBlankLine()` cannot tell whether `Body` means "the markdown body of the file" or "the description prose only."
- **Rejected** — the rename is the durable fix; deferring it costs more long-term than the one-time mechanical rename.

### D. Editor edits both `Description` and the file body equivalently (no model split)
- ✅ Round-trip is trivial.
- ❌ User has no way to express "this is scratch" vs "this is the framing description" — and `Blurb` would still pull from arbitrary prose.
- **Rejected.**

## Consequences

### Good
- Language model now matches user mental model: **what is this task** (Description) / **what am I chewing on** (Notes) / **what did agents produce** (Artifacts).
- Future ambiguity eliminated: code symbols (`Description`, `Notes`, `Artifact`) match UL terms exactly.
- Artifacts feature can land cleanly without inheriting prior confusion.
- `Blurb` source is now explicit and stable — only Description, ever.
- Each tier has a clear ownership story: Description (human framing), Notes (human scratch), Artifacts (agent deliverables).

### Bad / accepted trade-offs
- Existing tasks where users typed into the (mislabeled) Notes textbox will show that text under "Description" after upgrade. **Mitigation**: this is semantically faithful (prose stays prose) and recoverable in seconds with cut/paste. No migration UX is added. Documented as the chosen migration policy in the slice.
- Modest code churn from the `Body` → `Description` rename (~15-20 files in the solution). Mitigated by LSP-driven mechanical rename + the existing 270-test suite.
- Two prose textboxes on the task detail page is more visually busy than one. Acceptable cost for the language clarity.

### Reversible?
Partially. The model rename is mechanically reversible. The on-disk format change (now writing `## Notes` content instead of always empty) is forward-compatible (old empty `## Notes` files still parse to `Notes = ""`). The **language boundary** (Description / Notes / Artifacts as three distinct concepts) is the durable commitment; reverting it would re-introduce the exact ambiguity #45 fixed.

## Why this ADR exists

The skill rule for ADRs: hard to reverse + surprising without context + real trade-off. This decision qualifies on all three:

- **Hard to reverse**: the language boundary is durable. Once `Description`, `Notes`, and `Artifact` are canonical UL terms used in code, UI, docs, agent skills, and PRDs, undoing the split would cascade.
- **Surprising without context**: a future contributor will absolutely ask "why are there two `Notes` properties (one on `GlassworkTask`, one on `SubTask`)?" and "why did `Body` become `Description`?" This file is the answer.
- **Real trade-off**: option A (retire `## Notes`) is genuinely tempting for the minimalist model, and we explicitly chose against it.
