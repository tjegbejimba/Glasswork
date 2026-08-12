# ADR 0016: Tasks carry an explicit `type` (`task` / `pbi` / `bug`); PBIs are containers and don't self-promote to My Day

**Status**: Accepted
**Context slice**: `GlassworkTask`, `FrontmatterParser`, `MyDayPromotionPolicy`, `MyDayViewModel`, `TaskService.GetMyDay`, the ADO import skill
**Relates to**: ADR 0008 (My Day promotion model), ADR 0013 (date-scoped pins)

## Context

The ADO import skill (`glasswork-import-sprint`) stamps `due: <sprint-end>` on
**every** work item it imports — including Product Backlog Items and User
Stories. A PBI is a *container*: it isn't itself a unit of actionable work, it's
a parent for the Tasks that implement it. But Glasswork had no way to tell a
container apart from an actionable leaf, so once a sprint ended its PBIs all
became "overdue" and flooded My Day via the due-date promotion clause (ADR 0008
clause 2). My Day degraded into a wall of epics with no obvious next action.

Glasswork's value over ADO is breaking a unit of work into trackable subtasks.
That model only reads well if the parent is understood as a container and its
children as the actionable items — which requires the type distinction ADO
already makes (Product Backlog Item / User Story / Bug / Task).

## Decision

Add an explicit, user-owned **`type`** frontmatter field on every task, mirroring
ADO's work-item types, normalized to one of:

- **`task`** *(default)* — an actionable leaf. Unchanged behavior.
- **`pbi`** — a **non-actionable container**. Mirrors ADO's container work-item
  types: **Product Backlog Item, User Story, Epic, and Feature** all normalize to
  `pbi`. Does **not** self-promote to My Day on its **own** `due` date. (The enum
  stays `task` / `pbi` / `bug`; `pbi` is the single "container" bucket — Epic/Feature
  are containers for the same My-Day-gating reason as a PBI, so they share it rather
  than getting their own values.)
- **`bug`** — an actionable leaf; behaves exactly like `task` for promotion.

Only `pbi` changes behavior. A `pbi` is excluded from the **own-due** promotion
clause in three places that each check it independently:

1. `MyDayPromotionPolicy.IsTaskInMyDayToday` — the My Day membership gate
   (`MyDayQueries.Today` routes through it).
2. `MyDayViewModel.Refresh`'s `directlyPromoted` check — which decides whether a
   surfaced task renders as a bare row (`TodaysSubtasks = null`) or a container
   card (`TodaysSubtasks` populated).
3. `TaskService.GetMyDay` — the My Day path consumed by the MCP `get_my_day`
   agent tool. Without the gate here, imported PBIs still flood My Day on the
   agent/import surface even though the app UI is correct.

A `pbi` still promotes when:

- it is **directly pinned today** (`my_day == today`, ADR 0013), or
- it has a **flagged subtask** (`IsMyDay`), or
- it has a **subtask due today/overdue** and not done.

When a PBI surfaces via one of its children, it renders as a **container card**
with the actionable children inline beneath it — not as a bare overdue row that
hides its own work.

My Day presentation applies one additional container rule: a PBI is rendered only
when it has actionable in-file subtasks or cross-file child Tasks for today. A
direct pin remains part of membership policy, but it cannot create an empty
standalone PBI row.

### Serialization avoids file churn

The field is parsed with `GlassworkTask.Types.Normalize` (null / empty /
unrecognized → `task`, case-insensitive). On serialize, the default `task` is
**omitted** from the YAML; only `pbi` / `bug` are written. Legacy files (which
have no `type:` key) therefore round-trip byte-for-byte until a user or the
import skill actually marks something a PBI or Bug — no mass rewrite of the
vault, no Obsidian Sync / git churn.

### Import stamps the type

`glasswork-import-sprint` maps the ADO work-item type onto `type:` at import:
Product Backlog Item / User Story → `pbi`, Bug → `bug`, Task → `task`. This stops
new imports from re-polluting My Day at the source.

## Considered alternatives

- **Option B — "My Day only shows tasks, never PBIs," with no type field.**
  Solves the symptom but loses the container/leaf distinction entirely and gives
  the user no vocabulary to model their work the way ADO does. A PBI legitimately
  pinned for today (`my_day == today`) would also be silently suppressed.
  Rejected.
- **Infer container-ness from "has subtasks / has children."** Brittle: a normal
  Task with a checklist would be misread as a container, and a freshly-imported
  PBI with no children yet would be misread as a leaf. The distinction is a
  property of the *work item*, not of whether children happen to exist yet.
  Rejected.
- **Drop the import-stamped `due` instead of adding a type.** Treats only this
  one import path and throws away real sprint-end information; doesn't give
  Glasswork the container concept it needs regardless of where the task came
  from. Rejected.

## Consequences

- New frontmatter key `type` (optional; absent ⇒ `task`). Documented in
  `UBIQUITOUS_LANGUAGE.md` (**Task type**, **PBI**, **Bug**); the "In My Day
  today" entry notes PBIs don't self-promote on their own due.
- `GlassworkTask` gains an observable `Type` (default `"task"`) and a `Types`
  static class (`Task` / `Pbi` / `Bug`) with `Normalize`. `Clone()` and
  `MyDayViewModel.CopyTaskState` both carry `Type`.
- The own-due promotion gate now lives in **three** places
  (`MyDayPromotionPolicy`, `MyDayViewModel`, and `TaskService.GetMyDay` — the
  MCP `get_my_day` path); all three must stay in sync. A PBI
  that reaches My Day only via a stale own-due no longer appears at all; one that
  reaches it via a child renders as a container.
- **Phase 1 scope.** This ADR covers the type field plus the My Day own-due gate.
  It does **not** introduce between-file PBI→Task container *grouping* in My Day
  (rendering imported child Tasks visually nested under their imported PBI across
  separate vault files). That is deferred as Phase 2.
- **Backfilling the pre-existing corpus (issue #338).** PBIs imported before this
  field existed default to `task` and still leak into My Day on their stale
  import-stamped `due`. A one-time, ADO-authoritative, idempotent backfill stamps
  the existing vault: it queries each imported file's `System.WorkItemType` and maps
  any container type (**Product Backlog Item / User Story / Epic / Feature → `pbi`**),
  `Bug → bug`, and leaves `Task` untouched. It lives in `Glasswork.Core`
  (`TaskTypeBackfillService`, surgical frontmatter insert — never a `Serialize`
  round-trip, so legacy `ado_link:` files do not churn), is driven by the
  `tools/Glasswork.Maintenance` console + the `glasswork-backfill-types` skill, and
  registers writes with `SelfWriteCoordinator`.
- Core-only change — no App/XAML surface affected, so hard rule 7 (visual
  verification) does not apply.

## Why this ADR exists

It introduces a new user-owned frontmatter field and changes what reaches My Day
(surprising without context — a contributor would ask why a PBI with an overdue
`due` no longer shows), and it chooses the type-field model over the simpler
"hide all PBIs" rule as a deliberate, hard-to-reverse trade-off that shapes how
work is modeled across the app.
