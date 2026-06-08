# ADR 0013: My Day direct pins are date-scoped (`my_day == today`), not pin-forever

**Status**: Accepted
**Context slice**: `MyDayPromotionPolicy`, `my_day` frontmatter semantics, My Day promotion model
**Supersedes**: clause 1 ("direct pin") of ADR 0008's promotion rule

## Context

This records the decision for root cause **#3** of the "tasks reappear in My Day
after I removed them" investigation (issue #254).

`my_day` is stored in frontmatter as a **date**, but
`MyDayPromotionPolicy.IsTaskInMyDayToday` only consults `task.MyDay.HasValue`:

```csharp
// Direct pin: MyDay frontmatter is set (any non-null value).
if (task.MyDay.HasValue) return true;
```

So a directly-pinned task promotes into My Day **every day, forever**, regardless
of how old the pin is. On the real vault ~16 tasks carry `my_day` dates that are
weeks or months old (e.g. `2026-04-17`) yet still surface today. Dismissing such
a task only suppresses it for the current day via the in-memory
`dismissed.{yyyy-MM-dd}.{taskId}` key; the next day the `HasValue` pin
re-promotes it — "it came back."

The feature is named **My Day** (cf. Microsoft To Do) and the app already has a
**Suggestions** section. Both point at the *daily-list* mental model: My Day is
for **today**; yesterday's items don't auto-return — they resurface as
suggestions the user can re-add.

## Decision

A **direct pin promotes only when `my_day == today`.** The promotion rule's
clause 1 changes from `task.MyDay.HasValue` to
`task.MyDay == today` (compared as `DateOnly`). A `my_day` date in the **future**
does not promote until that day arrives (a natural "schedule for a day" capability);
a `my_day` date in the **past** does not promote at all.

The other promotion clauses from ADR 0008 are unchanged:

1. `task.MyDay == today` *(direct pin — **was** `HasValue`)*
2. `task.Due <= today && Status != Done` *(direct virtual)*
3. any subtask `IsMyDay == true` *(virtual — flagged subtask)*
4. any subtask `Due <= today && Status != Done` *(virtual — due subtask)*

Dismiss-for-today still overrides all four and remains necessary for the
**virtual** promotions (due-date / subtask), where there is no pin date to clear.
`RemoveFromMyDay` continues to durably clear `my_day` for a direct pin.

### One-time migration (no eviction on ship)

Switching to date-scoped semantics would, on first run, drop all ~16 existing
past-dated pins out of today. To avoid a jarring mass-eviction, a **one-time,
idempotency-guarded migration** runs at startup:

- For every task whose `my_day` is a date **strictly before today**, rewrite
  `my_day` to **today's date**. Tasks with `my_day == today` or a future date are
  left untouched.
- The migration is **guarded by a version flag** in ui-state
  (e.g. `migration.myDayDateScoped` / `IUiStateService`) and runs **exactly
  once**. It must **not** re-run on subsequent launches — otherwise yesterday's
  pins would be rewritten to each new "today" and we would silently recreate
  pin-forever.
- The migration writes to the vault and therefore **must register with
  `SelfWriteCoordinator`** (cross-cutting rule 5) so `FileWatcherService` does not
  fire spurious external-change events.

Net effect: every currently-pinned task appears today exactly as before, then
expires naturally tomorrow under the new rule.

## Considered alternatives

- **Option 1 — pin-forever (status quo).** Predictable "pin" mental model, but
  stale pins accumulate and "My Day" degrades into "My Everything"; it is the
  direct cause of the next-day recurrence. Rejected.
- **Option 3 — normalize-on-promote.** Keep `HasValue` but rewrite `my_day` to
  today whenever the task is shown. No eviction, but it mutates the vault on
  *read*, churns Obsidian Sync/git, and destroys "when did I actually pin this."
  Rejected.
- **Option 4 — separate `my_day_pinned` boolean distinct from the date.**
  Cleanest conceptual split, but a schema change plus migration for a property
  that the date already encodes. Rejected as more work than the problem warrants;
  the date field already carries enough information.

## Consequences

- `MyDayPromotionPolicy.IsTaskInMyDayToday` clause 1 becomes a date equality
  check; existing tests that assert "any non-null `my_day` promotes" must be
  updated to the date-scoped expectation.
- A new one-time, flag-guarded startup migration is added (Core-testable: given a
  set of tasks + a fake clock + fake ui-state, past-dated pins are rewritten to
  today, today/future pins are untouched, and a second run is a no-op).
- Future-dated `my_day` becomes a *scheduled* pin (promotes on its day). This is a
  new, intentional capability; document it in `UBIQUITOUS_LANGUAGE.md` alongside
  the refined definition of "pin."
- "Remove from My Day" on a direct pin is now fully durable (clears the date) and
  cannot recur next day. Virtual promotions (due/subtask) still rely on
  dismiss-for-today, unchanged.
- No frontmatter *schema* change — `my_day` remains a user-owned date; only its
  interpretation narrows.

## Why this ADR exists

It is hard to reverse (the migration rewrites user vault data once and changes
what "pinned" means), surprising without context (a contributor would ask why a
set `my_day` no longer always shows), and a real trade-off (Option 1 is the
intuitive "pin" model we are explicitly choosing against in favor of the daily
My Day model the app's name and Suggestions section already imply).
