# Parent Task compatibility migration runbook

This runbook is the operator contract for issue #516. The migration tooling is built by
issue #513, but #513 must never run it against the live Vault or publish/install a
release.

The unsafe interval starts only after all Glasswork and `glasswork-mcp` processes are
closed. It ends when the compatibility release is installed and its real app smoke gates
pass. Any failed gate restores the verified migration backup before the run ends.

## Inputs

- A reviewed checkout of current `main` containing every #505 implementation slice.
- The live Vault root.
- An output path outside the Vault for `parent-migration-plan.json`.
- An empty output directory outside the Vault for the changed-path backup.
- An ADO evidence JSON file:

```json
[
  {
    "task_id": "canonical-glasswork-task-id",
    "ado_id": 12345678,
    "source_kind": "Product Backlog Item",
    "retrieved_at": "2026-08-31T16:00:00Z"
  }
]
```

`source_kind` is copied only when the Task ID and uniquely resolved ADO ID both match.
Missing evidence leaves `source_kind` absent and reports the Task. Duplicate,
contradictory, or malformed evidence blocks execution.

## 1. Confirm release readiness

Verify every issue blocking #516 is delivered through its merged PR and all required
Core, MCP, skill, and visual-verification evidence is green. Prepare the compatibility
Release PR inputs, but do not publish or install anything yet.

**STOP — operator approval required before credentials or external reads.**

After approval, query ADO for the exact `System.WorkItemType` of every uniquely resolved
legacy PBI Task and write the evidence file. The maintenance CLI never authenticates to
or queries ADO.

## 2. Produce and review the live dry-run

This command reads the Vault but writes only the plan path outside it:

```powershell
dotnet run --project tools\Glasswork.Maintenance\Glasswork.Maintenance.csproj -- `
  parent-migration dry-run `
  --vault <VAULT_ROOT> `
  --ado-evidence <ADO_EVIDENCE_JSON> `
  --plan <PLAN_JSON_OUTSIDE_VAULT>
```

Review every converted Parent Task, promoted child Task, deterministic child ID, source
ordinal, Parent rewrite, source-kind result, unresolved value, collision, and invariant
diagnostic. The result must be `ready`. Unresolved `source_kind` entries are allowed only
when explicitly accepted; blocking diagnostics are never accepted.

Do not edit the plan. Its hash binds the Vault read basis, ADO evidence, proposed bytes,
and deterministic output paths. Any Vault drift requires a new dry-run and review.

## 3. Close all Vault writers

**STOP — operator approval required before closing processes, creating the live backup,
or mutating the Vault.**

After approval:

1. Close Glasswork.
2. End every Copilot/agent session using Glasswork MCP.
3. Verify no `Glasswork` or `glasswork-mcp` process remains.
4. Keep Obsidian and all other Vault writers idle for the complete unsafe interval.

The CLI independently checks the Glasswork process names and refuses execution while any
remain.

## 4. Execute with a verified changed-path backup

Run from a standalone terminal that does not start Glasswork MCP:

```powershell
dotnet run --project tools\Glasswork.Maintenance\Glasswork.Maintenance.csproj -- `
  parent-migration execute `
  --vault <VAULT_ROOT> `
  --plan <PLAN_JSON_OUTSIDE_VAULT> `
  --backup <EMPTY_BACKUP_DIRECTORY_OUTSIDE_VAULT>
```

Execution revalidates the complete read basis before creating the backup. The backup
contains exact original bytes for every modified Task and expected-absent entries for
every created child. `manifest.json` binds it to the accepted plan. All Task writes are
preceded by a durable journal and use atomic same-directory replacement.

If execution fails, do not remove the plan, backup, or retained journal. Continue only
with rollback.

## 5. Validate before release

```powershell
dotnet run --project tools\Glasswork.Maintenance\Glasswork.Maintenance.csproj -- `
  parent-migration validate `
  --vault <VAULT_ROOT> `
  --plan <PLAN_JSON_OUTSIDE_VAULT> `
  --backup <BACKUP_DIRECTORY>
```

The result must be `valid` with `rollback_viable: true`. This proves planned hashes,
promotion counts and fields, canonical Parent IDs, acyclicity, ownership rules,
parse/serialize/parse stability, unchanged Artifact fingerprints, and backup integrity.

## 6. Publish and install the compatibility release

**STOP — operator approval required before Release PR merge, workflow dispatch, release
publication, or installation.**

Follow ADR 0012 and the `glasswork-release` skill:

1. Land the narrow Release PR on `main`.
2. Confirm no requested app tag/release already exists.
3. Dispatch the `Release` workflow on `main`; do not create a tag or release directly.
4. Monitor it to success.
5. Install the published compatibility release immediately.

No MCP release is implied. App and MCP release streams remain independent under ADR 0023.

## 7. Run real smoke gates

Keep the backup until all gates pass:

1. Start the installed app against the migrated Vault.
2. Verify startup and inspect the behavior-specific Parent Task, Task Detail, My Day,
   Backlog, and picker renders.
3. Confirm canonical `parent` and legacy `pbi` both behave as Parent Tasks.
4. Confirm Obsidian and MCP reads can parse the Vault.
5. Perform the separately approved agent-write smoke only after all earlier gates pass.

Record the migration report, release/workflow reference, installed version, captured
visual evidence, unresolved `source_kind` follow-ups, and rollback status on #516.

## Rollback triggers

Rollback immediately if execute, validation, Release publication, installation, app
startup, visual verification, Vault parsing, MCP reads, or the approved agent-write smoke
fails.

Keep all writers closed and run:

```powershell
dotnet run --project tools\Glasswork.Maintenance\Glasswork.Maintenance.csproj -- `
  parent-migration rollback `
  --vault <VAULT_ROOT> `
  --plan <PLAN_JSON_OUTSIDE_VAULT> `
  --backup <BACKUP_DIRECTORY>
```

Rollback preflights every changed and unchanged read-basis path before writing. It
restores modified Tasks byte for byte and removes a created child only when its current
bytes match the accepted post-migration hash. Any external drift blocks rollback before
it overwrites data. Retain all evidence and resolve the named path manually rather than
forcing a partial restore.

After rollback, rerun the dry-run against the restored Vault. The same input must produce
the same deterministic child IDs. Do not resume release work until the pre-migration
Vault is revalidated and a new operator decision is recorded.
