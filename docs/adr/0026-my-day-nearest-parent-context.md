# ADR 0026: My Day uses nearest-Parent context

**Status**: Accepted
**Supersedes in part**: ADR 0017 (PBI naming, empty pinned-container suppression, container ordering)
**Context slice**: `MyDayContainerGrouper`, `MyDayViewModel`, `MyDayPage`

## Context

My Day already grouped cross-file leaves under a one-level PBI container card.
The Parent Task model introduced by ADR 0025 permits arbitrary acyclic depth and
separates container behavior from exact source kind. Rendering every ancestor as
a card would hide the actionable leaves behind container walls, while omitting a
directly pinned childless Parent would prevent deliberate coordination work.

Parent target dates and priority are useful context but are not leaf scheduling
inputs. Presentation must not turn those signals into membership or ranking.

## Decision

1. Task Query remains the authority for **In My Day today**. Grouping is a
   presentation-only transformation over its ordered actionable leaves.
2. Each actionable Task/Bug leaf groups under its nearest resolved Parent Task.
   The Parent is the only context row; higher local ancestors appear root-first
   in one compact breadcrumb.
3. The Parent header shows its exact `source_kind`, falling back to `Parent Task`.
   Parent due and priority use explicitly contextual labels.
4. Existing standalone leaves remain first in their current relative order.
   Parent groups follow in first-promoted-child order, and children retain Task
   Query order. Parent due and priority never rerank leaves or groups.
5. An explicitly pinned Parent with no My Day leaves renders as a compact
   **Parent coordination row** with Parent orchestration commands and Child
   activity summary freshness. It has no leaf completion affordance.
6. Removing a Parent group applies normal My Day removal to every visible leaf
   and the Parent. Removing a coordination row applies it to the Parent. This
   clears direct pins where present and records today's dismissal before the
   presentation is rebuilt.
7. Parent priority alone does not create a Suggestion. A past explicit Parent pin
   remains eligible for carryover so the user can deliberately restore the
   coordination row.
8. Terminal Parents are not resurrected as context hosts. Blocked Parent state is
   contextual and does not change an actionable child's membership.

## Consequences

- Standalone Task/Bug rows keep their existing appearance and lifecycle actions.
- Parent rows use a dedicated compact presentation branch rather than the leaf
  card details.
- `MyDayContainerGrouper` annotates transient Parent context while leaving Vault
  state, Task Query, Planner, and MCP My Day semantics unchanged.
- Child activity summary states are read in one batch per refresh instead of
  reparsing the task directory once per Parent row.
