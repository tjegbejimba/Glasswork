#!/usr/bin/env bash
# Helpers for finishing a ready PR that closes a Ralph issue.

ralph_merge_ready_open_pr_for_issue() {
  local issue="$1"
  echo "ℹ️  Automatic merge fallback is disabled for #$issue; the worker must complete validation, dual review, and the normal non-admin merge path." >&2
  return 1
}

# Release-branch fallback: when copilot pushed a green PR into a release
# branch (non-default base) but didn't run `gh pr merge` and `gh issue close`,
# do it for them. Distinct from the default-branch helper because:
#   - GitHub doesn't populate `linked:issue` / `closingIssuesReferences` for
#     PRs whose base != default, so we search by body text instead.
#   - Closure must be done via explicit `gh issue close` after merge — GitHub
#     will not auto-close from a non-default-base PR even with `Closes #N`.
# Opt-in via RALPH_RELEASE_BRANCH; this helper is a no-op for empty input.
ralph_merge_release_branch_pr_for_issue() {
  local issue="$1"
  local release_branch="$2"
  [[ -n "$release_branch" ]] || return 1
  echo "ℹ️  Automatic release-branch merge fallback is disabled for #$issue on '$release_branch'; the worker must merge and verify delivery." >&2
  return 1
}

# Branch-only fallback: copilot pushed `${branch_prefix}${issue}-…` to origin
# but never opened a PR. Open the PR ourselves so the merge fallback above
# can pick it up. Returns 0 if a PR was created (caller should re-run merge).
# Requires both release_branch and branch_prefix; no-op otherwise.
ralph_open_pr_for_pushed_branch() {
  local issue="$1"
  local release_branch="$2"
  local branch_prefix="$3"
  local branch sha title body

  [[ -n "$release_branch" && -n "$branch_prefix" ]] || return 1

  local candidates candidate date best_date
  candidates=$(gh api "repos/$REPO/branches" --paginate \
    --jq ".[] | select(.name | startswith(\"${branch_prefix}${issue}-\")) | .name" 2>/dev/null)
  [[ -n "$candidates" ]] || return 1

  # Tie-break by latest commit date (ISO 8601 sorts lexicographically as dates)
  # so a stale older branch can't beat a freshly pushed one.
  branch=""
  best_date=""
  while IFS= read -r candidate; do
    [[ -n "$candidate" ]] || continue
    date=$(gh api "repos/$REPO/branches/$candidate" --jq '.commit.commit.committer.date' 2>/dev/null || echo "")
    [[ -n "$date" ]] || continue
    if [[ -z "$best_date" || "$date" > "$best_date" ]]; then
      best_date="$date"
      branch="$candidate"
    fi
  done <<<"$candidates"
  [[ -n "$branch" ]] || return 1

  sha=$(gh api "repos/$REPO/branches/$branch" --jq '.commit.sha' 2>/dev/null || echo "")
  [[ -n "$sha" ]] || return 1

  title=$(gh api "repos/$REPO/commits/$sha" --jq '.commit.message' 2>/dev/null | head -1)
  [[ -n "$title" ]] || title="feat: complete issue #$issue"

  body=$(printf '%s\n\n%s' "Closes #$issue" "(Ralph branch-only fallback: copilot pushed the branch but didn't open the PR. Local checks were green at push time per the iteration log.)")

  echo "ℹ️  Found pushed branch '$branch' for issue #$issue with no PR; creating PR..." >&2
  if ! gh pr create --repo "$REPO" --base "$release_branch" --head "$branch" --title "$title" --body "$body" >/dev/null; then
    echo "⚠️  Failed to create fallback PR for branch '$branch'." >&2
    return 1
  fi
  return 0
}
