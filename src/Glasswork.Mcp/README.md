# glasswork-mcp

`glasswork-mcp` is a standalone [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that gives AI agents typed read/write access to a [Glasswork](https://github.com/tjegbejimba/Glasswork) task vault. It communicates over stdio and requires no running Glasswork app instance.

> **v0.2.0 — M2**: `add_task` and `list_tasks` are implemented. See [Tool reference](#tool-reference) for schemas.

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

1. **`GLASSWORK_VAULT` environment variable** — if set and points to an existing directory, that path is used.
2. **App state file** — the path stored by the Glasswork desktop app in `%LocalAppData%\Glasswork\ui-state.json` (key `vault.path`). Opening the Glasswork app and selecting a vault populates this automatically.
3. **Error** — if neither source resolves to an existing directory, the process exits with a message naming both attempted sources.

### Setting the env var

```bash
# Unix / WSL
export GLASSWORK_VAULT=/path/to/your/vault

# PowerShell
$env:GLASSWORK_VAULT = "C:\path\to\your\vault"
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
        "GLASSWORK_VAULT": "/absolute/path/to/your/vault"
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
        "GLASSWORK_VAULT": "/absolute/path/to/your/vault"
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
| `get_task` | M3 | Return full task content |
| `add_artifact` | M3 | Create a task artifact file |

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

## Architecture notes

- **Stdio transport only** — no network listener, no authentication.
- **Vault is the only writable surface** — the server cannot read or write files outside the vault root.
- **Path-traversal guard** — every path-like tool input is validated by `VaultPathGuard.EnsurePathInVault` before any file-system operation.
- **Stateless reads** — no in-process cache; every read call re-reads from disk.
- **Version**: follows semver, stays in `0.x` until the tool surface is stable.
