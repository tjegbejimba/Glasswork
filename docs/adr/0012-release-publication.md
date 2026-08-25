# ADR 0012: Release publication is explicit, agent-prepared, and tag-immutable

**Status**: Accepted
**Amended**: ADR 0023 replaces `/releases/latest` selection with explicit
`vX.Y.Z` app-stream filtering so MCP GitHub Releases cannot become App updates.
ADR 0025 automates evaluation, Release PR reconciliation, and exact-SHA
publication while preserving the review and immutability boundaries below.
**Context slice**: Release publication for App Update

## Context

ADR 0011 originally defined how the installed app detects and applies updates;
ADR 0020 supersedes its source-build apply mechanism with a packaged release.
An Update check still reads the latest GitHub Release `tag_name` and compares it
to the Installed version. That leaves a
separate repo-maintenance question: when does a merged change become the
GitHub Release signal that the app can see?

## Decision

Normal PR merges to `main` are not app-visible updates. A version becomes
available only through explicit **Release publication**:

1. An agent prepares a **Release PR** that bumps
   `src\Glasswork.App\Glasswork.csproj` (`Version`, `AssemblyVersion`,
   `FileVersion`, `InformationalVersion`) and commits **Release notes** at
   `docs\releases\vX.Y.Z.md`.
2. If the user asks an agent to "release a new version" without specifying a
   version, the agent chooses a patch bump by default. Minor or major bumps
   require explicit user instruction.
3. The Release PR may be auto-merged by the agent after required checks when
   the diff contains only the version bump and Release notes. If checks fail,
   the diff is broader, or there are no substantive changes since the latest
   published release tag, the agent stops.
4. After the Release PR lands on `main`, the agent runs the manually-dispatched
   **Release workflow** with one input: `version` in `X.Y.Z` form.
5. The workflow checks out current `main`, derives tag `vX.Y.Z`, validates the
   committed app version and `docs\releases\vX.Y.Z.md`, runs Core tests and a
   Windows Release x64 app publish, then creates the GitHub Release with those
   notes, `Glasswork-win-x64.zip`, and its SHA-256 sidecar.

Release tags are immutable. If tag `vX.Y.Z` or release `vX.Y.Z` already exists,
the workflow fails instead of moving the tag or rewriting the release. Corrected
bits require a new patch version; corrected wording can be edited manually on
the GitHub Release page.

## Release notes

Release notes summarize the range from the latest published release tag to
`main`, preferring merged PRs and linked issues and falling back to commit
messages for direct commits. The Release PR itself is excluded. The notes file
uses a concise template:

```md
# Glasswork vX.Y.Z

One short summary paragraph.

## Changes

- Grouped user-facing changes.

## Validation

- Release workflow gates run.
```

## Alternatives considered

### A. Every merge to `main` creates an app-visible update

- **Rejected.** It would make small internal slices noisy and would couple PR
  merge mechanics to the user's update experience. Release publication should
  be deliberate.

### B. GitHub Actions generates the version bump and notes

- **Rejected for now.** GitHub Actions should stay deterministic and auditable:
  validate inputs, run gates, and create the release. The agent is better suited
  to choosing a version, summarizing changes, and preparing reviewable markdown.

### C. Agent directly creates tags/releases

- **Rejected.** Direct publication skips the repo's standard checks surface.
  The Release PR plus Release workflow gives an audit trail and keeps tag
  creation behind one narrow automation path.

### D. Allow arbitrary `target_ref`

- **Rejected for v1.** Releasing only current `main` keeps the app-visible
  version tied to reviewed history and avoids publishing stale or unreviewed
  commits.

## Consequences

- App Update treats the highest stable `vX.Y.Z` GitHub Release as the app
  Available version and ignores the independent `mcp-vX.Y.Z` stream.
- Agents can run the release flow end-to-end without mandatory human prose
  input, while still leaving an auditable Release PR and committed Release notes.
- Release publication produces the deterministic Windows package consumed by
  Self-update, as defined by ADR 0020.
- A release is intentionally not created when there are no substantive changes,
  avoiding no-op updates that would rebuild to equivalent bits.
