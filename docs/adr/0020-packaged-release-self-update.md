# ADR 0020: Self-update installs a verified GitHub Release package

**Status**: Accepted
**Context slice**: App Update apply path; supersedes ADR 0011's source-build mechanism

## Context

ADR 0011 made Self-update rebuild Glasswork from a stamped local source
repository. When that repository was missing, stale, dirty, or otherwise failed
preflight, "Restart to update" opened the GitHub Release page and relaunched the
unchanged app. That fallback was deliberate, but it did not satisfy the action's
user-facing promise and coupled an installed app to developer tooling.

## Decision

Every Release publication produces two immutable assets:

- `Glasswork-win-x64.zip`, a self-contained Windows publish;
- `Glasswork-win-x64.zip.sha256`, its SHA-256 sidecar.

The Updater is bundled in the installed app and copied to a unique temporary
directory before launch so it never runs from the install directory it replaces.
"Restart to update" starts it detached, closes Glasswork, and passes the
Available Version plus the current executable path. The Updater:

1. opens the matching GitHub Release page so Release notes remain visible;
2. downloads the versioned package and checksum over HTTPS;
3. rejects malformed or mismatched checksums before changing the install;
4. extracts and validates that the package contains `Glasswork.exe`;
5. extracts to a staging directory beside the install, moves the current install
   to a sibling backup, moves the staging directory into place, and relaunches;
6. restores and relaunches the prior install if the swap fails.

The Updater uses a per-session OS mutex and a unique temporary work directory.
Mutex ownership ends automatically if the updater process dies. UI State and
the Vault live outside the install directory and are never part of the swap.

## Consequences

- Self-update no longer requires a source repository, `git`, the .NET SDK, or a
  clean worktree.
- Release publication now owns a binary payload as well as the tag and notes.
- The release archive is trusted only after its separately published SHA-256
  value matches. This detects corruption but is not code signing; Authenticode
  signing remains a possible future hardening step.
- The old Repo Path value may remain in existing UI State but is ignored.
- Existing versions that predate this ADR need one source-based or manual update
  before they gain the packaged Updater.
