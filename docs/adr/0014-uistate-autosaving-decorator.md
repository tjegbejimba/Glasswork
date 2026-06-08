# ADR 0014: UI-state persistence is centralized in an auto-saving decorator with flush-on-exit

**Status**: Accepted
**Context slice**: `IUiStateService`, `JsonFileUiStateService`, `App` ui-state wiring
**Extends**: ADR 0001 (UI state storage), composes with the merge-on-save behavior added in #258

## Context

This records the decision for the hardening follow-up (issue #257) raised
independently by both reviewers of #253 and #256.

Per ADR 0001, `IUiStateService` is a generic key/value store whose `Save()` is
**debounced ~500ms**. The debounce is currently scheduled by **callers** —
`App.ScheduleUiStateSave()` is invoked from individual page click-handlers
(`MyDayPage`, `BacklogPage`, `SettingsPage`). Two weaknesses follow:

1. **Wrong seam.** The dismiss key is *mutated* in `MyDayViewModel`
   (`Set`/`Remove`), but persistence is *triggered* in the page. PR #256 patched
   the only two current entry points, but any future caller of the dismiss
   commands — a keyboard shortcut, command binding, automation, test harness —
   can reintroduce the exact "dismissed task reappears" bug simply by forgetting
   to schedule a save. It is also awkward to unit-test, because the trigger is a
   static, page-layer call.
2. **No flush-on-exit.** The only synchronous `Save()` calls run at vault-init GC
   and vault-switch — neither runs on shutdown. If the user mutates state and
   closes the app inside the ~500ms debounce window, the pending save is
   cancelled and the write is lost (a smaller version of the original bug). This
   is systemic to every `ScheduleUiStateSave` caller.

## Decision

Centralize persistence at the **service boundary** with an **auto-saving
decorator** around `IUiStateService`, and add an explicit **flush-on-exit** path.

- A decorator (e.g. `AutoSavingUiStateService`) implements `IUiStateService` and
  wraps the concrete `JsonFileUiStateService`. Every `Set`/`Remove` mutates the
  inner store **and** schedules a debounced `inner.Save()` via a `Debouncer` the
  decorator owns. `Get` passes through.
- `App.Vault`-style wiring points `App.UiState` at the decorator, so **all
  callers** (current and future, VM or page) persist automatically. No caller can
  forget to save.
- The decorator exposes **`Flush()`** (cancel-and-run the pending save
  synchronously). `App` calls `Flush()` on shutdown (`MainWindow.Closed` / app
  exit), closing the rapid-exit data-loss window for *all* ui-state, not just My
  Day.
- The page-layer `App.ScheduleUiStateSave()` calls become redundant; they are
  removed (or reduced to a no-op/`Flush`-schedule shim) once the decorator is in
  place.
- The decorator composes with **#258's merge-on-save**: it calls the inner
  `Save()`, which still re-reads, merges, and writes ui-state.json to avoid
  cross-process clobber. The two mechanisms are orthogonal — the decorator
  decides *when* to save; merge-on-save decides *how* to write safely.

This lives in **`Glasswork.Core`** (pure .NET, like `JsonFileUiStateService` and
`Debouncer`), so it is unit-testable on the Core/Linux runner.

## Considered alternatives

- **(a) `Changed` event on `IUiStateService` + `App` debounces globally.**
  Achieves the same "can't forget" guarantee, but splits the wiring across the
  service (raises event) and `App` (subscribes + debounces), leaving the debounce
  in the WinUI layer and thus harder to cover in a Core test. Rejected in favor of
  the decorator, which keeps the trigger *and* the debounce in Core.
- **(c) Inject an `IUiStateSaveScheduler` into the ViewModels.** Better than the
  page layer (moves the trigger next to the mutation), but every ViewModel must
  still remember to call the scheduler after `Set`/`Remove` — the same
  forgettable contract, one layer down. Rejected.
- **Do only flush-on-exit, leave the seam.** Fixes weakness 2 but not weakness 1;
  the page-layer trigger remains forgettable. Rejected — the decorator delivers
  both, and its `Flush()` *is* the flush-on-exit path.

## Consequences

- New `AutoSavingUiStateService` decorator in `Glasswork.Core.Services`;
  `App` constructs `JsonFileUiStateService` wrapped in it.
- Core test: a `Set`/`Remove` schedules a save (verified with a fake/synchronous
  `Debouncer` or injectable clock), and `Flush()` forces an immediate `Save()`.
  This is the "mutation gets persisted" regression test that the page-layer design
  could not express cleanly.
- The explicit `App.ScheduleUiStateSave()` calls in pages are deleted; reviewers
  no longer need to police "did this handler schedule a save."
- A shutdown hook (`MainWindow.Closed` / app exit) calls `Flush()`.
- Behavior is unchanged in the happy path (still ~500ms debounce); only the
  guarantees tighten.

## Why this ADR exists

It changes *where* a cross-cutting concern lives (every ui-state write now routes
through the decorator) and is the kind of structural decision a future
contributor would otherwise reverse by re-adding caller-side `ScheduleUiStateSave`
calls. Recording it fixes the seam as the intended one.
