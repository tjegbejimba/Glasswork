# glasswork-mcp

`glasswork-mcp` is a standalone [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that gives AI agents typed read/write access to a [Glasswork](https://github.com/tjegbejimba/Glasswork) task vault. It communicates over stdio and requires no running Glasswork app instance.

> **v0.4.0 — M4**: `load_context` is now implemented — one-call full-context fetch for agent handoff. See [Tool reference](#tool-reference) for the schema.

---

## Installation

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Glasswork vault directory (Obsidian-backed markdown task files)

### One-command install (PowerShell)

```powershell
.\scripts\install-mcp.ps1
```

This script runs `dotnet pack` on the `Glasswork.Mcp` project and installs the resulting package as a global .NET tool named `glasswork-mcp`.

### Manual install

```powershell
dotnet pack src/Glasswork.Mcp -c Release -o nupkg
dotnet tool install -g glasswork-mcp --add-source ./nupkg
```

To update an existing install:

```powershell
dotnet tool update -g glasswork-mcp --add-source ./nupkg
```

---

## Vault discovery

`glasswork-mcp` discovers the vault directory in this order on startup:

1. **`GLASSWORK_VAULT` environment variable** — set this to the **Obsidian vault root** (the top-level folder you opened in Obsidian, e.g. `~/Wiki`). The server resolves the task directory internally as `<GLASSWORK_VAULT>/wiki/todo/`.
2. **App state file** — the path stored by the Glasswork desktop app in `%LocalAppData%\Glasswork\ui-state.json` (key `vault.path`). Opening the Glasswork app and selecting a vault populates this automatically.
3. **Error** — if neither source resolves to an existing directory, the process exits with a message naming both attempted sources.

### Setting the env var

```bash
# Unix / WSL — set to the Obsidian vault root (parent of wiki/todo/)
export GLASSWORK_VAULT=/path/to/your/vault-root

# PowerShell
$env:GLASSWORK_VAULT = "C:\path\to\your\vault-root"
```

---

## Configuring in Copilot CLI

Add `glasswork-mcp` to your Copilot CLI MCP configuration:

```json
{
  "mcpServers": {
    "glasswork": {
      "command": "glasswork-mcp",
      "env": {
        "GLASSWORK_VAULT": "/absolute/path/to/your/vault-root"
      }
    }
  }
}
```

If you have already opened the Glasswork app and configured the vault, you can omit the `env` block — the server will read the persisted path from the app state file.

---

## Configuring in Claude Desktop

Open Claude Desktop's settings (`claude_desktop_config.json`) and add:

```json
{
  "mcpServers": {
    "glasswork": {
      "command": "glasswork-mcp",
      "env": {
        "GLASSWORK_VAULT": "/absolute/path/to/your/vault-root"
      }
    }
  }
}
```

The `command` field must resolve to the `glasswork-mcp` binary on `PATH` (i.e., the .NET global tools directory, typically `~/.dotnet/tools` on Unix or `%USERPROFILE%\.dotnet\tools` on Windows, must be in `PATH`).

---

## Tool reference

| Tool | Status | Description |
|---|---|---|
| `add_task` | v0.2.0 | Create a new task file |
| `list_tasks` | v0.2.0 | List task summaries |
| `get_task` | v0.3.0 | Return full task content |
| `add_artifact` | v0.3.0 | Create a task artifact file |
| `load_context` | v0.4.0 | One-call full-context fetch: task + artifact bodies + recursive subtasks + backlinks |

### `add_task`

**Input**

```json
{
  "title": "string (required)",
  "description": "string (optional) — becomes the Description body section",
  "parent_task_id": "string (optional) — ID of the parent task",
  "status": "\"todo\" | \"doing\" | \"done\" (optional, defaults to todo)"
}
```

**Output**

```json
{
  "task_id": "string — the generated task ID (slug from title)",
  "path": "string — absolute path to the created task file"
}
```

### `list_tasks`

**Input**

```json
{
  "status": "\"todo\" | \"doing\" | \"done\" (optional)",
  "parent_task_id": "string (optional)"
}
```

**Output**

```json
{
  "tasks": [
    {
      "id": "string",
      "title": "string",
      "status": "\"todo\" | \"doing\" | \"done\"",
      "parent_id": "string | null",
      "path": "string — absolute path to the task file"
    }
  ]
}
```

Results are sorted by created date ascending, then by ID for stability.

---

### `get_task`

**Input**

```json
{
  "task_id": "string (required) — task ID to look up"
}
```

**Output (success)**

```json
{
  "id": "string",
  "title": "string",
  "status": "\"todo\" | \"doing\" | \"done\"",
  "parent_id": "string | null",
  "description": "string — full Description body (ADR 0002)",
  "notes": "string — full Notes body (ADR 0002)",
  "artifacts": [
    {
      "filename": "string — e.g. plan.md",
      "path": "string — vault-relative path, e.g. task-id.artifacts/plan.md"
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

Re-reads the vault and artifact folder on every call (no cache). The `artifacts` array lists filenames and vault-relative paths but does not include artifact body content.

---

### `add_artifact`

**Input**

```json
{
  "task_id": "string (required) — owning task ID",
  "filename": "string (required) — must end in .md, no path separators",
  "content": "string (required) — full markdown content"
}
```

**Output (success)**

```json
{
  "path": "string — vault-relative path to the created file, e.g. task-id.artifacts/plan.md"
}
```

**Output (errors)**

| `error` value | When |
|---|---|
| `not_found` | The task ID does not exist in the vault |
| `invalid_filename` | `filename` does not end in `.md` |
| `path_traversal` | `filename` contains `..`, is absolute, or resolves outside the artifact folder |
| `conflict` | A file with that name already exists — `add_artifact` is create-only in v1 |

Artifacts are stored under `<vault>/<task-id>.artifacts/<filename>`. The write is registered with `SelfWriteCoordinator` so the running Glasswork app does not raise a spurious "external change" banner.

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
    "description": "string",
    "notes": "string"
  },
  "artifacts": [
    {
      "filename": "string — e.g. plan.md",
      "path": "string — vault-relative, e.g. task-id.artifacts/plan.md",
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
      "source_path": "string — vault-relative path to the linking page",
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

## Profiling and structured logging

Every MCP tool call emits one structured JSON line (JSONL) to **stderr**. An optional file sink and per-phase trace are available via environment variables.

### Environment variables

| Variable | Value | Effect |
|---|---|---|
| `GLASSWORK_MCP_LOG` | `1` | Also write each log line to `<vault>/.glasswork/mcp.log`. The file is capped at ~1 MB; when the cap is exceeded the oldest half of entries is automatically pruned. |
| `GLASSWORK_MCP_TRACE` | `1` | Adds a `phases` object to each log line with per-phase wall-clock times (glob, yaml_parse, filter, sort, write). Off by default — zero overhead in normal use. |

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
{"ts":"2024-06-01T12:34:56.789Z","tool":"list_tasks","duration_ms":47,"result":"ok","task_count":3,"phases":{"glob":12,"yaml_parse":31,"filter":1,"sort":3}}
```

Phases instrumented in v1:

| Phase | Tools | Description |
|---|---|---|
| `glob` | `list_tasks` | Directory scan for `*.md` files |
| `yaml_parse` | `list_tasks` | Reading and parsing each file's YAML frontmatter |
| `filter` | `list_tasks` | Applying status / parent_task_id filters |
| `sort` | `list_tasks` | Sorting results by created date and ID |
| `write` | `add_task` | Writing the new task file to disk |

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

## Architecture notes

- **Stdio transport only** — no network listener, no authentication.
- **Vault is the only writable surface** — the server cannot read or write files outside the vault root.
- **Path-traversal guard** — every path-like tool input is validated by `VaultPathGuard.EnsurePathInVault` before any file-system operation.
- **Stateless reads** — no in-process cache; every read call re-reads from disk.
- **Version**: follows semver, stays in `0.x` until the tool surface is stable.
