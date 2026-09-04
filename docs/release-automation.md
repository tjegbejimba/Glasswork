# Release automation setup and operation

Glasswork evaluates App and MCP releases independently every weekday at 09:00
America/Los_Angeles. The evaluator creates narrow Release PRs, enables
auto-merge through normal protection, and dispatches publication at the exact
merge commit. See ADR 0025.

GitHub may start scheduled workflows hours after their cron slot. The evaluator
therefore selects the active daylight/standard-time cron from
`github.event.schedule`; it does not require the runner to start during the
Pacific 09:00 hour.

## One-time GitHub App setup

Create and install a repository-scoped GitHub App with no protection bypass and
these repository permissions:

| Permission | Access | Purpose |
| --- | --- | --- |
| Actions | Read and write | Dispatch pinned App and MCP publication workflows |
| Contents | Read and write | Create/update automation branches and commits; fetch and push immutable publication tags |
| Issues | Read and write | Deduplicate, update, and close blocker issues |
| Pull requests | Read and write | Create/update Release PRs and enable auto-merge |
| Metadata | Read | Required GitHub App baseline |

Add repository Actions secrets:

- `RELEASE_AUTOMATION_APP_ID`: numeric App ID.
- `RELEASE_AUTOMATION_PRIVATE_KEY`: the App private key in PEM form.

For organization-owned repositories, confirm the Copilot CLI policy **Allow use
of Copilot CLI billed to the organization** is enabled. The evaluator installs
the pinned `@github/copilot` CLI and invokes the SHA-pinned
`actions/ai-inference` v3 action with the built-in `GITHUB_TOKEN` and only
`copilot-requests: write`. It grants Copilot no tools. Installation, inference,
authentication, model, or output-validation failures fall back to deterministic
categorized notes and never block a release.

Token responsibilities are deliberately split:

- The GitHub App installation token creates and updates automation branches,
  commits, PRs, auto-merge state, blocker issues, publication dispatches, and
  immutable publication tag fetches/pushes.
- The built-in `GITHUB_TOKEN` authenticates Copilot inference in the evaluator.
  In `release.yml` and `publish-mcp.yml`, it also creates, updates, uploads,
  downloads, verifies, and publishes GitHub Releases. Those workflows reserve
  the App token for authenticated git fetch/tag operations and blocker issues.

Do not rotate the App private key while an automation Release PR is open. Its
signed provenance marker is verified with that key after merge; rotate only
after open automation Release PRs have merged or been closed and reconciled.

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
`allow_squash_merge=true`, and the required up-to-date `ci` check plus
conversation resolution remain active, set
`RELEASE_AUTOMATION_ENABLED=true`. Squash merge is required because the
evaluator enables squash auto-merge for its Release PRs. This immediately
enables full autonomy; there is no shadow period.

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

Evaluator plan, prompt, and failure scratch files live under the runner's
temporary directory, outside the checkout. This keeps generated workflow state
out of the Release PR changed-file allowlist.

## Publication timing

Automation invokes the App or MCP publication `workflow_dispatch` only after
the matching labeled automation Release PR merges. The
`release-publication.yml` merge listener validates the exact merged PR, signed
head, changed-file allowlist, and kill switches, then dispatches `release.yml`
or `publish-mcp.yml` with that exact merge SHA. The publication workflows retain
manual `workflow_dispatch` entry points only for deliberate recovery; the
automated path never dispatches publication before merge.

## Recovery

- Disable the global or affected stream variable to stop new mutations.
- Fix the blocker recorded in the stream issue, then rerun the evaluator.
- Do not edit automation branches, generated commits, or Release PR bodies by
  hand. Signed tree provenance appears in both the generated commit and the
  controlled PR body; unsigned or conflicting state fails closed.
- Publication workflows are resumable. Rerun the failed workflow with the same
  `version` and `source_ref`; never delete or move an integrity tag.
- Before rotating the App private key, merge or close every open automation
  Release PR. Rerun the evaluator afterward so any required PR is signed with
  the new key.
- If a stable release already exists, corrected binaries require the next patch
  version.
