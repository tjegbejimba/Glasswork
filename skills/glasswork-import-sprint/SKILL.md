---
name: glasswork-import-sprint
description: 'Import current-sprint ADO work items into Glasswork as todo tasks. Use when the user says "Pull my sprint into Glasswork", "Import my current sprint from ADO", "Sync ADO sprint to my todos", or otherwise asks to bring their current Azure DevOps sprint into Glasswork. Does NOT trigger on read-only questions like "what''s in my sprint" — that''s a query, not an import.'
---

# Glasswork — Import Sprint

You are pulling the user's current ADO sprint into their Glasswork todo corpus. Each ADO work item assigned to the user in the current sprint becomes (or updates) a markdown file in `wiki/todo/`.

The vault root is typically `C:\Users\toegbeji\Wiki\`. Task files live in `wiki/todo/{slug}.md` (with `wiki/todo/done/{slug}.md` for completed work).

> Design context: this skill is the output of a grill-me session. The non-obvious choices are documented inline — read them before deviating.

## Configuration (edit here if your ADO setup changes)

```
ORGANIZATION: msazure
PROJECT:      One
ITERATION_PATTERN: iteration paths matching One\FY*\Q*\2Wk\* (date-anchored — no team config needed)
USER:         the authenticated identity (queried via @Me)
```

If you ever switch project, org, or iteration scheme, edit this block. The skill intentionally has no config file.

## Process

### 0. Discover the authoritative reconciliation contract

Before reading or mutating Tasks, call `get_capabilities` and inspect the
available Glasswork MCP tools. The import requires `transact_tasks`; stop and
report that the installed Glasswork MCP is too old when it is absent.
Authoritative ADO cancellation/restoration is available only when both of these
are present:

- implemented capability `authoritative_ado_reconciliation`
- tool `reconcile_ado_task(task_id, ado_work_item_id, authoritative_state, mutation_id, if_revision, ado_work_item_type?, ado_parent_work_item_id?, update_ado_parent?)`

If either is absent, continue the ordinary import, promotion, type, due-date, and
stale-reporting work below, but do **not** cancel or restore any Task. Report every
candidate under the matching pending-action section and say that a supporting
`glasswork-mcp` 0.10.0 or later must be installed (repository checkout:
`scripts\install-mcp.ps1`). Never emulate the missing lifecycle operation by
writing `status: cancelled`, `cancelled_at`, or `cancellation_reason` as raw YAML,
and never substitute `restore_task` plus a generic status update.

### 1. Resolve the current sprint

Walk the project's iteration tree and find the leaf node whose date range contains today. Do **not** rely on a team's `timeframe=current` pointer — different teams keep different parts of the iteration tree current, and the team that owns the user's actual sprint backlog may not match what `work_list_team_iterations` reports.

Use the ADO MCP tool:

```
work_list_iterations(project="One", depth=10)
```

From the results, find iterations whose `path` matches the pattern `One\FY*\Q*\2Wk\*` and whose `attributes.startDate <= today <= attributes.finishDate`. There should be exactly one match.

Record:
- `SPRINT_PATH` — e.g. `One\FY26\Q4\2Wk\2Wk23`
- `SPRINT_LEAF` — the last segment, e.g. `2Wk23`
- `SPRINT_END` — `attributes.finishDate` in `YYYY-MM-DD` form

If zero matches: abort with the message `"Couldn't resolve current sprint. Searched under One\FY*\Q*\2Wk\* for a node containing <today>. Either the iteration tree hasn't been provisioned for the current period, or the pattern in this skill needs updating."` Do not guess or fall back to a stale sprint.

### 2. Query items assigned to the user in that sprint

Run a WIQL-style query (via the MCP tools available) to fetch every leaf-level work item assigned to the user in `SPRINT_PATH`. Logical filter:

```
System.AssignedTo = @Me
AND System.IterationPath = '<SPRINT_PATH>'
AND System.State <> 'Removed'
```

Terminal items remain in the result so reconciliation can complete their Glasswork tasks. Only `Removed` is filtered. Exact `Removed` handling happens only after an authoritative per-item fetch in step 5; absence from this query is never evidence of removal.
Do not filter by work-item type: custom process types must be imported too.

For each work item, retrieve at minimum:
- `System.Id`
- `System.Title`
- `System.State`
- `System.IterationPath`
- `System.Parent` (if present)
- `System.WorkItemType`

### 3. Build the dedup index

Before creating or updating any task, scan the Glasswork corpus to discover what's already imported.

**Scope** — scan only:
- `wiki/todo/*.md` (root-level task files)
- `wiki/todo/done/*.md` (completed task files)

Do **not** recurse into `wiki/todo/<id>.artifacts/` subdirectories. Artifacts often reference ADO ids that aren't the artifact's "primary" work item (cross-references, related-bug mentions, etc.) — counting those as "imported" would suppress legitimate imports.

Cancelled Tasks remain in `wiki/todo/*.md`; they are not moved to a separate
folder. Default `list_tasks`, `query_tasks`, and `search_tasks` calls exclude them,
so those defaults must never be the sole dedup source. The direct file scan above
must retain `status: cancelled`, and `list_tasks(status: "cancelled")` or
`get_task(task_id)` may be used when an explicit MCP read is needed.

**Patterns** — for each file in scope, match the body / frontmatter against these three precise patterns:

| Pattern (regex)                              | What it matches                                     |
|----------------------------------------------|-----------------------------------------------------|
| `(?m)^ADO\s+(\d+)\b`                         | Canonical body line `ADO <id> — <url>` (anchored to start of line) |
| `_workitems/edit/(\d+)\b`                    | The canonical ADO URL anywhere in the file         |
| `(?m)^ado_link:\s*(\d+)\s*$`                 | Legacy schema frontmatter field per `_schema.md`   |

Do **not** match a bare `\bADO\s+\d+\b` (no line anchor) — casual Notes mentions like "Same shape as ADO 37076384" would false-positive and suppress legitimate imports.

Do **not** match the `parent:` frontmatter field or the `Parent ADO:` body line — those carry the *parent's* ADO id, which is a different work item from the one the task represents.

Build a dictionary
`imported: { ado_id -> { task_id, file_path, status, resource_revision? } }`.
This is the authoritative source of "already imported." Before a conditional MCP
mutation, call `get_task(task_id)` by exact ID and use its current
`resource_revision`; do not reuse a revision from an earlier scan.

### 4. Per-item action: classify, then act

For each ADO work item from step 2:

**Resolve the desired Glasswork status** (the "ADO → Glasswork status map"):

| ADO state          | Glasswork status   |
|--------------------|--------------------|
| `New`              | `todo`             |
| `To Do`            | `todo`             |
| `Committed`        | `todo`             |
| `Active`           | `in-progress`      |
| `In Progress`      | `in-progress`      |
| `In Review`        | `in-progress`      |
| `Resolved`         | `done`             |
| `Done`             | `done`             |
| `Closed`           | `done`             |

If you encounter an ADO state not in this table, **skip the item** and surface it in the summary under "Skipped (unmapped state)" with the state name. Do not guess a mapping — surface the gap so the skill can be updated.

**Resolve the Glasswork Task type** (the "ADO → Glasswork type map"), from `System.WorkItemType`:

| ADO work-item type     | Glasswork `type` |
|------------------------|------------------|
| `Task`                 | `task` |
| `Bug`                  | `bug` |
| `Product Backlog Item` | `parent` |
| `User Story`           | `parent` |
| `Epic`                 | `parent` |
| `Feature`              | `parent` |
| any custom type        | `parent` when another imported item names it as `System.Parent`; otherwise `task` for a new import; preserve an existing valid `task` / `parent` / `bug` |

A `parent` is a **Parent Task** and will not self-promote to My Day on its sprint-end `due`. `task` and `bug` are actionable leaves. Preserve the exact ADO type in `source_kind`.
Only the six named standard kinds are authoritative behavioral mappings. For a
new custom kind, the current batch's Parent edges provide the only structural
decision: a referenced item is a `parent`; an unreferenced item defaults to
`task`. A custom `source_kind` never changes an existing Task's valid behavioral
`type`. If a custom item later needs to own children but was previously imported
as a leaf, report the ownership validation instead of silently changing its
behavior.

**Decide the action:**

1. **Not in `imported`** → CREATE (step 4a).
2. **In `imported`** → consider PROMOTION (step 4b), including a narrowly
   authorized restore when the existing Task is cancelled (step 4c).

#### 4a. Create

Determine the file path:
- If desired status is `done`: `wiki/todo/done/{slug}.md`
- Otherwise: `wiki/todo/{slug}.md`

Compute the slug from `System.Title`:
- Lowercase
- Replace non-alphanumeric runs with `-`
- Strip leading/trailing `-`
- Truncate to 54 characters (matches existing corpus convention)

**Slug-collision check:** if the target file path already exists AND it is **not** in the `imported` dictionary (meaning a different file with the same slug, unrelated to this ADO id), abort this single item with the message `"Slug '<slug>' collides with existing <file-path> (different ADO ID). Skipping item ADO <id>. Rename or delete the existing file and re-run."` Continue with other items — do not abort the whole skill run.

Stage one `create_task` operation for this item. Do not write the vault file
directly:

```json
{
  "op": "create_task",
  "task_id": "<slug>",
  "if_absent": true,
  "fields": {
    "title": "<System.Title verbatim>",
    "status": "<mapped status>",
    "type": "<mapped behavioral type>",
    "source_kind": "<System.WorkItemType verbatim>",
    "priority": "medium",
    "due_date": "<SPRINT_END>",
    "parent_task_id": "<System.Parent as decimal text, or null>",
    "ado_link": 12345678,
    "description": "ADO <id> — https://msazure.visualstudio.com/One/_workitems/edit/<id>\nSprint: <SPRINT_LEAF>.",
    "notes": "### <today YYYY-MM-DD>\nImported from ADO sprint pull (sprint <SPRINT_LEAF>)."
  }
}
```

Notes:
- `priority: medium` always — do **not** map from ADO Priority (it's too noisy at Microsoft to trust).
- `type` maps behavior using the authoritative table above. `source_kind`
  preserves `System.WorkItemType` exactly for display and never controls behavior.
- `due:` is the sprint end date — always set, even for `done` imports. Parent Tasks still get it for reference; `type: parent` keeps them from polluting My Day.
- `my_day:` is NOT set. The user owns that field.
- No copy of the ADO description. Click the link if you need context.
- The single `## Notes` entry is provenance — it gives `glasswork-resume` something to anchor on if the user resumes this task later.

#### 4b. Promote (forward-only reconciliation)

The task already exists. Read its current frontmatter `status`. Apply the forward-only rule:

| Current Glasswork status        | ADO mapped status | Action                                |
|---------------------------------|-------------------|---------------------------------------|
| `todo`                          | `todo`            | leave                                 |
| `todo`                          | `in-progress`     | promote → set status to `in-progress` |
| `todo`                          | `done`            | promote → move to `done/` (see below) |
| `in-progress`                   | `todo`            | leave                                 |
| `in-progress`                   | `in-progress`    | leave                                 |
| `in-progress`                   | `done`            | promote → move to `done/` (see below) |
| `done`                          | anything          | leave (never reopen)                  |
| `cancelled`                     | `in-progress`     | restore only through step 4c          |
| `cancelled`                     | anything else     | leave                                 |
| anything non-canonical (e.g. `in_review`) | anything | leave (unknown ordering — don't guess) |

**Promotions to in-progress** add `status: in-progress` to that Task's staged
`set_task_fields` operation. Keep unrelated fields out of the update.

**Due-date reconciliation (every already-imported actionable item).** If a
`todo`, `in-progress`, or `blocked` Task's `due:` does not equal `SPRINT_END`,
update only `due:` to `SPRINT_END`. Apply the same update after a successful
restore. This keeps actionable Tasks aligned with their current sprint and
prevents stale sprint dates from making My Day appear overdue. Never add, remove,
or change `my_day:`. Do not edit a Task while it remains cancelled or done.

**Import metadata reconciliation (every already-imported non-cancelled item,
regardless of status change).** Stage one `set_task_fields` operation with a
fresh `if_revision`. Always set `source_kind` to the exact
`System.WorkItemType`, `ado_link` to `System.Id`, and `parent_task_id` to
`System.Parent` as decimal text (or null when ADO has no Parent). Set `type` only
when the authoritative table maps the kind. For a custom kind, omit `type` so an
existing valid behavioral type is preserved. Include any forward-only status or
due-date changes already selected above in the same field set. A cancelled Task
must be restored before this ordinary mutation.

After every current-sprint item has been classified, submit all staged
`create_task` and `set_task_fields` operations in **one** `transact_tasks` call.
This is the import's coherence boundary: the mutation service resolves every
numeric ADO Parent against the complete staged graph, so a child stores its
Parent's canonical Glasswork Task ID even when the child operation appears
first. Parent Tasks may nest to any valid acyclic depth. If a Parent is absent,
the numeric external reference remains explicit and the UI renders
`Unresolved parent · ADO #<id>`. A later batch that includes or has already
imported that Parent canonicalizes the child reference.

Use one new batch `mutation_id` and reuse it only for an exact retry. On a
Resource Revision conflict, re-read every existing Task represented in the
batch, rebuild the operations, and submit with a new mutation ID. An exact
retry replays; a later unchanged run stages updates and returns `no_op`.

> This per-sprint backfill only reaches items the current pull re-encounters. To stamp the **entire existing corpus** in one pass — including PBIs that aren't in any current sprint — use the `glasswork-backfill-types` skill (an ADO-authoritative, dry-run-then-apply maintenance pass over the whole vault). Both honor the same strict ADR 0016 mapping.

**Promotions to done** require a file move (`wiki/todo/{slug}.md` → `wiki/todo/done/{slug}.md`) plus frontmatter edits (`status: done`, add `completed_at: <today>`). In an interactive run, these moves are CONFIRM-tier per the D8 guardrails below: collect all candidates and ask the user to confirm in one batch. Non-move promotions execute without confirmation.

**Unattended / workflow mode.** An unattended run may perform promote-to-done moves only when its workflow prompt contains the exact durable authorization `AUTHORIZED_AUTONOMOUS_RECONCILIATION`. That token records the user's standing consent for status-to-done transitions, corresponding `todo/` → `done/` moves, and the bounded reconciliation described in step 4c. Without the token, list candidates under "Pending user action — promote to done" and do not move them. Non-move promotions, due-date reconciliation, and import metadata reconciliation still execute normally.

#### 4c. Authoritative ADO cancellation and restoration

This is a dedicated lifecycle reconciliation, not generic status synchronization.
It applies to current-sprint items and to the authoritative stale-item fetch in
step 5.

- Exact ADO state `Removed` cancels only a matching imported Task whose current
  status is `todo`, `in-progress`, or `blocked`. The dedicated lifecycle sets
  `status: cancelled`, stamps `cancelled_at`, sets reason
  `ADO work item removed`, clears `my_day`, and preserves Task content,
  dates, Links, Artifacts, and relationships.
- A `done` Task always wins. Never reclassify it as cancelled.
- A cancelled Task restores directly to `in-progress` only for exact ADO states
  `Active`, `In Progress`, or `In Review`. The same operation clears
  cancellation metadata atomically. `New`, `To Do`, `Committed`, terminal
  states, unknown states, and case/whitespace variants do not restore.
- Ordinary staleness, iteration movement, reassignment, or absence from the
  current-sprint query is not `Removed` and must never trigger Cancellation.

Both transitions require all of:

1. the capability/tool gate from step 0;
2. the exact durable workflow token `AUTHORIZED_AUTONOMOUS_RECONCILIATION`;
3. a fresh exact-ID `get_task` read and its Resource Revision;
4. a fresh authoritative ADO response carrying the exact state;
5. the matching ADO ID from the dedup index.

Call only:

```text
reconcile_ado_task(
  task_id=<Glasswork task id>,
  ado_work_item_id=<System.Id>,
  authoritative_state=<exact System.State>,
  mutation_id=<new client id; reuse only for an exact retry>,
  if_revision=<fresh Resource Revision>,
  ado_work_item_type=<exact System.WorkItemType>,
  ado_parent_work_item_id=<System.Parent integer or null>,
  update_ado_parent=true)
```

Generate one `mutation_id` per candidate and reuse it only when retrying that
identical request. On a Resource Revision conflict, re-read the Task, re-evaluate
the state machine, and use a new mutation ID if a new request is still valid.
Never call `delete_task`. Never use ordinary `restore_task` for resumed-active
automation: it intentionally restores to `todo`, and chaining a generic update
would expose an invalid intermediate state.

Without the durable token or the capability/tool contract, record the candidate
under the corresponding pending-action section and do not mutate it.

### 5. Detect stale tasks

A **stale task** is a previously-imported task whose ADO work item is no longer in the current sprint.

Build the set `imported_ids_in_glasswork` (the keys of the `imported` dictionary from step 3, filtered to ids that are *not* in `done/` — completed tasks are not stale, they're done). Build the set `current_sprint_ids` (the ids returned from step 2). The **stale set** is `imported_ids_in_glasswork - current_sprint_ids`.

For each stale id, fetch `System.Title`, `System.IterationPath`, `System.State`,
`System.WorkItemType`, and `System.Parent` from ADO by exact ID. Treat this
response as authoritative only for that ID.

- If the exact state is `Removed`, apply the step 4c cancellation rules. Do not
  infer `Removed` from a missing query result, reassignment, or iteration change.
- If the Glasswork Task is cancelled and the exact state is `Active`,
  `In Progress`, or `In Review`, apply the step 4c direct restoration rules even
  when the item remains outside the current sprint.
- Else if the state maps to `done`, apply the same forward-only
  promotion-to-done behavior and authorization rules as step 4b. This catches
  terminal items omitted by a query or moved out of the current sprint before
  completion.
- Otherwise, if its iteration resolves to a dated sprint leaf in the iteration
  tree, update only `due:` to that iteration's finish date when different.
- Otherwise, leave it unchanged and report it as stale. Never guess a due date
  for an undated backlog iteration.

Cancelled and restored candidates still appear in the Stale section when they
are outside the sprint; the lifecycle counts below additionally record the
transition that occurred.

### 6. Print summary

Output to chat (no wiki log entry — per-task provenance lives in each created task's `## Notes`):

```
Sprint <SPRINT_LEAF> ending <SPRINT_END>:

Imported (N):
  - ADO <id>: <title> → wiki/todo/<slug>.md [status: <mapped>]
  ...

Promoted (M):
  - ADO <id>: <title> (todo → in-progress) — wiki/todo/<slug>.md
  - ADO <id>: <title> (in-progress → done, MOVED) — wiki/todo/done/<slug>.md
  ...

Due dates updated (D):
  - ADO <id>: <title> (<old-date> → <new-date>) — wiki/todo/<slug>.md
  ...

Cancelled (C):
  - ADO <id>: <title> (<prior-status> → cancelled) — reason: ADO work item removed; source: Azure DevOps state Removed — wiki/todo/<slug>.md [task_id: <task-id>]
  ...

Restored (R):
  - ADO <id>: <title> (cancelled → in-progress) — reason: resumed active; source: Azure DevOps state <Active|In Progress|In Review> — wiki/todo/<slug>.md [task_id: <task-id>]
  ...

Stale (K) — previously imported, not in current sprint:
  - ADO <id>: <title> → now in <new-iteration-path> [state: <ado-state>] — wiki/todo/<slug>.md
  ...

Skipped (J) — reason listed per item:
  - ADO <id>: <title> [slug collision] would collide with <existing-file>
  - ADO <id>: <title> [unmapped state] ADO state `<state>` has no mapping; update the skill's state map
  ...

Pending user action — promote to done (P, unattended mode only):
  - ADO <id>: <title> — move wiki/todo/<slug>.md → wiki/todo/done/<slug>.md
  ...

Pending user action — cancel (X):
  - ADO <id>: <title> (<prior-status> → cancelled) — source: Azure DevOps state Removed — wiki/todo/<slug>.md [task_id: <task-id>] [reason: missing authorization or supporting glasswork-mcp 0.10.0+]
  ...

Pending user action — restore (Y):
  - ADO <id>: <title> (cancelled → in-progress) — source: Azure DevOps state <state> — wiki/todo/<slug>.md [task_id: <task-id>] [reason: missing authorization or supporting glasswork-mcp 0.10.0+]
  ...
```

Always print `Imported`, `Promoted`, `Due dates updated`, `Cancelled`,
`Restored`, `Stale`, and `Skipped`, using `(0): none` for empty sections. Omit a
pending-action section only when it has no candidates. `Cancelled` and `Restored`
must include ADO ID, prior/new status, reason/source, path, and Task ID.

## Sprint resolution example

Today is `2026-05-15`. `work_list_iterations(project="One", depth=10)` returns hundreds of nodes. Filter to those matching `One\FY*\Q*\2Wk\*` and inspect dates. One node will satisfy `startDate <= 2026-05-15 <= finishDate` — e.g.:

```json
{
  "path": "One\\FY26\\Q4\\2Wk\\2Wk23 (May 03 - May 16)",
  "attributes": { "startDate": "2026-05-03T00:00:00Z", "finishDate": "2026-05-16T00:00:00Z" }
}
```

That gives `SPRINT_PATH = One\FY26\Q4\2Wk\2Wk23 (May 03 - May 16)`, `SPRINT_LEAF = 2Wk23` (strip the date-range suffix), `SPRINT_END = 2026-05-16`.

Note: the iteration `name` field often includes the date range in parens — strip it for `SPRINT_LEAF` so the body line stays clean (`Sprint: 2Wk23.` not `Sprint: 2Wk23 (May 03 - May 16).`).

## D8 Guardrails (apply to every action you take)

These are **baked into this skill** and override any user request that conflicts. They mirror the guardrails in `glasswork-start-work` and `glasswork-resume`.

### HARD NO — never, under any circumstances (no override)
- Do not send Teams messages, emails, or calendar invites.
- Do not approve or merge pull requests.
- Do not delete files in the wiki vault.
- Do not call `delete_task` or otherwise Hard-delete a Task during reconciliation.
- Do not close ADO work items.
- Do not mutate ADO state, assignment, iteration, or content.
- Do not infer ADO state `Removed` from absence, staleness, reassignment, or iteration movement.
- Do not write Cancellation metadata as raw YAML or emulate direct restoration through `restore_task` plus a generic update.
- Do not modify these task frontmatter fields once a task exists: `id`, `created`, `ado` (if present).

### CONFIRM — allowed only with explicit user confirmation in this session
- Move any wiki page (including the promote-to-done file move described in step 4b).
- Transition a task's `status` to `done` (this skill does that for Resolved imports — confirm the batch in step 4b).
- Run any command that mutates external state (git push, ADO comments, etc.).
- Write source code.

The first two items have one narrow unattended exception: a workflow prompt
containing the exact token `AUTHORIZED_AUTONOMOUS_RECONCILIATION`.
Cancellation/Restore is not ordinary CONFIRM-tier: the same exact token is
mandatory in every automated run. The token has exactly two state-changing
scopes: step 4b promote-to-done moves, and the step 4c state machine through
`reconcile_ado_task`. Without it, report those pending transitions. The token
does not authorize unrelated page moves, generic status changes, Hard deletion,
or any ADO mutation.

### ALLOWED — proceed without asking
- Read from any source: ADO, code, wiki vault.
- Create new task files under `wiki/todo/` (initial imports of non-Resolved items).
- Edit a single frontmatter field on an existing task (promote `status: todo` → `status: in-progress`).
- Reconcile an existing actionable Task's `due:` with the authoritative finish date of its ADO sprint.
- Backfill the mapped Task type on a non-cancelled import.
- Print the summary to chat.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it.

## Scope notes — what this skill does NOT do

- **Does not** sync title changes. If ADO title changes after the initial import, the Glasswork file keeps its original title. Slugs are slugified once at create time and never renamed.
- **Does not** sync description changes. The ADO description is never copied in — the link is the source of truth.
- **Does not** reconcile backward. ADO state going from Active → New doesn't demote the Glasswork status.
- **Does not** reopen done tasks. ADO bouncing a terminal item back to Active won't touch a Glasswork `done` Task.
- **Does not** import work items where you are not the assignee, even if they're in your sprint.
- **Does not** import work items in `Removed` state. Exact `Removed` is relevant only when reconciling an existing matching import.
- **Does not** Hard-delete stale Tasks. Exact Removed may cancel one through the guarded lifecycle; other stale Tasks may complete or refresh due dates, and all are reported.
- **Does not** treat all cancelled Tasks as restorable. Only exact authoritative `Active`, `In Progress`, or `In Review` restores directly to `in-progress`.
- **Does not** fetch and create an absent Parent solely because a child names it.
  Parents already selected for import participate in the same coherent batch;
  otherwise the explicit ADO Parent identity remains unresolved until a later
  import brings that Parent into the vault.
- **Does not** set `my_day:` on imported tasks. The user owns that field.
