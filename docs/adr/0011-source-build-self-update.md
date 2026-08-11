# ADR 0011: The installed app self-updates by building from the local source repo

**Status**: Superseded by ADR 0020
**Context slice**: Restart-to-update feature; introduces the App Update context

## Context

Glasswork ships **unpackaged and self-contained**. The only install path is
`scripts\publish.ps1`, which runs `dotnet publish -c Release --self-contained
-r win-x64` from the local source repo into `%LOCALAPPDATA%\Programs\Glasswork`,
then creates shortcuts and installs Copilot skills. There is no MSIX, no
installer bundle, no auto-update channel.

GitHub Releases exist for the repo (`v1.2.0`, `v1.3.0`) but carry **no binary
assets** — they are tag + release-notes only. So there is nothing to download
and swap: the bits that become a new version are produced by building the
source, not by fetching a pre-built artifact.

The user asked for a "Restart to update" button like other apps have: check for
a newer version, apply it, and relaunch. The hard constraint is that the
*installed* app and the *source repo* are two separate locations on disk, and a
running `.exe` cannot overwrite itself.

## Decision

The **apply** mechanism is a **source build**: when the user clicks "Restart to
update," Glasswork runs `git pull` then `scripts\publish.ps1` against the local
source repo and relaunches the freshly built executable. We do **not** download
a pre-built artifact, because none exists.

Four sub-decisions make this workable:

### 1. Detection is decoupled from apply

"Is there an update?" is answered by an unauthenticated HTTPS call to the public
GitHub API (`GET /repos/tjegbejimba/Glasswork/releases/latest`), reading
`tag_name`. The repo is public, so detection needs no `gh` CLI and no auth. The
SemVer comparison (`Available Version` vs `Installed Version`) lives in
`Glasswork.Core` as pure, testable logic. Only the heavier **apply** path
depends on local tooling (`git`, `pwsh`, `publish.ps1`).

This means the common path — just checking — stays maximally robust, and the
rare path — actually updating — is the only thing that needs the source repo.
ADR 0012 defines the separate **Release publication** process that creates the
GitHub Release tag consumed here; a normal PR merge is not itself an update
signal.

### 2. The install stamps its own Repo Path

The installed app has no inherent link back to the source repo. So
`publish.ps1` — the one component that authoritatively knows the repo root
(`$PSScriptRoot\..`) — writes that path into UI State
(`%LocalAppData%\Glasswork\`) at install time, under a `RepoPath` key. The
updater reads it. Because every install and every self-update re-runs
`publish.ps1`, the Repo Path is a self-maintaining invariant rather than a guess.

### 3. Apply runs in a detached updater script

`publish.ps1` kills running Glasswork instances before overwriting the binary,
and a real `dotnet publish` takes 1–2 minutes. The app therefore cannot run the
update while staying alive. Instead:

1. "Restart to update" spawns a **detached** `scripts\self-update.ps1`, passing
   the app's PID, the Repo Path, and the install-exe path. Detached so it
   survives the app exiting.
2. The app **closes itself**.
3. The updater **waits for that PID to exit**, then runs `git pull` →
   `publish.ps1` → relaunches the new `glasswork.exe`.

During the closed window the updater shows a minimal **"Updating Glasswork…"
progress window**, so the user is never staring at nothing — mirroring the
updater splash that apps like VS Code show.

### 4. Every failure degrades to "open the release page"

Detection failures are silent on startup and surfaced only on an explicit
"Check for updates." Apply failures never leave the user without a working app:
if the Repo Path is missing/stale, or `git`/`pwsh` is absent, the button falls
back to opening the GitHub release page; if `git pull` or `publish.ps1` fails,
the updater reports it and relaunches the **existing** version unchanged. This
mirrors the classified-failure model already used by `GhCliIssueFiler`.

## Alternatives considered

### A. Notify + open release page only

- ✅ Trivial, robust, no local tooling dependency.
- ❌ Not a "restart to update" button — the user still updates by hand.
- **Rejected** as the primary mechanism, but **adopted as the universal
  fallback** for every apply failure.

### B. Download-and-swap a pre-built artifact (like big apps)

- ✅ Closest to the familiar consumer-app update feel; no source repo needed on
  the user's machine.
- ❌ Requires standing up **new release infrastructure** that does not exist:
  CI to build and attach a zipped self-contained build to every release,
  plus extraction-over-the-running-install-dir hazards and asset integrity
  concerns.
- ❌ Large build for a single-user developer tool whose source repo is already
  on disk.
- **Rejected for now.** This is the "right" answer for a broadly distributed
  app and is the natural future migration if Glasswork ever ships to machines
  without the source repo.

### C. Auto-detect the repo path from the running exe location

- ✅ No stamping step.
- ❌ Only works for the dev build running out of `repo\src\Glasswork.App\bin`;
  the real install at `%LOCALAPPDATA%\Programs\Glasswork` has no such
  relationship.
- **Rejected** as the primary resolver; the stamped Repo Path (decision 2) is
  authoritative, with graceful fallback when it is absent.

### D. Reuse `gh release view` for detection

- ✅ Consistent with `GhCliIssueFiler`.
- ❌ Drags the whole `gh`-must-be-installed-and-authenticated dependency into a
  check that, for a public repo, needs neither.
- **Rejected** — a plain HTTPS call is more robust for the common path.

## Consequences

### Good

- Reuses the one true install path (`publish.ps1`); there is no second
  packaging format to build or maintain.
- Detection works even without `gh`, auth, or the source repo present.
- The Repo Path link is a maintained invariant, refreshed on every install and
  update.
- Every failure mode has a defined, non-destructive outcome.
- Version-comparison logic is pure and lives in Core, so it is unit-testable on
  Linux/CI.

### Bad / accepted trade-offs

- The installed app is **coupled to the presence of the source repo on disk**.
  On a machine without the repo (or after the repo is moved/deleted), self-update
  cannot run and the button falls back to opening the release page. This is
  acceptable for a single-user developer install and is the main thing that
  alternative B would later remove.
- There is a **1–2 minute window where the app is closed** during the build.
  The progress window covers it, but the update is not instant.
- The apply path spawns processes and runs a build — heavier and more
  environment-dependent than a file copy. The detection/compare path is kept
  deliberately lightweight to compensate.
- Any code that writes the vault must register with `SelfWriteCoordinator`;
  the updater writes only outside the vault (UI State + install dir) so it does
  not interact with the watcher.

### Reversible?

The detection half is fully reversible — it is a self-contained client plus a
pure comparison. The apply half commits to the source-build model; switching to
download-and-swap (alternative B) later would replace `self-update.ps1` and add
CI, but would not change the detection surface or the UI. The UI affordances
(My Day InfoBar, Settings section, nav dot) are independent of which apply
mechanism is chosen.

## Why this ADR exists

The skill rule for ADRs: hard to reverse + surprising without context + real
trade-off. This decision qualifies on all three:

- **Hard to reverse**: the apply mechanism is an architectural fork; moving to
  download-and-swap later means new CI and a new updater.
- **Surprising without context**: a future reader will be genuinely puzzled that
  the *installed* app reaches back to a *source repo* and runs `dotnet publish`
  to update itself. This records why.
- **Real trade-off**: source-build was chosen over notify-only and
  download-and-swap, accepting repo-coupling in exchange for reusing the one
  install path that already exists.
