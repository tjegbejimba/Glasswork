# Release automation setup and operation

Glasswork evaluates App and MCP releases independently every weekday at 09:00
America/Los_Angeles. The evaluator creates narrow Release PRs, enables
auto-merge through normal protection, and dispatches publication at the exact
merge commit. See ADR 0025.

## One-time GitHub App setup

Create and install a repository-scoped GitHub App with no protection bypass and
these repository permissions:

| Permission | Access | Purpose |
| --- | --- | --- |
| Actions | Read and write | Dispatch pinned App and MCP publication workflows |
| Contents | Read and write | Create and update automation branches and commits |
| Issues | Read and write | Deduplicate, update, and close blocker issues |
| Pull requests | Read and write | Create/update Release PRs and enable auto-merge |
| Metadata | Read | Required GitHub App baseline |

Add repository Actions secrets:

- `RELEASE_AUTOMATION_APP_ID`: numeric App ID.
- `RELEASE_AUTOMATION_PRIVATE_KEY`: the App private key in PEM form.

The normal workflow token is not used for repository mutations. It is used
separately by `actions/ai-inference@v1` with `copilot-requests: write`.

## Labels

Create these repository labels exactly:

| Label | Meaning |
| --- | --- |
| `release:app` | Force/include App release impact |
| `release:mcp` | Force/include MCP release impact |
| `release:both` | Force/include both release streams |
| `release:none` | Explicitly no forced release |
| `semver:major` | Breaking App bump; MCP maps this to minor |
| `semver:minor` | Minor bump |
| `semver:patch` | Patch bump |
| `release-automation` | Automation-owned Release PR |
| `release-automation-blocker` | Deduplicated per-stream blocker issue |

Apply release and SemVer labels to ordinary change PRs. Multiple release labels
or multiple SemVer labels on one PR are invalid and block only the affected
stream. Existing `bug`, `feature`, `enhancement`, and `tech-debt` labels group
notes; they do not select versions.

## Variables and rollout

Create these repository Actions variables:

| Variable | Initial value | Purpose |
| --- | --- | --- |
| `RELEASE_AUTOMATION_ENABLED` | `false` | Global kill switch |
| `RELEASE_AUTOMATION_APP_ENABLED` | `true` | App stream kill switch |
| `RELEASE_AUTOMATION_MCP_ENABLED` | `true` | MCP stream kill switch |

The missing/`false` global variable keeps initial setup safe. After the App is
installed, labels exist, secrets resolve, repository auto-merge is enabled, and
the required up-to-date `ci` check plus conversation resolution remain active,
set `RELEASE_AUTOMATION_ENABLED=true`. This immediately enables full autonomy;
there is no shadow period.

## Manual evaluation

Run **Evaluate releases** with:

- `dry_run=true` to build plans and summaries without GitHub mutations.
- `force_evaluate=true` to bypass schedule, quiet-period, and normal path
  inclusion gates for a non-empty range. It never bypasses net-zero, label
  conflicts, branch protection, PR allowlists, or publication integrity.

Every stream uploads a `release-plan-<stream>` JSON artifact retained for 90
days. Routine success, deferral, and no-op states appear only in job summaries.
Failures update one issue titled `[Release automation][App] Blocked` or
`[Release automation][Mcp] Blocked`. Its hidden stage marker prevents a
successful evaluation from closing an unresolved publication failure, and vice
versa.

## Recovery

- Disable the global or affected stream variable to stop new mutations.
- Fix the blocker recorded in the stream issue, then rerun the evaluator.
- Do not edit automation branches, generated commits, or Release PR bodies by
  hand. Signed tree provenance appears in both the generated commit and the
  controlled PR body; unsigned or conflicting state fails closed.
- Publication workflows are resumable. Rerun the failed workflow with the same
  `version` and `source_ref`; never delete or move an integrity tag.
- If a stable release already exists, corrected binaries require the next patch
  version.
