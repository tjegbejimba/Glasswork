# ADR 0017: My Day groups child Tasks under their parent PBI as cross-file container cards

**Status**: Accepted
**Context slice**: `MyDayViewModel`, `MyDayContainerGrouper` (new), `MyDayRemovalPolicy`, `GlassworkTask.TodaysChildren` (new), `MyDayPage` card template, the in-memory `IndexService` task map
**Relates to**: ADR 0016 (PBI type / containers — Phase 1), ADR 0008 (My Day virtual promotion + inline subtasks), ADR 0013 (date-scoped pins), ADR 0005 (backlinks — explicitly *not* the parent model)

## Context

ADR 0016 (Phase 1) gave tasks a `type` and stopped PBIs from self-promoting to My Day
on their import-stamped `due` date, but **explicitly deferred** between-file PBI→Task
container *grouping* — rendering imported child Tasks visually nested under their
imported PBI across separate vault files. This ADR is that Phase 2.

Glasswork's value over ADO is breaking a unit of work into trackable children; that
only reads well in My Day if a child Task that is in My Day is shown **under** its
parent PBI, with the PBI acting as a container — even though the PBI and its children
live in **separate vault files**.

The parent link already exists: the `parent:` frontmatter field. Backlog "group by
parent" and TaskDetail's Children section resolve it via `IndexService.GetChildren`; the
My Day grouper resolves it directly through the in-memory index by parent id (a lookup
over `IndexService.Tasks`). My Day cards already render *in-file* subtasks inline via
`TodaysSubtasks` (ADR 0008). Phase 2 layers cross-file children onto that same card.

## Decision

Add a **presentation-only** grouping step to the My Day view that nests a promoted child
Task under its parent PBI as a container card.

1. **Parent identity = the `parent:` field**, resolved through the in-memory index by
   Glasswork Task ID or, for imported work, by matching a numeric/full-URL ADO parent
   identity to the unique in-app PBI carrying that ADO Link. A child nests only when
   the resolved in-app task has `type == pbi`. Ambiguous ADO identities do not group.
   Wikilinks and backlinks are *not* used — backlinks (ADR 0005) are incoming wiki
   references, a different relationship.
2. **Container-only host.** A PBI with ≥1 in-My-Day child is shown in My Day to host
   those children **even if it would not independently promote**. This is a view-model
   construct: `MyDayPromotionPolicy`, `MyDayQueries.Today`, and `TaskService.GetMyDay`
   are **unchanged**. A container-only PBI is *not* "in My Day" by policy — it is a host.
3. **New transient model field `GlassworkTask.TodaysChildren`**
   (`IReadOnlyList<GlassworkTask>?`), parallel to `TodaysSubtasks`, carrying the
   in-My-Day child Tasks to render beneath the PBI card. `HasTodaysChildren` gates
   visibility. Like `TodaysSubtasks` it is **reset by `Clone()`** (recomputed per
   refresh) and **carried by `MyDayViewModel.CopyTaskState`** (so reconcile updates the
   bound row while preserving `IsManuallyCollapsed`).
4. **Pure grouper `MyDayContainerGrouper`** (Core, Linux-testable): given the promoted
   list, the full task dictionary, and `today`, it returns the ordered top-level rows
   with `TodaysChildren` attached and grouped children removed.
   `MyDayViewModel.Refresh` calls it after computing the promoted set, before reconcile.
5. **Ordering**: standalone (non-container) rows first, in the existing priority-first
   order; then PBI container rows, ordered by earliest child `due` then PBI title.
   Children within a container are ordered by `due` ascending.
6. **One level only.** A child that is itself a container PBI stays at the top level as
   its own container rather than being nested (no PBI-under-PBI nesting). A
   PBI→PBI→Task grandchild chain is a non-goal.
7. **Rendering**: the My Day card template gains a `TodaysChildren` section beside the
   existing `TodaysSubtasks` section. A PBI container card suppresses the leaf
   "complete" affordance (you complete its children — ADR 0016) and reuses
   `IsManuallyCollapsed` to collapse/expand its children. A PBI with neither
   actionable `TodaysSubtasks` nor `TodaysChildren` is omitted from the view even
   when directly pinned; PBIs never render as empty standalone rows.
8. **Removal removes the group.** "Remove from My Day" (the row X) on a PBI container
   acts on the **whole group**: the existing `MyDayRemovalPolicy.PlanRemoval` is applied
   to each nested child (dismiss-for-today, plus clear `my_day` if set) **and** to the
   container PBI itself, so an independently promoted PBI cannot pop back as a standalone
   row. Without this, the grouper would rebuild the container from its still-promoted
   children on the next refresh and the X would be a no-op. `MyDayRemovalPolicy` stays
   pure: a new `RemovalTargets(task)` returns the child+container set for a container (or
   just the task otherwise), and the view-model applies the per-task plan to each.

## Considered alternatives

- **Mixed `Rows` list + `DataTemplateSelector` (the Backlog `BacklogGrouper` pattern):**
  render My Day as header rows + task rows with the PBI as a thin group header. Rejected
  — ADR 0016 specifies a *container card*, and My Day's card aesthetic plus its existing
  `TodaysSubtasks` / `IsManuallyCollapsed` machinery compose more naturally with a
  nested-children section than with a separate header type. (Documented because it is
  genuinely less code and is the established Backlog precedent.)
- **Identify the parent via wikilink or the backlink index.** Rejected — `parent:` is
  the canonical cross-file parent model already in use; backlinks (ADR 0005) are
  vault-wide incoming wiki references, a different relationship.
- **Make PBIs promote into My Day when a child is in My Day (change the policy).**
  Rejected — it would re-pollute the three promotion gates and `get_my_day` and blur
  "in My Day" semantics. Grouping stays a presentation layer.
- **Group under any resolvable in-app parent (Backlog parity).** Rejected — the feature
  is framed around PBIs; grouping ordinary tasks under ordinary tasks in My Day is
  surprising. PBI-only keeps it predictable.
- **Recursively expand grandchildren.** Rejected for Phase 2 — keeps My Day scannable; a
  nested child that is itself a container is a compact row you open to drill in.

## Consequences

- New transient observable `GlassworkTask.TodaysChildren` + `HasTodaysChildren`; reset by
  `Clone()`, carried by `CopyTaskState`. No frontmatter/schema change, no migration.
- New pure `MyDayContainerGrouper` in `Glasswork.Core` (Linux-buildable, fully
  unit-tested in `MyDayContainerGrouperTests`).
- `MyDayViewModel.Refresh` gains one grouping call; the Suggestions exclusion set now
  derives from the grouped rows (nested children and container hosts are excluded from
  Suggestions). Covered by `MyDayViewModelCrossFileContainerTests`.
- `MyDayPage` card template gains a children section and a PBI-container variant
  (suppressed leaf-complete). **Windows-only XAML → requires local visual verification
  per hard rule 7.**
- `UBIQUITOUS_LANGUAGE.md` adds **PBI container** and **Today's children**, and the
  **In My Day today** entry notes that grouping is presentation-only and a container-only
  PBI is a host, not independently "in My Day."
- The three promotion gates and `get_my_day` are deliberately untouched.

## Why this ADR exists

It introduces a view-model construct ("container-only host") that intentionally diverges
from the promotion policy, adds a new model field, and chooses the container-card shape
over the existing Backlog group-header precedent — a real, hard-to-reverse trade-off a
future contributor would question.
