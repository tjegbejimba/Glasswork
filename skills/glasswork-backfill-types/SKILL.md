---
name: glasswork-backfill-types
description: 'One-time, vault-wide backfill that stamps the `type:` frontmatter field (ADR 0016) onto ADO-imported tasks that predate it — so existing PBIs stop leaking into My Day. Use when the user says "backfill task types", "stamp type on existing PBIs", "fix PBIs showing in My Day", "retro-stamp type: pbi", or "run the type backfill". This is a maintenance/repair action, not a sprint import — for pulling the current sprint use glasswork-import-sprint instead.'
---

# Glasswork — Backfill Task Types

You are retro-stamping the `type:` frontmatter field (`pbi` / `bug`) onto ADO-imported
task files that were created **before** the field existed (PR #335 / ADR 0016). Without it
those items default to `task` and a `pbi` leaks into My Day on its stale sprint-end `due`.
This skill fixes the existing corpus once; new imports are already stamped by
`glasswork-import-sprint`.

> Design context: identification is **ADO-authoritative**. A pure vault-local guess
> (e.g. "is anyone's parent") only catches about half the in-vault containers, so this
> skill asks ADO for each file's `System.WorkItemType`. The safe, idempotent, lossless
> stamping lives in a tested Core service (`Glasswork.Core` → `TaskTypeBackfillService`),
> driven through the `tools/Glasswork.Maintenance` console. See ADR
> `docs/adr/0016-task-type-pbi-distinction.md`.

## Configuration

```
VAULT_ROOT:   C:\Users\toegbeji\Wiki        (the Obsidian vault root; task files are under wiki/todo)
ORGANIZATION: msazure
PROJECT:      One
TOOL:         tools/Glasswork.Maintenance/Glasswork.Maintenance.csproj   (run from the Glasswork repo root)
```

## ADO → Glasswork type map (ADR 0016)

| ADO `System.WorkItemType`         | Action                 |
|-----------------------------------|------------------------|
| `Product Backlog Item`            | classify `type: pbi`   |
| `User Story`                      | classify `type: pbi`   |
| `Epic`                            | classify `type: pbi`   |
| `Feature`                         | classify `type: pbi`   |
| `Bug`                             | classify `type: bug`   |
| `Task`                            | skip (default, omitted)|
| anything else                     | skip + surface for review |

`pbi` is the single **non-actionable container** bucket — Product Backlog Item, User Story,
Epic, and Feature all map to it (ADR 0016). A container will not self-promote to My Day on
its own `due`. `bug` behaves like `task` for My Day; it is stamped only for fidelity. The
enum stays `task` / `pbi` / `bug` — there are no separate `epic` / `feature` values.

## Process

### 1. Inventory the vault (read-only)

From the Glasswork repo root, run:

```
dotnet run --project tools/Glasswork.Maintenance/Glasswork.Maintenance.csproj -- inventory --vault <VAULT_ROOT>
```

This prints a JSON array of every task file under `wiki/todo` and `wiki/todo/done` (it does
**not** descend into `*.artifacts/`). Each entry has `relative_path`, an `ado` resolution
(`status` = `resolved` / `none` / `ambiguous`, and `id` when resolved), `has_type`, and
`normalized_type`.

### 2. Pick the candidates

Keep only entries where:

- `has_type` is `false` (don't re-stamp; already-typed files are no-ops anyway), **and**
- `ado.status` is `resolved` (you need a work-item id to classify).

Surface — but do not classify — entries with `ado.status` of `ambiguous` (multiple
conflicting ADO ids in the file) or `none` (no ADO link); list them under "Needs manual
review" in the summary.

### 3. Classify against ADO

Batch-query the work-item types for the candidate ids (chunks of ≤200):

```
wit_get_work_items_batch_by_ids(project="One", ids=[...], fields=["System.WorkItemType"])
```

Apply the **ADO → Glasswork type map** above. Build a classifications array containing the
`pbi` results (Product Backlog Item / User Story / Epic / Feature) and the `bug` results:

```json
[
  { "relative_path": "general-arm-manifests-improvements.md", "ado_id": 14480984, "type": "pbi" },
  { "relative_path": "some-bug.md", "ado_id": 31736539, "type": "bug" }
]
```

Write it to a temp file (e.g. `%TEMP%\backfill-classifications.json`). Do **not** include
`Task` rows (the default is omitted).

### 4. Dry run (read-only preview)

```
dotnet run --project tools/Glasswork.Maintenance/Glasswork.Maintenance.csproj -- apply --vault <VAULT_ROOT> --classifications <file.json>
```

Without `--apply` this is a **dry run**: it writes nothing and prints a report
(`stamped` = would-stamp, `skipped_already_typed`, `skipped_drift`, `skipped_conflict`,
`unstampable`, `invalid`). Show the report to the user. Investigate any `invalid` (bad path /
non-pbi-bug type / duplicate), `unstampable` (resolvable ADO id but malformed frontmatter — no
place to insert), or `skipped_drift` (the file's ADO id no longer matches what you classified)
before proceeding. `skipped_conflict` only appears on `--apply` (a file changed on disk between
read and write — re-run to pick it up).

### 5. CONFIRM, then apply

Stamping the vault is **CONFIRM-tier** (see guardrails). Ask the user to confirm the dry-run
report in one batch. On confirmation, re-run with `--apply`:

```
dotnet run --project tools/Glasswork.Maintenance/Glasswork.Maintenance.csproj -- apply --vault <VAULT_ROOT> --classifications <file.json> --apply
```

The console registers every write with `SelfWriteCoordinator` (hard rule 5), so a running
Glasswork app will not raise a spurious "changed on disk" banner. Edits are surgical
single-line frontmatter inserts — `ado_link:` and all other fields are preserved (no churn).

### 6. Verify and summarize

Re-running `apply --apply` should be a no-op (everything reports `skipped_already_typed`).
Report to chat:

```
Type backfill complete:

Stamped (N): pbi=<count>, bug=<count>
  - <relative_path>  -> type: <pbi|bug>  (ADO <id>, <WorkItemType>)
  ...

Needs manual review (M):
  - <relative_path>  [ado: ambiguous|none]      # unresolvable / conflicting ADO id
  - <relative_path>  [unstampable]              # resolvable id but malformed frontmatter
  ...

Not changed: <count> already-typed, <count> drift, <count> conflict (re-run), <count> invalid (list any).
```

If the user wants confirmation it worked: an active (non-done) PBI with `due <= today`
should no longer appear in My Day via its **own** due. A PBI still shown via a direct
`my_day` pin or a flagged/due **subtask** is expected — call those out separately, don't
treat them as failures.

## D8 Guardrails (apply to every action you take)

Baked into this skill; they override any conflicting user request. They mirror the
guardrails in `glasswork-import-sprint`.

### HARD NO — never, under any circumstances
- Do not delete or move files in the wiki vault.
- Do not modify any frontmatter field other than adding `type:` (never touch `id`,
  `created`, `ado_link`, `due`, `status`, `parent`, etc.).
- Do not stamp a type other than `pbi` or `bug`, and never stamp `task` (it is the omitted
  default).
- Do not edit task files directly with a text editor / generic file tools — always go
  through the maintenance console so writes are surgical and self-write-registered.

### CONFIRM — only with explicit user confirmation in this session
- Run `apply --apply` (the write step in §5).

### ALLOWED — proceed without asking
- Read from any source: ADO, the vault, the inventory output.
- Run `inventory` and the dry-run `apply` (no `--apply`) — both are read-only.
- Print the summary to chat.

If a request would require breaking a HARD NO, refuse and name the guardrail.

## Scope notes — what this skill does NOT do

- **Does not** import or update tasks from ADO — it only adds a missing `type:` field. Use
  `glasswork-import-sprint` for imports.
- **Does not** stamp `Task` items (the default is omitted). Containers — Product Backlog
  Item, User Story, Epic, and Feature — all map to `pbi` (ADR 0016).
- **Does not** touch files with no resolvable ADO id, or with ambiguous ADO ids — those are
  surfaced for manual review.
- **Does not** change `due`, `status`, or any other field; a stamped PBI keeps its dates.
- **Is idempotent** — safe to re-run; already-typed files are skipped.
