# Issue tracker: GitHub

Engineering issues and specs for Glasswork live in
[GitHub Issues](https://github.com/tjegbejimba/Glasswork/issues). Use the `gh`
CLI for repository issue operations.

Azure DevOps work items are upstream product data that Glasswork imports or
links to as Tasks. They are not the engineering issue tracker for this
repository.

## Conventions

- Use GitHub Issues for bugs, features, technical debt, PRDs, and implementation
  slices.
- Parent specifications use a `PRD:` title and the repository's `prd` or
  `work:prd` label. Vertical implementation issues use `work:slice`; standalone
  implementation issues use `work:standalone`.
- Readiness is explicit: `needs-triage`, `needs-info`, `ready-for-agent`,
  `ready-for-human`, or `wontfix`.
- Issues filed by the in-app feedback dialog carry `user-report`. Triage those
  reports without modifying repository files or opening a pull request unless
  the issue is a clearly safe one-line fix with no ADR-level decision.
- Ralph execution labels (`ralph:*`) describe worker state and are separate from
  issue type and readiness labels.
- Use Glasswork's canonical domain terms in titles and bodies. In particular,
  use **Task** for a Glasswork Task and reserve **work item** for Azure DevOps.

## Common operations

Run these commands from the repository checkout:

- **Create**:
  `gh issue create --repo tjegbejimba/Glasswork --title "..." --body-file <path>`
- **Read**:
  `gh issue view <number> --repo tjegbejimba/Glasswork --comments`
- **List**:
  `gh issue list --repo tjegbejimba/Glasswork --state open --json number,title,body,labels,comments`
- **Comment**:
  `gh issue comment <number> --repo tjegbejimba/Glasswork --body-file <path>`
- **Apply or remove labels**:
  `gh issue edit <number> --repo tjegbejimba/Glasswork --add-label "..."` or
  `--remove-label "..."`
- **Close**:
  `gh issue close <number> --repo tjegbejimba/Glasswork --comment "..."`

The `origin` remote is the authoritative repository. In automation, pass
`--repo tjegbejimba/Glasswork` explicitly so commands remain deterministic.

## Pull requests as a triage surface

**PRs as a request surface: no.**

Feature requests and work requests enter through GitHub Issues. Pull requests
implement accepted work; they do not join the issue-triage queue.

GitHub shares one number space across issues and pull requests. If a bare
`#<number>` is ambiguous, try `gh pr view <number>` and then
`gh issue view <number>`.

## Skill terminology

When an engineering skill says:

- **publish to the issue tracker**: create a GitHub issue in
  `tjegbejimba/Glasswork`.
- **fetch the relevant ticket**: read the corresponding GitHub issue, including
  comments and labels.
- **map**: use a GitHub issue labelled `wayfinder:map`.
- **child ticket**: use a GitHub sub-issue with the appropriate
  `wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling`, or
  `wayfinder:task` label.
- **blocking**: prefer GitHub's native issue dependencies. If unavailable, add a
  `Blocked by: #<number>` line to the child issue body.
