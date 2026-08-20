# ADR 0023: App and MCP use independent GitHub Release streams

**Status**: Accepted
**Amended**: ADR 0024 replaces in-place global-tool updates with side-by-side
installation and an atomic Copilot command pointer.

## Context

ADR 0022 selected NuGet.org so MCP-only releases would not replace the
repository's latest app-visible GitHub Release. That avoided corrupting App
Update, but introduced an external publisher account and split update ownership
between NuGet and Glasswork. The desktop app already owns verified update
orchestration and can distinguish release streams by tag.

## Decision

The Glasswork repository publishes two independent stable GitHub Release
streams:

- app releases use `vX.Y.Z` and carry `Glasswork-win-x64.zip` plus SHA-256;
- MCP releases use `mcp-vX.Y.Z` and carry
  `glasswork-mcp.X.Y.Z.nupkg` plus SHA-256.

Release detection enumerates GitHub Releases and selects the highest stable tag
for the requested stream. The GitHub API is preferred; when anonymous API
limits reject the request, detection falls back to GitHub's complete public
smart-Git tag advertisement and accepts only an exact stable-shaped tag whose
expected immutable Release asset is downloadable. App Update ignores every
`mcp-v*` release, and MCP Update ignores every app `v*` release. Drafts,
malformed tags, and the other stream never participate. The fallback relies on
the repository rule that these exact tag namespaces are created only by the
non-prerelease publication workflows. `/releases/latest` is no longer a release
selection interface.

The manually dispatched MCP publication workflow still publishes only current
reviewed `main`, validates the committed `0.x` version and dated changelog,
runs serial MCP tests plus Release build/pack gates, verifies package source
revision and checksum, and fails if that version is already published. The
workflow stages a resumable draft, verifies the uploaded package/checksum
assets, creates an annotated `mcp-vX.Y.Z` tag whose message independently
anchors the source revision and SHA-256, then publishes the immutable GitHub
Release. A rerun may resume the draft; once the integrity tag exists, the
workflow reuses and verifies the already-anchored assets rather than rebuilding
their non-reproducible archive bytes. Agents never create tags or releases
directly.

Glasswork checks the installed global MCP tool through
`glasswork-mcp --version`. Missing or legacy builds are eligible for the newest
MCP release. The app bundles the exact-version installer, which downloads the
MCP Release assets, verifies the checksum and tag source revision, installs to
an isolated staging path, executes the staged build identity, then activates the
side-by-side version through the Copilot MCP command pointer. MCP-only updates
do not restart Glasswork or existing agent sessions. See ADR 0024.

Semantic version rules from ADR 0022 remain:

- remain in `0.x`;
- additive or breaking public MCP tool/CLI shape changes bump minor;
- compatible implementation or packaging fixes bump patch;
- published versions, annotated integrity tags, and assets are immutable.

## Consequences

- App and MCP versions and cadences remain independent in one repository.
- No NuGet account, API key, trusted-publisher policy, or package feed is
  required.
- The app Release package must include the MCP updater scripts.
- App Update and MCP Update share GitHub transport but retain separate status,
  apply behavior, and tag namespaces.
