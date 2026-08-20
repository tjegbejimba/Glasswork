# ADR 0024: MCP updates switch side-by-side installations

**Status**: Accepted

## Context

The .NET global-tool updater removes the installed version before installing
its replacement. On Windows, every running `glasswork-mcp` process locks files
under the global `.store` version directory, so one long-lived Copilot session
can block the update. Requiring all agent sessions to close does not fit a tool
designed to serve many concurrent sessions.

## Decision

Glasswork installs each verified MCP build into an immutable side-by-side
directory under `%LocalAppData%\Glasswork\Mcp\versions\<build-identity>\`.
The build identity remains `X.Y.Z+<source-commit>`. Installation stages and
executes the new package before it becomes current.

The Copilot user MCP configuration is the atomic **MCP command pointer**.
After the new version is verified, the installer rewrites only
`mcpServers.glasswork.command` in `~\.copilot\mcp-config.json` through a
same-directory temporary file and atomic replace. Every other MCP server,
Glasswork environment value, tool filter, and setting is preserved. New
sessions read the new executable path; already-running sessions continue using
the old loaded process without interruption.

The installer records the selected version in
`%LocalAppData%\Glasswork\Mcp\current.json` as deployment metadata. App Update
uses the same Copilot command pointer as new sessions for installed-version
detection. Old version directories are not eagerly deleted: a later
garbage-collection pass may remove them only after no process uses them. The
legacy global-tool install may remain on disk after migration but is no longer
the command for new Copilot sessions.

Explicit `-ToolPath` installation remains an in-place test/development seam;
the normal app and script path always use side-by-side deployment.

## Consequences

- MCP updates no longer require active agent sessions to stop.
- A session keeps one MCP build for its lifetime; new sessions converge on the
  current build.
- The first update performs the Copilot configuration migration automatically.
- Other MCP clients with separate configuration files must opt into the
  side-by-side executable path independently.
