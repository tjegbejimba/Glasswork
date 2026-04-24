# ADR 0008: My Day virtually promotes parents of flagged or due-soon subtasks

**Status**: Accepted
**Context slice**: `MyDayPage`, `MyDayViewModel`, subtask data model

## Context

Today, a task appears on My Day if `task.MyDay` is today, or — virtually — if it
is due-today/overdue. Subtasks have their own `IsMyDay` flag (`my_day`
frontmatter) and their own `Due` date, but those signals only surface in a
separate **"Flagged subtasks"** section that lists parents whose own `MyDay` is
unset and groups their flagged subtasks under them.

Two pain points:

1. The separate section makes the user scan two lists for "what am I doing
   today." Parents with both task-level pinning and flagged subtasks would
   appear in the top list with no indication that subtasks were flagged.
2. Subtasks with earlier due dates than their parent are silently invisible
   on My Day — a parent due next week can hide a subtask due today.

## Decision

Make subtask-level signals participate in the same virtual-promotion rule
already used for task-level due dates, and render qualifying subtasks
**inline beneath their parent** instead of in a separate section.

**Promotion rule** — a task is in My Day today if any of:

1. `task.IsMyDay` *(direct pin)*
2. `task.Due <= today && Status != Done` *(direct virtual — existing)*
3. any subtask has `IsMyDay == true` *(new virtual — flagged subtask)*
4. any subtask has `Due <= today && Status != Done` *(new virtual — due subtask)*

Dismiss-for-today (`IUiStateService`, key `dismissed.{yyyy-MM-dd}.{taskId}`)
overrides all four.

**Render rule** — beneath each parent on My Day, render only the
*today's subtasks*: `(IsMyDay || Due <= today) && Status != Done`.
A directly-pinned parent with no qualifying subtasks renders only its
segmented progress bar (no subtask list).

**Removal** — "Remove from My Day" on a parent always sets the
dismiss-for-today flag. It does **not** clear `task.MyDay` for direct pins
and does **not** cascade-unflag subtasks. The user clears flags individually
from `SubtaskDetailDialog` if desired. The parent returns tomorrow if
qualifying signals persist.

**Completion** — completing a subtask does not clear its `my_day`
metadata, mirroring the task-level rule (`TaskService.cs:52` only sets
`CompletedAt`). The render rule's `Status != Done` filter handles
visibility. When the last today's-subtask of a virtually-promoted parent
is completed, the parent exits My Day for the day.

The separate "Flagged subtasks" section in `MyDayPage.xaml` and its
backing `SubtaskGroups` / `SubtaskAnchor` types are removed.

## Considered alternatives

- **Cascade-write promotion**: flagging a subtask also writes
  `my_day: <today>` to the parent. Rejected — creates two writes per
  user action and makes `task.MyDay` ambiguous (was it user-set or
  auto-set?), which breaks every removal interaction.
- **Render full subtask list under each promoted parent**: rejected — the
  segmented progress bar already conveys task shape; duplicating titles
  for non-flagged subs adds noise. The user flagged subtasks specifically
  to focus on *those*.
- **Cascade-unflag on "Remove from My Day"**: rejected — silently revokes
  user intent on subtasks. Symmetric with the existing rule that
  due-today tasks can't be "removed," only dismissed.

## Consequences

- `MyDayViewModel.IsOnMyDayToday` gains two new disjuncts (clauses 3 and 4
  above).
- `MyDayPage` parent card needs a new inline subtask list bound to a
  computed `TodaysSubtasks` collection per task.
- `SubtaskGroups` / `SubtaskAnchor` / the "Flagged subtasks" XAML section
  are deleted; tests that assert on them need updating.
- No vault schema change. No migration. Existing `my_day` metadata on
  subtasks is honored as-is.
- The "Flagged subtask" term is added to `UBIQUITOUS_LANGUAGE.md` as a
  user-facing label; the code surface remains `IsMyDay` / `my_day` for
  task/subtask symmetry.
