# ADR 0025: Release automation evaluates and publishes independent streams

**Status**: Accepted
**Amends**: ADR 0012 and ADR 0023

## Context

App and MCP publication already use reviewable Release PRs and immutable GitHub
Releases, but choosing a release, preparing its metadata, and dispatching
publication are manual. The two components share a repository while retaining
different version histories and artifact contracts. Automation must therefore
derive durable state from GitHub, preserve branch protection, and fail closed
without coupling one stream's recovery to the other.

## Decision

A repository-owned **Release evaluator** runs at 09:00
`America/Los_Angeles` on weekdays. Two UTC cron entries cover standard and
daylight time; an in-workflow timezone guard rejects the inactive entry.
`workflow_dispatch` supports dry-run and forced evaluation.

The evaluator treats App and MCP as independent state machines:

- App compares the highest published stable `vX.Y.Z` GitHub Release with a
  green `main` candidate.
- MCP compares the highest published stable `mcp-vX.Y.Z` GitHub Release with
  the same candidate.
- The candidate must have been quiet for two hours.
- The net Git diff is authoritative. An empty range never releases, including
  a range whose changes were fully reverted.
- deterministic artifact-impact paths select a stream; tests, docs,
  workflows, release metadata, and developer tooling are excluded by default.
- `release:app`, `release:mcp`, and `release:both` may force a normally excluded
  non-empty change. `release:none` records an explicit no-release intent.
  Conflicting release or semantic-version labels fail closed.
- `semver:major`, `semver:minor`, and `semver:patch` select the maximum bump in
  a stream range. Missing metadata means patch. MCP stays in `0.x`, so a major
  marker advances its minor version.

The evaluator reconciles at most one automation-created Release PR per stream.
It regenerates that branch from the latest eligible `main`, signs the generated
tree provenance in both the commit and controlled PR body, rejects unsigned or
conflicting edits, enforces an exact changed-file allowlist, enables auto-merge,
and relies on the existing required `ci` check and conversation resolution.
App work is evaluated first; MCP evaluation still runs if App evaluation fails.

Release notes come from merged PRs in the exact tag-to-candidate range and are
grouped as breaking changes, features, fixes, and maintenance. A pinned Copilot
CLI plus SHA-pinned `actions/ai-inference` v3 may rewrite only those
human-facing notes through the built-in workflow token. Copilot receives no
tools. Untrusted PR text is normalized and delimited, model output is
schema-checked, and deterministic notes are used on any installation,
authentication, inference, model, or validation failure. Eligibility, stream
selection, and version selection never depend on model output.

Only after an automation Release PR merges, a separate merge listener validates
its signed state and invokes the matching publication workflow's
`workflow_dispatch` with that exact merge commit. Both publication workflows
verify the commit belongs to `main`, check it out detached, and serialize by
stream. Moving `main` after merge cannot change published bits.

App publication adopts MCP's resumable integrity model: a matching draft can be
resumed, a complete existing asset pair is downloaded and verified, partial or
mismatched state fails, an annotated immutable tag anchors source revision and
SHA-256, and publication never moves a tag. App automation is autonomous only
because this prerequisite is part of the same change.

Durable state remains in published immutable releases, signed labeled Release
PRs, workflow runs, and one deduplicated blocker issue per stream. Evaluation
and publication record their stage in that issue so only the recovering stage
closes it. Successful and no-op evaluations emit a 90-day machine-readable
Release plan artifact plus job summary.

A dedicated least-privilege GitHub App installation token owns branch, PR,
issue, auto-merge, workflow-dispatch, and authenticated git tag mutations. The
built-in workflow token is separate: the evaluator grants it
`copilot-requests: write` for Copilot inference, while the App and MCP
publication workflows grant it `contents: write` for GitHub Release
create/edit/upload/download/publish operations. Global and per-stream
repository variables are kill switches. No token bypasses branch protection.

## Consequences

- Stable App and MCP releases can proceed without a shadow period after
  repository setup and kill-switch enablement.
- A stream failure is visible and recoverable without blocking the other
  stream.
- Release PRs and publication remain auditable through normal GitHub controls.
- Repository setup must install the GitHub App, create labels, add secrets and
  variables, allow organization-billed Copilot CLI requests, and enable both
  auto-merge and squash merge before the global kill switch is enabled.
- Rotating the App private key while an automation Release PR is open
  invalidates that PR's signed provenance marker; open automation PRs must be
  merged or closed before rotation and reconciled afterward.
