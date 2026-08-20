# glasswork-mcp

`glasswork-mcp` is a standalone [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that gives AI agents typed read/write access to a [Glasswork](https://github.com/tjegbejimba/Glasswork) task vault. It communicates over stdio and requires no running Glasswork app instance.

> **v0.11.0**: adds capability-gated Authoritative ADO reconciliation while
> preserving the fail-closed mutation contract. Clients must provide a
> `mutation_id` and an applicable `if_absent` or Resource Revision precondition
> on every Task and task-owned-file mutation. See [CHANGELOG](./CHANGELOG.md)
> for migration notes.

---

## Installation

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)
- A Glasswork vault directory (Obsidian-backed markdown task files)

### Exact published install (PowerShell)

```powershell
.\scripts\install-mcp.ps1 -Version 0.11.0
```

The script downloads the exact package and checksum assets from the
`mcp-v0.11.0` GitHub Release, verifies the tag source revision, stages and
executes the replacement, then installs it under
`%LocalAppData%\Glasswork\Mcp\versions\0.11.0+<commit>`. It atomically updates
only the Glasswork command in `~\.copilot\mcp-config.json`, preserving every
other server and setting. Running sessions keep their loaded build; new sessions
start the verified version immediately. Re-running the command recognizes an
already-current build instead of trusting the package version alone.

### Development package install

```powershell
$revision = (git rev-parse HEAD).Trim()
dotnet pack src\Glasswork.Mcp -c Release -o nupkg "-p:RepositoryCommit=$revision"
.\scripts\install-mcp.ps1 `
    -Version 0.11.0 `
    -PackagePath .\nupkg\glasswork-mcp.0.11.0.nupkg
```

Development packages still require an exact semantic version and source
revision. Change the project version whenever the binary changes; never reuse a
published version.

To inspect the installed build:

```powershell
$state = Get-Content "$env:LOCALAPPDATA\Glasswork\Mcp\current.json" -Raw |
    ConvertFrom-Json
& $state.executablePath --version
```

---

## Vault discovery

`glasswork-mcp` discovers the vault directory in this order on startup:

1. **`GLASSWORK_VAULT` environment variable** — set this to the **Obsidian vault root** (the top-level folder you opened in Obsidian, e.g. `~/Wiki`). The server resolves the task directory internally as `<GLASSWORK_VAULT>/wiki/todo/`.
2. **App state file** — the path stored by the Glasswork desktop app in `%LocalAppData%\Glasswork\ui-state.json` (key `vault.path`). Opening the Glasswork app and selecting a vault populates this automatically. The persisted key also refers to the **vault root** (not the task directory).
3. **Exit with clear error** — if neither source resolves to an existing directory, **or if the task directory `<vault root>/wiki/todo` does not exist**, the server exits with a diagnostic naming the attempted paths. This prevents silent task-misplacement.

**Contract (Issue #132 fix):**  
Both `GLASSWORK_VAULT` and `vault.path` always refer to the **vault root**. The task directory is always `<vault root>/wiki/todo`. If your vault uses a different structure, create the `wiki/todo` subdirectory — MCP will not auto-create it.

### Setting the env var

```bash
# Unix / WSL — set to the Obsidian vault root (parent of wiki/todo/)
export GLASSWORK_VAULT=/path/to/your/vault-root

# PowerShell
$env:GLASSWORK_VAULT = "C:\path\to\your\vault-root"
```

---

## Configuring in Copilot CLI

`install-mcp.ps1` creates or updates the `glasswork` entry in
`~/.copilot/mcp-config.json` automatically. It changes only the command path and
preserves existing tools, arguments, environment values, and every other MCP
server. If you have opened Glasswork and configured the vault, no explicit
`GLASSWORK_VAULT` entry is required.

---

## Configuring in Claude Desktop

Open Claude Desktop's settings (`claude_desktop_config.json`) and add:

```json
{
  "mcpServers": {
    "glasswork": {
      "command": "C:\\Users\\you\\AppData\\Local\\Glasswork\\Mcp\\versions\\<build-identity>\\glasswork-mcp.exe",
      "env": {
        "GLASSWORK_VAULT": "/absolute/path/to/your/vault-root"
      }
    }
  }
}
```

Read the exact executable path from
`%LocalAppData%\Glasswork\Mcp\current.json`. Copilot configuration is migrated
automatically; other MCP clients must update their command when choosing a new
side-by-side version.

---

## Tool reference

| Tool | Status | Description |
|---|---|---|
| `get_capabilities` | v1.0 contract | Read-only handshake for MCP contract version and supported guarantees |
| `query_tasks` | v0.8.0 | Query Tasks by typed fields and dependency readiness with deterministic paging |
| `transact_tasks` | v0.9.0 | Idempotently create explicit-ID Tasks or conditionally update existing Tasks |
| `cancel_task` | current | Reversibly archive an active Task |
| `restore_task` | current | Restore a Cancelled Task to todo |
| `reconcile_ado_task` | current | Apply the exact authoritative ADO cancellation/resumed-active state machine |
| `preflight_delete_task` | current | Preview Hard-deletion impact and obtain its preflight revision |
| `delete_task` | current | Permanently delete a guarded Task subtree and repair inbound Wiki links |
| `add_task` | v0.2.0 | Create a new task file |
| `update_task` | v0.8.0 | Update an existing task (partial updates supported) |
| `list_tasks` | v0.2.0 | List task summaries (structural enumeration — filter by status or parent) |
| `get_task` | v0.3.0 | Return full task content (v0.4.0: +artifact bodies) |
| `get_artifact` | v0.4.0 | Read a single artifact by task_id + filename |
| `add_artifact` | v0.3.0 | Create a task artifact file |
| `load_context` | v0.4.0 | One-call full-context fetch: task + artifact bodies + recursive subtasks + backlinks |
| `search_tasks` | v0.5.0 | Topic-driven task discovery — ranked, scoped, with per-hit snippets |
| `set_my_day` | v0.6.0 | Direct-pin an existing task into My Day for a date |
| `list_backlinks` | v0.7.0 | List wiki pages that reference a task via `[[task-id]]` |

### `get_capabilities`

Use this read-only operation before relying on optional workflow guarantees:

```json
{
  "contract_version": "1.0",
  "implemented_capabilities": [
    "relation_aware_queries",
    "resource_revisions",
    "read_assertions",
    "typed_transactions",
    "complete_set_relationships",
    "transaction_idempotency",
    "recoverable_all_or_none_commit",
    "guarded_hard_deletion",
    "authoritative_ado_reconciliation"
  ]
}
```

`implemented_capabilities` are guarantees clients may rely on now. There is no
runtime downgrade or compatibility negotiation: a server that does not
advertise the complete set must not be used for this contract.

Every response containing a Task or Task summary includes `resource_revision`.
It is an opaque, versioned token derived from the exact bytes of that Task's
markdown file (currently formatted with the `rr1-` version prefix). Identical
bytes produce the same token regardless of filesystem timestamps, while any
byte change produces a different token. Clients must compare tokens for
equality and must not parse or otherwise depend on the digest format.

### `transact_tasks`

`transact_tasks` accepts typed operations. Every mutation requires a
client-generated `mutation_id` and an applicable precondition. `create_task` requires an
explicit safe Task ID and `if_absent: true`; it never generates a title-based
collision suffix. The operation returns the created Task and its Resource
Revision, reports an existing ID as a conflict, and durably replays an exact
request with the same `mutation_id`. `set_task_fields` requires the current
Task Resource Revision, rejects contradictory transaction/operation revisions,
preserves hand-formatted Markdown for semantic no-ops, and uses the same
journaled all-or-none recovery boundary.

All Task-bearing reads, including compatibility reads, include
`resource_revision`. Stable error envelopes use `conflict`,
`validation_error`, `precondition_required`, `mutation_id_reused`, and
`operation_failed`; clients must branch on `error` rather than message text.
Exact replay of the same request and `mutation_id` returns the recorded outcome.
Reusing a mutation ID for a changed request is rejected.

`query_tasks` returns a coherent page plus a complete `read_basis`, and
`transact_tasks` accepts read-only Revision assertions, typed field operations,
and complete relationship replacement. Recovery runs before managed access and
commits changes all-or-none.

### `reconcile_ado_task`

This is the only MCP interface for Authoritative ADO reconciliation. It requires
the named `authoritative_ado_reconciliation` capability and accepts:

```json
{
  "task_id": "imported-task-id",
  "ado_work_item_id": 12345678,
  "authoritative_state": "Removed",
  "mutation_id": "ado-reconcile-12345678-1",
  "if_revision": "rr1-..."
}
```

Core verifies that the Task represents that ADO work-item ID before applying an
exact, case-sensitive state transition. `Removed` cancels only `todo`, `doing`,
or `blocked`, stamps reason `ADO work item removed`, and clears `my_day`.
`Active`, `In Progress`, and `In Review` restore only a Cancelled Task directly
to `doing` while clearing Cancellation metadata. Every other state is a no-op;
`done` is never reopened or reclassified. The response reports
`source: "azure-devops"`, the authoritative state, `action` (`cancelled`,
`restored`, or `unchanged`), final status, and the new Resource Revision.

The operation uses the existing journaled conditional mutation and idempotency
contract. It does not call `delete_task`, and clients must not replace it with
raw Cancellation YAML or `restore_task` followed by generic status mutation.

`add_artifact` uses the same fail-closed rule: creation requires
`mutation_id` and `if_absent: true`; overwrite requires `mutation_id` and the
artifact Resource Revision in `if_revision`. Clients upgrading from older
versions must stop sending unconditional calls and migrate `if_exists` task
creation to explicit `transact_tasks` operations.

```json
{
  "mutation_id": "create-123",
  "operations": [
    {
      "op": "create_task",
      "task_id": "workflow-child-1",
      "if_absent": true,
      "fields": {
        "title": "Implement the child workflow",
        "status": "todo",
        "priority": "medium",
        "type": "task",
        "parent_task_id": "workflow-parent",
        "tags": ["workflow"],
        "description": "Stable framing",
        "notes": "Initial context"
      }
    }
  ]
}
```

### `delete_task`

`delete_task` means **Hard deletion only**. Use `cancel_task` for the safe,
reversible archive lifecycle. Hard deletion applies to Tasks, Bugs, and PBIs and
requires all destructive guards. Call `preflight_delete_task` first and retain
its opaque `preflight_revision`:

```json
{
  "task_id": "obsolete-plan",
  "mutation_id": "delete-obsolete-plan-1",
  "if_revision": "rr1-...",
  "confirm_title": "Obsolete plan",
  "cascade_children": false,
  "if_preflight_revision": null
}
```

When descendants exist and `cascade_children` is false, the call fails without
mutation using `descendants_require_cascade` and returns `descendant_ids` plus
the complete preflight. Retry with a new `mutation_id`,
`cascade_children: true`, and that preflight's `preflight_revision` only after
reviewing the subtree. If the subtree or other impact changes, the opaque
revision conflicts and a refreshed preflight is returned.

Success returns `deleted_tasks`, `descendants`, `removed_artifacts`,
`rewritten_backlink_pages`, and `recovery_outcome`. Exact `[[task-id|alias]]`
links become the alias; bare `[[task-id]]` links become the deleted Task title.
The operation is journaled with hidden staged backups, rolls back ordinary
failures, finishes or rolls back deterministically after restart, and replays an
identical request by `mutation_id`. Recovery refuses to overwrite post-crash
Vault edits and retains invalid deletion journals/backups for explicit repair.

### When to use which tool

| Goal | Use |
|---|---|
| Know which tasks exist right now | `list_tasks` |
| Fetch a specific task you already know by ID | `get_task` or `load_context` |
| Read one artifact's content | `get_artifact` |
| Read all artifacts for a task | `load_context` or `get_task(include_artifact_bodies=true)` |
| Discover tasks related to a concept or keyword | `search_tasks` |
| Add an existing task to My Day | `set_my_day` |
| Archive abandoned work safely | `cancel_task` |
| Restore archived work | `restore_task` |
| Reconcile exact authoritative ADO removal/resumption | `reconcile_ado_task` after `get_capabilities` |
| Preview permanent deletion impact | `preflight_delete_task` |
| Irreversibly remove a reviewed Task subtree | `delete_task` |
| Orient before starting work on a new issue | `search_tasks` (find prior art), then `load_context` (deep-dive) |

### `query_tasks`

`query_tasks` evaluates one page against one managed Vault snapshot. It supports
typed predicates for `parent_task_id`, a status set, Task `type`, and required
Tags. The general `blocked_by` relationship is stored as a top-level list of
Task IDs. Duplicate IDs are canonicalized during parse and serialization.
Internally this is a thin translation to Core's stateless fresh-Vault Task Query
adapter; query policy, cursors, diagnostics, and read basis remain Core-owned.

**Input**

```json
{
  "parent_task_id": "string | null",
  "status": ["todo", "doing", "blocked", "done"],
  "type": "task | pbi | bug",
  "tags": ["string", "..."],
  "blocked_by_empty": "boolean",
  "blocked_by_status": ["done"],
  "order_by": "created_id | id",
  "limit": "integer in [1, 100]",
  "cursor": "opaque continuation token | null"
}
```

`blocked_by_empty` selects Tasks with no dependencies. `blocked_by_status`
requires every dependency target to have one of the requested statuses and does
not match a Task with no dependencies. Missing targets and self-edges return a
structured `validation_error` with diagnostic entries.

**Output**

```json
{
  "tasks": [
    {
      "id": "task-id",
      "title": "Task title",
      "status": "todo",
      "type": "task",
      "parent_id": null,
      "tags": ["workflow"],
      "blocked_by": ["dependency-id"],
      "description": "Description",
      "notes": "Notes",
      "resource_revision": "rr1-opaque-versioned-token"
    }
  ],
  "read_basis": [
    {
      "id": "dependency-id",
      "title": "Dependency task",
      "status": "done",
      "type": "task",
      "parent_id": null,
      "tags": [],
      "blocked_by": [],
      "description": "",
      "notes": "",
      "resource_revision": "rr1-opaque-versioned-token"
    }
  ],
  "next_cursor": "opaque continuation token | null"
}
```

Results are ordered by `id` by default, or by `created_id`, and are bounded by
`limit`. Each `read_basis` entry is a complete Task snapshot with its Resource
Revision. The set is deduplicated and includes each returned Task plus related
dependency Tasks whose state affected the relationship predicate.

### `search_tasks`

Topic-driven task discovery. Splits the query on whitespace and requires **all tokens** to match (AND semantics). Searches across up to five field types, returns ranked results with per-hit field clues and a short snippet.

**Input**

```json
{
  "query": "string (required) — free-text topic query; max 500 chars",
  "in": ["title", "description", "notes", "subtasks", "tags"],
  "tags": ["string", "..."],
  "status": ["todo", "doing", "done"],
  "limit": "integer (optional, default 20, clamped to [1, 100])"
}
```

- **`in`** (optional): restrict which fields are searched. Omit to search all five.
- **`tags`** (optional): AND filter — every listed tag must be present on the task.
- **`status`** (optional): include only tasks with one of the listed statuses (`todo`, `doing`, `blocked`, `done`).
- **`limit`** (optional): max results. Values outside `[1, 100]` are silently clamped.

**Output**

```json
{
  "tasks": [
    {
      "id": "string",
      "title": "string",
      "status": "\"todo\" | \"doing\" | \"done\"",
      "parent_id": "string | null",
      "matched_in": ["title", "notes"],
      "snippet": "string — ~120-char excerpt from the best matched field",
      "ready": true,
      "urgency_score": 12.5,
      "backlink_count": 2,
      "resource_revision": "rr1-opaque-versioned-token"
    }
  ]
}
```

Results are ranked: tasks with a title match score higher than body-only matches. Tiebreak: newest first, then by ID. Artifact body content is **not** searched in v1.

**Errors** — returned as the structured `{ "error": "<code>", "message": "<text>" }` envelope:

| `error` value | When |
|---|---|
| `invalid_query` | Empty/whitespace query or query longer than 500 characters |
| `invalid_in_field` | Unknown value in `in[]` (allowed: `title`, `description`, `notes`, `subtasks`, `tags`) |
| `invalid_status` | Unknown value in `status[]` (allowed: `todo`, `doing`, `blocked`, `done`) |
| `invalid_argument` | Defensive fallback for any other input-validation failure surfaced from `Glasswork.Core` |

---

### `add_task`

**Input**

```json
{
  "title": "string (required)",
  "description": "string (optional) — becomes the Description body section",
  "parent_task_id": "string (optional) — ID of the parent task",
  "status": "\"todo\" | \"doing\" | \"blocked\" | \"done\" (optional, defaults to todo)",
  "blocked_reason": "string (required when status is blocked)"
}
```

**Output (success)**

```json
{
  "task_id": "string — the generated task ID (slug from title)",
  "path": "string — todo-relative path to the created task file, e.g. fix-the-bug.md"
}
```

**Output (errors)**

| `error` value | When |
|---|---|
| `invalid_title` | `title` is null, empty, or whitespace |
| `invalid_status` | `status` is not one of `todo`, `doing`, `blocked`, `done` |
| `invalid_blocked_reason` | `status` is `blocked` but `blocked_reason` is null, empty, or whitespace |

### `update_task`

Update an existing task. Only provided fields are written; omitted fields remain untouched on disk.

**Input**

```json
{
  "task_id": "string (required)",
  "fields": {
    "title": "string (optional)",
    "status": "\"todo\" | \"doing\" | \"blocked\" | \"done\" (optional)",
    "blocked_reason": "string | null (optional) — required when setting status to blocked; used alone to edit blocker details on an already-blocked task",
    "blocked_from_status": "\"todo\" | \"doing\" | null (optional) — only used to repair malformed blocked metadata",
    "description": "string (optional)",
    "notes": {
      "value": "string | null (required in notes object)",
      "append": "boolean (optional, default false)"
    },
    "priority": "string (optional)",
    "parent_task_id": "string | null (optional) — empty string or null clears parent",
    "ado_link": "integer | null (optional) — ADO work item ID; null clears it",
    "ado_title": "string | null (optional) — ADO work item title"
  }
}
```

**Output (success)**

```json
{
  "task_id": "string",
  "updated_fields": ["status", "notes"]
}
```

The `updated_fields` array lists field names that actually changed. Fields provided but already equal to the current value are omitted (no-op updates don't appear here).

**Notes append semantics:**
- When `fields.notes.append` is `true` and existing Notes is non-empty: inserts a blank line separator (`existing.TrimEnd() + "\n\n" + new`)
- When `fields.notes.append` is `true` and existing Notes is empty: writes the new value directly
- When `fields.notes.append` is `false` or omitted: replaces the entire Notes body

**Output (errors)**

| `error` value | When |
|---|---|
| `not_found` | Task with `task_id` doesn't exist |
| `invalid_status` | `status` is not one of `todo`, `doing`, `blocked`, `done` |
| `invalid_blocked_reason` | Blocked transition attempted without a non-empty `blocked_reason` |
| `invalid_blocked_from_status` | `blocked_from_status` is not `todo` / `doing`, or was supplied outside malformed-block repair |
| `invalid_blocked_state` | `blocked_reason` / `blocked_from_status` were supplied for a non-blocked task |
| `repair_required` | Task is `status: blocked` but missing valid blocker metadata; repair must provide both `blocked_reason` and `blocked_from_status` |

### Blocked-task contract

- `status: "blocked"` requires `blocked_reason`.
- `update_task` routes blocked transitions through the same Core rules as the WinUI app:
  - non-blocked -> blocked: marks blocked and stamps `blocked_at` + `blocked_from_status`
  - blocked -> `todo` / `doing`: resumes and clears blocker metadata
  - blocked -> `done`: completes directly and clears blocker metadata
  - blocked + `blocked_reason` only: edits blocker text without resetting `blocked_at`
  - malformed blocked task + `blocked_reason` + `blocked_from_status`: repairs the metadata; if `blocked_at` was missing/invalid, repair time is used
- Task summaries and full task payloads now surface `status: "blocked"` plus optional `blocked_reason`, `blocked_at`, `blocked_from_status`, and `needs_blocker_details`.
| `invalid_parent` | `parent_task_id` doesn't exist in the vault |

### `list_tasks`

`list_tasks` is a thin transport projection over the same stateless fresh-Vault
Task Query adapter used by `query_tasks`; each call acquires a new coherent
managed snapshot.

**Input**

```json
{
  "status": "\"todo\" | \"doing\" | \"done\" (optional)",
  "parent_task_id": "string (optional)",
  "fields": ["string", "..."]
}
```

- **`fields`** (optional): when provided, each returned summary contains only the requested fields plus `id` (always included). Allowed values: `title`, `status`, `parent_id`, `path`, `created`, `priority`, `due`, `start`, `my_day`, `defer_until`, `ready`, `urgency_score`, `backlink_count`, `in_my_day_today`. Field names are case-folded, whitespace-trimmed, and de-duplicated; unknown names are silently dropped. Omitting `fields` (or passing `null` / `[]`) preserves the default shape below.

**Output (default — no `fields`)**

```json
{
  "tasks": [
    {
      "id": "string",
      "title": "string",
      "status": "\"todo\" | \"doing\" | \"done\"",
      "parent_id": "string | null",
      "path": "string — todo-relative path to the task file, e.g. fix-the-bug.md",
      "ready": true,
      "urgency_score": 12.5,
      "backlink_count": 2,
      "resource_revision": "rr1-opaque-versioned-token"
    }
  ]
}
```

**Output (with `fields: ["created", "priority"]`)**

```json
{
  "tasks": [
    { "id": "string", "resource_revision": "rr1-opaque-versioned-token", "created": "yyyy-MM-dd", "priority": "medium" }
  ]
}
```

**Output (with `fields: ["due", "my_day", "in_my_day_today"]`)**

```json
{
  "tasks": [
    {
      "id": "string",
      "resource_revision": "rr1-opaque-versioned-token",
      "due": "yyyy-MM-dd | null",
      "my_day": "yyyy-MM-dd | null",
      "in_my_day_today": true
    }
  ]
}
```

Results are sorted by created date ascending, then by ID for stability.

**Output (error)**

| `error` value | When |
|---|---|
| `invalid_status` | `status` is not one of `todo`, `doing`, `done` |

---

### `get_task`

**Input**

```json
{
  "task_id": "string (required) — task ID to look up",
  "include_artifact_bodies": "boolean (optional, default false) — when true, embed artifact content in each artifacts[] entry"
}
```

**Output (success — default)**

```json
{
  "id": "string",
  "title": "string",
  "status": "\"todo\" | \"doing\" | \"done\"",
  "parent_id": "string | null",
  "resource_revision": "rr1-opaque-versioned-token",
  "description": "string — full Description body (ADR 0002)",
  "notes": "string — full Notes body (ADR 0002)",
  "artifacts": [
    {
      "filename": "string — e.g. plan.md",
      "path": "string — todo-relative path, e.g. task-id.artifacts/plan.md"
    }
  ]
}
```

**Output (success — with `include_artifact_bodies: true`)**

```json
{
  "id": "string",
  "title": "string",
  "status": "\"todo\" | \"doing\" | \"done\"",
  "parent_id": "string | null",
  "resource_revision": "rr1-opaque-versioned-token",
  "description": "string",
  "notes": "string",
  "artifacts": [
    {
      "filename": "string — e.g. plan.md",
      "path": "string — todo-relative path, e.g. task-id.artifacts/plan.md",
      "content": "string — full markdown content"
    }
  ]
}
```

**Output (not found)**

```json
{
  "error": "not_found",
  "message": "string"
}
```

Re-reads the vault and artifact folder on every call (no cache). When `include_artifact_bodies` is omitted or `false`, behaviour is byte-identical to v0.3.0.

---

### `get_artifact`

Read a single artifact's content by task ID and filename. Built for agent-to-agent handoff when only one artifact needs to be read.

**Input**

```json
{
  "task_id": "string (required) — owning task ID",
  "filename": "string (required) — artifact filename, e.g. plan.md"
}
```

**Output (success)**

```json
{
  "content": "string — full markdown content",
  "path": "string — todo-relative path, e.g. task-id.artifacts/plan.md"
}
```

**Output (errors)**

| `error` value | When |
|---|---|
| `not_found` | Task or artifact file does not exist |
| `path_traversal` | `filename` contains `..`, is absolute, or resolves outside the artifact folder |

Re-reads from disk on every call (no cache). `filename` must be a simple name with no path separators. Under `GLASSWORK_MCP_TRACE=1`, the log line includes a `read_artifact` phase.

---

### `add_artifact`

**Input**

```json
{
  "task_id": "string (required) — owning task ID",
  "filename": "string (required) — must end in .md, no path separators",
  "content": "string (required) — full markdown content",
  "mode": "string (optional) — \"create\" (default) | \"overwrite\""
}
```

**Output (success)**

```json
{
  "path": "string — todo-relative path to the created file, e.g. task-id.artifacts/plan.md"
}
```

**Output (errors)**

| `error` value | When |
|---|---|
| `not_found` | The task ID does not exist in the vault |
| `invalid_filename` | `filename` is null, empty, whitespace, or does not end in `.md` |
| `invalid_content` | `content` is null |
| `invalid_mode` | `mode` is not null, `"create"`, or `"overwrite"` (case-insensitive, whitespace-trimmed) |
| `path_traversal` | `filename` contains a path separator (`/` or `\`), `..`, is absolute, or resolves outside the artifact folder |
| `conflict` | A file with that name already exists and `mode` is `"create"` (or omitted). Pass `mode: "overwrite"` to replace existing files |

**Mode behavior:**
- `mode: "create"` (or omitted) — create-only semantics. Returns `{error: "conflict"}` if the file already exists.
- `mode: "overwrite"` — create-or-replace semantics. If the file exists, replaces its content. If it doesn't exist, creates it. Use this for iterative agent workflows that refine artifacts across multiple turns.

Artifacts are stored under `<vault>/wiki/todo/<task-id>.artifacts/<filename>`. The write is registered with `SelfWriteCoordinator` so the running Glasswork app does not raise a spurious "external change" banner.

---

### `set_my_day`

Direct-pins an existing task into **My Day** by setting task-level `my_day` frontmatter to a date. This follows ADR 0013's date-scoped pin model: the task promotes into My Day only when `my_day` equals the user's current local date. The tool does not change due dates, priority, status, subtasks, Description, Notes, or artifacts.

**Input**

```json
{
  "task_id": "string (required) — task ID to pin",
  "my_day": "yyyy-MM-dd (optional) — defaults to today's local date"
}
```

**Output (success)**

```json
{
  "task_id": "string",
  "my_day": "yyyy-MM-dd",
  "path": "string — todo-relative path to the updated task file, e.g. task-id.md"
}
```

**Output (errors)**

| `error` value | When |
|---|---|
| `not_found` | The task ID does not exist in the vault |
| `invalid_my_day` | `my_day` is not in `yyyy-MM-dd` format |

The write is registered with `SelfWriteCoordinator` so the running Glasswork app does not raise a spurious "external change" banner.

---

### `load_context`

Returns a task's complete context bundle — task content, every artifact's body, all subtasks recursively to `depth`, and all backlinks — in a single call. Built for agent handoffs (e.g. Ralph loop) where chaining `get_task` + N artifact reads + `list_tasks` + backlink discovery is expensive and error-prone.

**Input**

```json
{
  "task_id": "string (required) — task ID to load",
  "depth": "int (optional, default 1) — subtask recursion depth, clamped to [0, 3]"
}
```

`depth` semantics: `0` returns no subtasks; `1` (default) returns direct children only; `2`/`3` recurse further. Values `> 3` are silently clamped (no error). Negative values are treated as `0`.

**Output (success)**

```json
{
  "task": {
    "id": "string",
    "title": "string",
    "status": "\"todo\" | \"doing\" | \"done\"",
    "parent_id": "string | null",
    "resource_revision": "rr1-opaque-versioned-token",
    "description": "string",
    "notes": "string"
  },
  "artifacts": [
    {
      "filename": "string — e.g. plan.md",
      "path": "string — todo-relative, e.g. task-id.artifacts/plan.md",
      "content": "string — full file body"
    }
  ],
  "subtasks": [
    {
      "task": { "id": "...", "title": "...", "...": "same shape as task above" },
      "artifacts": [ { "filename": "...", "path": "...", "content": "..." } ],
      "subtasks": [ "...further subtrees to remaining depth..." ]
    }
  ],
  "backlinks": [
    {
      "source_path": "string — vault-root-relative path to the linking page, e.g. wiki/concepts/foo.md (always forward slashes). NOTE: this is the one path field that is vault-root-relative, not todo-relative — backlinks point at pages outside wiki/todo/.",
      "source_title": "string — H1 or first non-empty line",
      "page_type": "\"concept\" | \"decision\" | \"incident\" | \"system\" | \"other\""
    }
  ]
}
```

**Output (not found)**

```json
{
  "error": "not_found",
  "message": "string"
}
```

**Design notes**

- **Backlinks are root-only.** Subtask payloads include their own artifacts and nested subtasks but NOT a `backlinks` field. The backlink scan is the dominant cost; running it per subtree would blow up payload size and latency without clear agent value. Agents that need a subtask's backlinks can issue a follow-up `load_context` rooted at that subtask.
- **Stateless re-read.** The backlink index is rebuilt per call (ADR 0007 §6). ADR 0005 measures this at well under 2s on a 10k-file vault on cold start.
- **Cycle-safe.** A visited-set guards the BFS so hand-edited vaults with parent loops do not stack-overflow.
- **`not_found` short-circuits the backlink build** to avoid the scan cost on misses.

**Example payload**

For task `issue-137-mcp-load-context` with one artifact `plan.md`, one direct child `child-task-a`, and one concept page referencing it:

```json
{
  "task": {
    "id": "issue-137-mcp-load-context",
    "title": "MCP: load_context tool",
    "status": "doing",
    "parent_id": null,
    "resource_revision": "rr1-opaque-versioned-token",
    "description": "Single-call full-context fetch for agent handoff.",
    "notes": ""
  },
  "artifacts": [
    {
      "filename": "plan.md",
      "path": "issue-137-mcp-load-context.artifacts/plan.md",
      "content": "# Plan\n\nReuse VaultService, BacklinkIndex..."
    }
  ],
  "subtasks": [
    {
      "task": {
        "id": "child-task-a",
        "title": "Child A",
        "status": "todo",
        "parent_id": "issue-137-mcp-load-context",
        "resource_revision": "rr1-opaque-versioned-token",
        "description": "",
        "notes": ""
      },
      "artifacts": [],
      "subtasks": []
    }
  ],
  "backlinks": [
    {
      "source_path": "wiki/concepts/agent-handoff.md",
      "source_title": "Agent Handoff",
      "page_type": "concept"
    }
  ]
}
```

Under `GLASSWORK_MCP_TRACE=1`, the log line for a `load_context` call includes per-phase timings: `load_task`, `load_artifacts`, `load_subtasks`, `load_backlinks`.

---

### `list_backlinks`

Returns metadata for every wiki page that references a task via `[[task-id]]` wikilinks. Reuses the same `BacklinkIndex` from ADR 0005 that powers the App's Backlinks section on TaskDetail. Task→task references are **not** returned (per ADR 0005, the index excludes `wiki/todo/` — backlinks are strictly incoming references from non-task wiki pages like concepts, decisions, incidents, systems).

**Input**

```json
{
  "task_id": "string (required) — task ID to look up backlinks for"
}
```

**Output (success)**

```json
{
  "backlinks": [
    {
      "linking_page_path": "string — vault-root-relative path, e.g. wiki/concepts/foo.md (always forward slashes)",
      "linking_page_title": "string — H1 or first non-empty line from the linking page",
      "page_type": "\"concept\" | \"decision\" | \"incident\" | \"system\" | \"other\"",
      "last_modified_utc": "string — ISO 8601 datetime, e.g. 2024-06-01T12:34:56Z"
    }
  ]
}
```

Returns an empty `backlinks` array (not an error) when the task exists but has no incoming references.

**Output (not found)**

```json
{
  "error": "not_found",
  "message": "string"
}
```

**Design notes**

- **Per-page deduplication**: If a page mentions the task multiple times, only one entry appears (per ADR 0005).
- **Display text is stripped**: `[[task-id|My Label]]` counts as a backlink; the display text is ignored.
- **Stateless read (ADR 0007 §6)**: The backlink index is built fresh on every `list_backlinks` call. No caching, no stale results.

Under `GLASSWORK_MCP_TRACE=1`, the log line for a `list_backlinks` call includes the `backlinks_scan` phase.

---

## Profiling and structured logging

Every MCP tool call emits one structured JSON line (JSONL) to **stderr**. An optional file sink and per-phase trace are available via environment variables.

### Environment variables

| Variable | Value | Effect |
|---|---|---|
| `GLASSWORK_MCP_LOG` | `1` | Also write each log line to `<vault>/.glasswork/mcp.log`. The file is capped at ~1 MB; when the cap is exceeded the oldest half of entries is automatically pruned. |
| `GLASSWORK_MCP_TRACE` | `1` | Adds a `phases` object to each log line with per-phase wall-clock times. Off by default — zero overhead in normal use. |

### JSONL log-line shape

**Default (Layer 1 — always emitted to stderr):**

```json
{"ts":"2024-06-01T12:34:56.789Z","tool":"list_tasks","duration_ms":47,"result":"ok","task_count":3}
```

Fields:

| Field | Type | Description |
|---|---|---|
| `ts` | ISO-8601 UTC string | Timestamp of the log line |
| `tool` | string | Tool name (`add_task`, `list_tasks`, …) |
| `duration_ms` | number | Total wall-clock time in milliseconds |
| `result` | string | Outcome: `ok` \| `error` \| `conflict` \| `not_found` |
| `task_count` | number | *(list_tasks only)* Number of tasks returned after filtering |

**With `GLASSWORK_MCP_TRACE=1` (Layer 2 — adds `phases`):**

```json
{"ts":"2024-06-01T12:34:56.789Z","tool":"list_tasks","duration_ms":47,"result":"ok","task_count":3,"phases":{"glob":0,"yaml_parse":47,"filter":0,"sort":0}}
```

Phases instrumented in v1:

| Phase | Tools | Description |
|---|---|---|
| `glob` | `list_tasks` | Retained compatibility phase; emitted as `0` after Task Query migration |
| `yaml_parse` | `list_tasks` | Whole fresh-Vault Task Query execution, including snapshot acquisition and policy |
| `filter` | `list_tasks` | Retained compatibility phase; emitted as `0` because filtering is inside Task Query |
| `sort` | `list_tasks` | Retained compatibility phase; emitted as `0` because ordering is inside Task Query |
| `write` | `add_task`, `add_artifact`, `set_my_day` | Writing the file to disk |
| `load_task` | `get_task`, `load_context` | Loading the root task from disk |
| `scan_artifacts` | `get_task` | Enumerating the task's artifact folder |
| `load_artifacts` | `load_context` | Reading every artifact body for the root task |
| `load_subtasks` | `load_context` | Bounded BFS over the subtask tree |
| `load_backlinks` | `load_context` | Building the backlink index against the vault root |

### Enabling the file sink (PowerShell)

```powershell
$env:GLASSWORK_MCP_LOG = "1"
$env:GLASSWORK_MCP_TRACE = "1"
glasswork-mcp
```

### Parsing the log — p50 / p95 latency (PowerShell)

```powershell
$log = "$env:USERPROFILE\vault\.glasswork\mcp.log"   # adjust to your vault path
$rows = Get-Content $log | ForEach-Object { $_ | ConvertFrom-Json }
$ms = ($rows | Where-Object tool -eq 'list_tasks' | Select-Object -ExpandProperty duration_ms) | Sort-Object
$p50 = $ms[[int]($ms.Count * 0.50)]
$p95 = $ms[[int]($ms.Count * 0.95)]
"p50=${p50}ms  p95=${p95}ms"
```

---

## Tool preconditions

Tools advertise themselves via `ListTools` only when their **preconditions** pass. This is a **detection-before-display** pattern: instead of advertising five tools and letting the agent get a runtime error when it calls one against a missing vault, the server filters unavailable tools out of the listing entirely.

### How it works

1. Each tool method on `GlassworkTools` is annotated with `[ToolPrecondition("<name>")]` alongside its `[McpServerTool]` attribute.
2. At server startup, `ToolPreconditionRegistry` reflects over the tool type and builds a tool-name → precondition map.
3. A `ListTools` filter (registered via the SDK's `WithRequestFilters` / `AddListToolsFilter` hook) evaluates each tool's precondition and removes failing tools from the response.
4. A companion `CallTool` filter re-evaluates the precondition at call time. This closes the TOCTOU gap — if the vault disappears between `ListTools` and `CallTool`, the agent gets a clean `tool unavailable: <reason>` instead of a `NullReferenceException`.

### Annotating a tool

```csharp
[McpServerTool(Name = "add_task")]
[ToolPrecondition(VaultPathReadablePrecondition.PreconditionName)]
[Description("Create a new task file.")]
public string AddTask(...) { ... }
```

A tool with **no** `[ToolPrecondition]` attribute is always advertised and always invokable — same behavior as before the pattern was introduced.

### Built-in preconditions

| Name | Source | Fails when |
|---|---|---|
| `vault-path-readable` | `VaultPathReadablePrecondition` | Vault path was never resolved, the path points to a non-existent directory, or the directory cannot be read. |

### Authoring a new precondition

Implement `IToolPrecondition`:

```csharp
public sealed class MyPrecondition : IToolPrecondition
{
    public const string PreconditionName = "my-precondition";
    public string Name => PreconditionName;

    public ToolPreconditionResult Evaluate() =>
        IsHealthy()
            ? ToolPreconditionResult.Ok()
            : ToolPreconditionResult.Unavailable("reason shown in logs");
}
```

Register the instance in `Program.cs` alongside `VaultPathReadablePrecondition`, then annotate tools with `[ToolPrecondition(MyPrecondition.PreconditionName)]`.

**Evaluation rules:**

- Preconditions are evaluated **synchronously** and **uncached**. Keep them cheap (sub-millisecond). The vault-readable check is a `Directory.Exists` + tiny probe.
- If `Evaluate()` throws, the tool is treated as unavailable and the exception is logged to stderr via `McpLogger`. A bug in a precondition never crashes the server.
- Every filtered-out tool is logged once per `ListTools` call at the `Information` level so agents can debug why a tool isn't showing up.

---

## Architecture notes

- **Stdio transport only** — no network listener, no authentication.
- **Vault is the only writable surface** — the server cannot read or write files outside the vault root.
- **Path-traversal guard** — every path-like tool input is validated by `VaultPathGuard.EnsurePathInVault` before any file-system operation.
- **Stateless reads** — no in-process cache; every read call re-reads from disk.
- **Version**: follows semver, stays in `0.x` until the tool surface is stable.
