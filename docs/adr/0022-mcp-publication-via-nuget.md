# ADR 0022: Publish `glasswork-mcp` immutably through NuGet.org

**Status**: Accepted

## Context

ADR 0007's local `dotnet pack` plus `dotnet tool update --add-source` flow
allowed different binaries to retain one package version. NuGet's global cache
then had no reason to replace the installed bits, so `glasswork-mcp 0.10.0`
could describe either current `main` or an older build. The tool needs an
independent durable release cadence, but Glasswork App Update treats this
repository's latest GitHub Release as the app's Available version. Publishing
MCP-only GitHub Releases would corrupt that signal.

## Decision

**MCP publication** uses NuGet.org as the immutable public package channel.
NuGet.org supports anonymous exact-version installs and permits unlisting but
not owner deletion of published versions. GitHub Packages was rejected because
even public package reads require a classic personal access token.

A dedicated, manually dispatched **MCP publication workflow** publishes only
current reviewed `main`. Its input is a stable `0.x` version already committed
in `Glasswork.Mcp.csproj`; the matching top changelog entry must be dated rather
than `Unreleased`. The workflow verifies current `origin/main`, package and tag
state, serial MCP tests, Release build, a clean pack output, package version,
repository source revision, and SHA-256 before publication. It uses NuGet.org
trusted publishing (GitHub OIDC to a short-lived API key), not a long-lived
repository secret.

The immutable package is published before an annotated `mcp-vX.Y.Z` tag is
created. The tag targets the package's source commit and records its version and
published `.nupkg` SHA-256. If package publication succeeds but tag creation
does not, rerunning the workflow may recover only the missing tag after
downloading the existing package, reading its embedded source revision, and
confirming that commit remains in `main` history. Recovery tags that exact
published commit even when `main` has advanced. Any other partial or duplicate
state fails closed. The workflow never creates a GitHub Release, so App Update
continues to see only `vX.Y.Z` app releases.

Every changed MCP binary receives a new semantic version:

- remain in `0.x`;
- additive or breaking public MCP tool/CLI shape changes bump the minor version;
- compatible implementation fixes, performance changes, and packaging-only
  corrections bump the patch version;
- a published version is never rebuilt, replaced, or reused.

The installer selects an exact version, downloads the NuGet package, verifies
the checksum and source revision recorded by its MCP tag, installs the package
to an isolated staging path, and executes `glasswork-mcp --version`. Only a
staged `X.Y.Z+<source-commit>` identity may replace the target installation.
This detects stale same-version local builds while ensuring replacement bits
are known-good before the working tool is removed.

## Consequences

- NuGet.org must be configured once with a trusted-publishing policy for
  `publish-mcp.yml`, and the repository variable `NUGET_USER` must name that
  publisher. Until then, publication is intentionally blocked after all local
  gates.
- `mcp-vX.Y.Z` Git tags are MCP integrity metadata, not GitHub Releases and not
  App Update signals.
- Future routine MCP Release PRs are narrow: version metadata and the dated MCP
  changelog entry. Agents publish only after that PR lands on `main`.
