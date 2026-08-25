# Glasswork release notes

Release notes are committed by a Release PR before Release publication. The
Release evaluator normally generates categorized notes from merged PRs in the
exact stream range. Each published version uses `docs\releases\vX.Y.Z.md`; the
Release workflow validates that the file exists and uses it as the GitHub
Release body.

Use this template:

```md
# Glasswork vX.Y.Z

One short summary paragraph.

## Changes

### Breaking

- Grouped user-facing changes with PR links and author credit.

### Features

- Grouped user-facing changes with PR links and author credit.

### Fixes

- Grouped user-facing changes with PR links and author credit.

### Maintenance

- Grouped user-facing changes with PR links and author credit.

## Validation

- Release workflow gates run.
```
