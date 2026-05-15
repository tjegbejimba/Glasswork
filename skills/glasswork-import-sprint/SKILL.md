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
AND System.State NOT IN ('Closed', 'Removed')
AND System.WorkItemType IN ('Task', 'Bug', 'Product Backlog Item', 'User Story')
```

Note: `Resolved` is **not** filtered — Resolved items get imported as `done`. `Closed` and `Removed` are filtered. `Done` is sometimes used as a state too — filter it as well.

For each work item, retrieve at minimum:
- `System.Id`
- `System.Title`
- `System.State`
- `System.IterationPath`
- `System.Parent` (if present)
- `System.WorkItemType`

### 3. Build the dedup index

Before creating or updating any task, scan the entire Glasswork corpus to discover what's already imported. For each markdown file under `wiki/todo/**/*.md` (**including `wiki/todo/done/`**), search the body for either pattern:

```
\bADO\s+<id>\b
_workitems/edit/<id>\b
```

Build a dictionary `imported: { ado_id -> file_path }`. This is the only authoritative source of "already imported." Do not check the `parent:` frontmatter field — that holds the ADO parent, not the work item itself, and a `parent:`-based check would wrongly suppress imports.

### 4. Per-item action: classify, then act

For each ADO work item from step 2:

**Resolve the desired Glasswork status** (the "ADO → Glasswork status map"):

| ADO state          | Glasswork status   |
|--------------------|--------------------|
| `New`              | `todo`             |
| `Committed`        | `todo`             |
| `Active`           | `in-progress`      |
| `Resolved`         | `done`             |

**Decide the action:**

1. **Not in `imported`** → CREATE (step 4a).
2. **In `imported`** → consider PROMOTION (step 4b).

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

Write the file with this exact template:

```markdown
---
id: <slug>
title: <System.Title verbatim>
status: <mapped status>
priority: medium
created: <today YYYY-MM-DD>
due: <SPRINT_END>
<if status == 'done'>completed_at: <today YYYY-MM-DD>
</if><if System.Parent>parent: <System.Parent>
</if>---

ADO <id> — https://msazure.visualstudio.com/One/_workitems/edit/<id>
Sprint: <SPRINT_LEAF>.<if System.Parent> Parent ADO: <System.Parent>.</if>

## Subtasks

## Notes

### <today YYYY-MM-DD>
Imported from ADO sprint pull (sprint <SPRINT_LEAF>).

## Related
```

Notes:
- `priority: medium` always — do **not** map from ADO Priority (it's too noisy at Microsoft to trust).
- `due:` is the sprint end date — always set, even for `done` imports.
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
| anything non-canonical (e.g. `in_review`) | anything | leave (unknown ordering — don't guess) |

**Promotions to in-progress** are an in-place frontmatter edit only (single field change — `status: in-progress`). Do not rewrite the file. Do not touch other fields.

**Promotions to done** require a file move (`wiki/todo/{slug}.md` → `wiki/todo/done/{slug}.md`) plus frontmatter edits (`status: done`, add `completed_at: <today>`). File moves are CONFIRM-tier per the D8 guardrails below — collect all promote-to-done candidates, list them in chat, and ask the user to confirm in one batch before performing the moves. Non-move promotions (todo → in-progress) execute without confirmation.

**Unattended / workflow mode.** If the user explicitly says you are running unattended (e.g. the workflow prompt includes "run unattended" or "workflow mode"), do **not** perform promote-to-done file moves. Instead, list them in the summary under a "Pending user action — promote to done" section with the source path, target path, and ADO id. The user will perform the moves manually next time they open the project. Non-move promotions (todo → in-progress) still execute as normal in unattended mode — they're single-field edits, not destructive.

### 5. Detect stale tasks

A **stale task** is a previously-imported task whose ADO work item is no longer in the current sprint.

Build the set `imported_ids_in_glasswork` (the keys of the `imported` dictionary from step 3, filtered to ids that are *not* in `done/` — completed tasks are not stale, they're done). Build the set `current_sprint_ids` (the ids returned from step 2). The **stale set** is `imported_ids_in_glasswork - current_sprint_ids`.

For each stale id, fetch the work item from ADO once more (just `System.IterationPath` and `System.State`) so the output can tell the user where it went. Surface these in the final summary as a list — do **not** move, edit, or delete the stale task files. Cleanup is a user decision.

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

Stale (K) — previously imported, not in current sprint:
  - ADO <id>: <title> → now in <new-iteration-path> [state: <ado-state>] — wiki/todo/<slug>.md
  ...

Skipped (slug collisions, J):
  - ADO <id>: <title> would collide with <existing-file>
  ...

Pending user action — promote to done (P, unattended mode only):
  - ADO <id>: <title> — move wiki/todo/<slug>.md → wiki/todo/done/<slug>.md
  ...
```

If any list is empty, write `Imported (0): none` rather than omitting the section — the user wants to see all counts every run. Omit the "Pending user action" section entirely when running interactively (it's empty by construction).

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
- Do not close ADO work items.
- Do not modify these task frontmatter fields once a task exists: `id`, `created`, `ado` (if present).

### CONFIRM — allowed only with explicit user confirmation in this session
- Move any wiki page (including the promote-to-done file move described in step 4b).
- Transition a task's `status` to `done` (this skill does that for Resolved imports — confirm the batch in step 4b).
- Run any command that mutates external state (git push, ADO comments, etc.).
- Write source code.

### ALLOWED — proceed without asking
- Read from any source: ADO, code, wiki vault.
- Create new task files under `wiki/todo/` (initial imports of non-Resolved items).
- Edit a single frontmatter field on an existing task (promote `status: todo` → `status: in-progress`).
- Print the summary to chat.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it.

## Scope notes — what this skill does NOT do

- **Does not** sync title changes. If ADO title changes after the initial import, the Glasswork file keeps its original title. Slugs are slugified once at create time and never renamed.
- **Does not** sync description changes. The ADO description is never copied in — the link is the source of truth.
- **Does not** reconcile backward. ADO state going from Active → New doesn't demote the Glasswork status.
- **Does not** reopen done tasks. ADO bouncing a Resolved item back to Active won't touch a Glasswork `done` task — the user manually moves it back if needed.
- **Does not** import work items where you are not the assignee, even if they're in your sprint.
- **Does not** import work items in `Closed` or `Removed` state.
- **Does not** delete or move stale tasks. They're flagged in the summary; user decides.
- **Does not** create the parent ADO work item as its own Glasswork task. The `parent:` frontmatter is just an integer — no link is followed.
- **Does not** set `my_day:` on imported tasks. The user owns that field.
