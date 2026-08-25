# ADR 0025: Parent Task is the canonical container contract

**Status**: Accepted
**Supersedes in part**: ADR 0016 (`pbi` as the canonical container type), ADR 0017 (PBI-only naming and one-level compatibility assumptions)
**Context slice**: `GlassworkTask`, vault frontmatter, Task Query, MCP contracts, ADO import and type-backfill workflows

## Context

ADRs 0016 and 0017 introduced `type: pbi` as the container behavior needed to keep imported ADO containers out of actionable due-date and Planner flows. That behavior is correct, but the name combines behavioral role with one external source kind. Parent Tasks must also represent Features, Epics, User Stories, custom ADO types, and native hierarchy.

The contract must expand before hierarchy changes land. Existing vault files and clients still send `pbi`, while new files need a source-neutral canonical type and an independent display value.

## Decision

1. Canonical Task types are `task`, `parent`, and `bug`. `parent` declares Parent Task behavior; it is not inferred from children.
2. Persisted `type: pbi` and public input `pbi` are compatibility aliases that normalize to `parent`. Managed writes and projections always emit `parent`.
3. Persisted null, blank, or unknown types retain the safe legacy default `task`. Unknown values never acquire Parent Task behavior. Existing documented ADO container-name inputs continue to normalize to `parent` during expansion.
4. `source_kind` is optional, trimmed open display text. It preserves source fidelity and never controls behavior. Blank values normalize to null and are omitted.
5. Type and source kind flow through creation, updates, exact reads, Task Query filters and projections, defensive clones, the in-memory Index, MCP snapshots, and import/maintenance workflows.
6. Existing container behavior is unchanged: a Parent Task does not self-promote from its own due date, is not an Actionable leaf, remains excluded from Planner leaf work, and continues to host compatible My Day children.
7. `type: parent` and the `parent:` relationship are distinct. A Parent Task may itself have a Parent relationship.

## Compatibility boundary

Compatibility is asymmetric:

- **Read/input**: accept canonical `parent`, legacy `pbi`, and previously documented ADO container aliases.
- **Write/output**: emit only canonical `parent`.
- **Unknown values**: default to `task` on persisted reads and public mutation normalization; query filters reject them.

This boundary permits rolling migration and rollback without allowing new legacy writes.

## Consequences

- Editing a legacy PBI file through a managed writer canonicalizes `type: pbi` to `type: parent`.
- `source_kind` may contain custom values without application changes.
- ADR 0016's due-date gate and ADR 0017's existing My Day presentation remain active under Parent Task terminology. Later slices may supersede ADR 0017's one-level and empty-container choices.
- Import and maintenance workflows write `parent`; old `pbi` classifications remain accepted at their public compatibility boundary.
