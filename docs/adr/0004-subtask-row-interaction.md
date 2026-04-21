# ADR 0004: Subtask row interaction model — single-click splits "mark done" from "open detail"

**Status**: Accepted
**Context slice**: Subtask access on TaskDetailPage; revamp of completed subtasks section

## Context

Two pain points on `TaskDetailPage` motivate this decision:

1. **Completed subtasks are inert.** The `Completed (n)` expander renders done subtasks as a checkbox + strikethrough text with no `⋯ More` button. There is currently no way to re-open the detail dialog for a completed subtask. To edit the title, change status, view notes, or promote it, you must un-check it (which mutates state) or never let it complete in the first place.

2. **Detail access for active subtasks is buried.** Today: click `⋯ More` → wait for menu → click "Open detail…" — three clicks to reach the most information-rich surface a subtask has. The dialog itself is the right scope (Title, Status, Blocker reason, Due date, ADO ID, Notes, My Day, Promote, Delete) — only the path to it is wrong.

The current row template stores the subtask text **as the `CheckBox`'s `Content`**, meaning clicking the text toggles done. That coupling is the root cause of both pains: it makes the text unavailable as a separate click target for "open detail," forcing a dedicated `⋯` menu, and it precludes adding double-click without checkbox flicker.

## Decision

Restructure the row so the **circle glyph** and the **subtask text** are siblings, not a CheckBox-with-text-content. Then split single-click semantics by hit zone:

- **Single-click on the circle glyph** → toggle done (checkbox behavior).
- **Single-click on the text** → open the detail dialog (`SubtaskDetailDialog`).
- **No double-click anywhere.** The gesture is unnecessary once the hit zones are split.

The circle stays as a styled `CheckBox` with no `Content` (just the glyph). The text becomes a sibling `TextBlock` that owns `Tapped` (open detail), `IsTabStop="True"`, and `KeyDown` Enter (open detail). Cursor changes to hand on hover over the text to advertise the affordance.

This is applied uniformly to **active and completed** rows. Completed rows additionally get a `⋯ More` button (parity with active) but with a reduced menu (no Set due, no My Day toggle on the row).

## Considered alternatives

### Pattern 1: Preserve text-toggles-done, add double-click for detail

Single-click on text toggles done (as today). Double-click on text opens detail.

**Rejected** because WinUI's `Tapped` always fires before `DoubleTapped`, so the user sees done → undone → detail-opens — every detail open includes a checkbox flicker. A 250ms suppression timer would mask this but adds latency to every "mark done" click and is fragile around focus transitions.

### Pattern 3: Hybrid with delay timer

Single-click on text starts a 250ms timer that toggles done if no second click arrives; double-click cancels the timer and opens detail.

**Rejected.** The fragility is real (focus changes, rapid keyboard interactions, accessibility tools all interact poorly with deferred actions), and the 250ms delay is perceptible on every "mark done" click — the most frequent interaction in the section.

### Keep current row, just expose detail elsewhere

Add a "Detail" button to the row, or hoist the menu item to be more prominent.

**Rejected.** It re-clutters every row with a button or maintains the menu indirection. Pattern 2 keeps the row visually minimal — the text is already there, we're just teaching it a click behavior.

### Symmetric template (active and completed identical)

One row template, rely on visual de-emphasis (strikethrough + opacity) to mark completed.

**Rejected.** The Completed expander exists to push done items out of the way; keeping its rows quieter (no My Day button, smaller menu) reinforces that. Maintenance cost of two templates is small (~30 lines of XAML difference).

## Consequences

**Positive:**

- Detail is a one-click affordance everywhere subtasks render — no hidden gesture, no menu hunt.
- Completed subtasks finally have detail access without un-completing them first.
- No flicker, no timer, no ambiguity.
- The hit-zone split (circle vs text) matches the dominant pattern in modern todo apps (Microsoft To Do, Things, Todoist, Apple Reminders), so users coming from any of those need no retraining.

**Negative:**

- **Muscle-memory retraining.** Existing Glasswork users have learned "click text to mark done." After this change, clicking the text opens detail instead. The hand-cursor on the text is the only affordance hint — if it proves insufficient, follow up with a tooltip "Click to open detail."
- **The circle becomes the sole target for "mark done."** Smaller hit zone than the full row. The existing `CircleCheckBoxStyle` size is the gating factor; if reports of mis-click come in, enlarge the style.
- **Two row templates to maintain** (active and completed). Mitigated by both rooting on the same `Grid` shape — only the action set differs.

**Reversibility.** Moderately hard. Once users adapt to "click text = open detail," reverting would re-train them again. The structural change (text-as-sibling-of-checkbox) is one-way without breaking other features that may come to depend on it (e.g., per-text drag handles, inline rename). Worth this ADR.

## Related

- `UBIQUITOUS_LANGUAGE.md` — `Subtask`, `Active subtask`, `Completed subtask`
- `src/Glasswork.App/Pages/TaskDetailPage.xaml` — row templates (active list, completed list)
- `src/Glasswork.App/Pages/SubtaskDetailDialog.xaml` — the detail surface this ADR makes accessible
