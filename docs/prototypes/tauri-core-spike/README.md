# Tauri + bounded Rust Core — cross-platform prototype spike

Disposable, time-boxed spike for Wayfinder ticket
[Prototype Tauri with a bounded Rust Core](https://github.com/tjegbejimba/Glasswork/issues/372)
under map [Wayfinder: Choose the cross-platform Glasswork architecture](https://github.com/tjegbejimba/Glasswork/issues/369).

Implements exactly the shared vertical slice and fixed 3-task fixture locked
by resolved ticket
[Define the shared cross-platform vertical slice](https://github.com/tjegbejimba/Glasswork/issues/370)
(accepted Variant B), scored against the rubric locked by resolved ticket
[Fix the cross-platform prototype scorecard](https://github.com/tjegbejimba/Glasswork/issues/376)
(`docs/research/cross-platform-prototype-scorecard.md`). This spike does not
redefine the slice or the rubric — it only implements against them and
reports evidence.

**This is not production code.** It does not touch `Glasswork.App` or
`Glasswork.Core`, and is expected to be deleted once the framework decision
(ticket #381) is made. See "Not in scope" below.

## Status: evidence partially captured, ticket remains OPEN/claimed

Per the ticket's real-platform constraint, this spike was developed and
measured on **macOS only** in this session — no Windows runner/device was
available. TJ's hands-on visual/interaction ratings and a live VoiceOver
pass are also outstanding. **None** of these are simulated or invented here.
Do not close #372 or append the #369 decision pointer until all of the
following exist:

- [x] macOS: full slice built and gate-checked
- [x] macOS: measured evidence (8 metrics) captured, with caveats noted below
- [x] macOS: automated tests passing (27 total)
- [x] macOS: HITL evidence artifacts prepared (screenshots + recording) for
      TJ to review
- [ ] macOS: TJ's hands-on visual/interaction ratings (4 sub-criteria,
      scorecard Phase 1, rated alongside the other two spikes once they exist)
- [ ] macOS: live VoiceOver pass (Gate 4 + metric 6) — **automation proved
      unreliable for this ad-hoc-signed binary; this must be a human HITL
      session**, see "VoiceOver — required HITL step" below
- [ ] Windows: build, gate-check, measure, VoiceOver→Narrator equivalent pass
      — see "Windows reproduction package" below
- [ ] PWA-reuse module classification is provisional (see below) —
      confidence will improve once the PWA target itself is scoped, but the
      6-module classification recorded here is real, not a placeholder

## What was built

- **Bounded Rust Core** (`core/`) — a from-scratch Rust reimplementation of
  *only* the slice-required subset of `Glasswork.Core`'s behavior: task
  frontmatter parsing/serialization round-trip, vault folder loading,
  file-watch change detection, self-write suppression (so the app's own
  writes don't re-trigger its own watcher), Obsidian deep-link URI building
  with vault-escape rejection, and HTML/Markdown artifact-kind
  classification + sandboxed-HTML CSP policy. It is **not** a port of the
  whole product — no Planner, no backlinks, no UI state, no full task
  catalog beyond the fixed fixture. 20 passing tests (`cargo test --release`,
  see `evidence/automated-ui-test-log.txt`).
- **Tauri 2 shell** (`src-tauri/`) — thin IPC layer exposing the Core's
  operations to the frontend (`src-tauri/src/lib.rs`, 205 LOC), plus
  platform-conditional window chrome config
  (`tauri.macos.conf.json`: overlay title bar + `macOSPrivateApi` for native
  traffic lights and vibrancy; `tauri.windows.conf.json`: `decorations:
  false` + `transparent: true` for the custom-caption/Mica-tint path).
- **Frontend** (`src/`, vanilla JS/HTML/CSS, 586 LOC) — shared layout
  (My Day list with rich/quiet row forms, Task Detail, subtask rows with the
  circle-vs-text hit-zone split per ADR 0004, sandboxed HTML artifact
  preview, reserved Planner nav stub with zero content) plus the accepted
  Deliberate Adaptation chrome split: native traffic lights + subtle
  vibrancy tint on macOS, custom caption buttons + Mica-like tint on
  Windows, identical shared layout otherwise.
- **Fixed 3-task fixture** (`fixture-vault/`) — byte-identical to the
  fixture locked in #370: `budget-q3-review.md` (rich card, active, with a
  blocked subtask and an HTML artifact), `confirm-tailscale-acl.md` (rich
  card, active, the file-watch demo task), `renew-domain.md` (quiet/single-
  line row, low priority). MD5s recorded below for tamper-evidence.

## Hard safety gates (Phase 0) — macOS results

Locked acceptance scripts from the scorecard, run against the macOS release
build:

| # | Gate | macOS result |
|---|---|---|
| 1 | Full slice behavior | **PASS** — every item in the checklist present and functional against the fixture (verified via screenshots + `vertical-slice.spec.js` + `subtask-reorder.spec.js`) |
| 2 | Genuine HTML sandbox | **PASS** — `evidence/html-sandbox-verification.json` (`verdict: "SANDBOX_HELD"`), corroborated by `tests/artifact_sandbox.rs`'s `html_sandbox_csp_blocks_script_and_network_and_framing_out` and `sandbox-verify.spec.js` |
| 3 | Native file/Obsidian launching | **macOS: PASS.** Deep-link build verified by `core/tests/obsidian_uri.rs`; a real gap was found and fixed this session (an earlier version dropped `.md` incorrectly for compound extensions — see `obsidian_uri.rs::drops_the_md_extension_but_preserves_other_extensions`). **Windows: NOT YET RUN** — no Windows device available this session |
| 4 | Accessibility reachability | **macOS: NOT YET CONFIRMED via live VoiceOver.** Keyboard-focus reachability for every zone is exercised by `vertical-slice.spec.js` and `subtask-reorder.spec.js`, but a spoken-announcement check requires a human with VoiceOver running — see "VoiceOver — required HITL step". **Windows/Narrator: NOT YET RUN** |
| 5 | A real automated test passes | **PASS** — 7 WebDriverIO specs (27 tests total incl. Rust), each exercising a real fixture interaction, not app-launch smoke tests |
| 6 | No crash or hang | **PASS** on macOS across all evidence-capture sessions this segment; no crash or >10s unresponsive period observed. **Windows: NOT YET RUN** |

**Gates 3 and 4 cannot be marked fully PASS until the Windows half runs.**
Per the gate definition ("succeeds on both macOS and Windows" / "both
VoiceOver ... and Narrator"), this ticket treats them as open until that
evidence exists — this is intentional, not an oversight.

## Measured evidence (Phase 1, 30% bucket) — macOS raw numbers

Raw values in `evidence/measured-performance-macos.json` (median of 3 runs
each; per-run values in `evidence/measured-performance-macos-raw.jsonl`).
**These are not yet scoreable** — the scorecard's normalization is a
relative ranking among gate-surviving candidates in the *same* round, and no
other spike (Avalonia, Uno) has been built yet in this ticket's scope. What
follows is raw evidence only, for #381 to rank once all three exist.

| # | Metric | macOS value | Caveat |
|---|---|---|---|
| 1 | Cold launch time | 117 ms (median) | **Not a true frozen-cache cold launch** — no reboot/disk-cache purge was available in this environment (no `sudo`/`purge`). Must be redone in a genuinely frozen environment per the scorecard's "Measurement Environment Freeze" section, alongside the Windows pass. |
| 2 | Task-detail interaction latency | ~1 ms (median) | Captured via in-page `performance.now()` timestamps instrumented in `src/main.js`, read through the WebDriverIO embedded provider (see note below on why not native OS Accessibility APIs). |
| 3 | Idle resident memory | 84.97 MB (median) | Via `ps -o rss=`, 30s post-launch, no interaction — matches #370's procedure. |
| 4 | Installed package size | App bundle 15 MB / DMG 4.3 MB | **Inflated vs. a real production build** — includes `@wdio/tauri-service`'s test-only WebDriver bridge plugin, which a real release build would feature-gate out. Flagged explicitly so this isn't silently compared against Avalonia/Uno builds that won't carry test-only weight. |
| 5 | File-watch response latency | not separately stopwatched | The live-update *behavior* is proven (`evidence/filewatch-live-update.json`, `verdict: "LIVE_UPDATE_CONFIRMED"`, corroborated by `tests/watcher_live_update.rs` and the `filewatch-live-update.spec.js` WDIO test), but a clean 3-run stopwatch timing from "external save" to "on-screen update," timestamped from the recording, was **not captured this session** — the recording evidence covers the reorder interaction only (see "Recording evidence" below). This metric currently has **no timing value**, only a pass/fail behavioral confirmation. Must be captured before scoring. |
| 6 | Accessibility completeness | not yet measured | Depends on the live VoiceOver/Narrator pass (see gate 4). |
| 7 | Testability breadth | **6 / 6** catalog interactions covered | toggle a subtask (`vertical-slice.spec.js`), open Task Detail (`vertical-slice.spec.js`), drag-reorder a subtask (`subtask-reorder.spec.js`, via the accessible keyboard alternative — see UX finding below), trigger the HTML preview (`sandbox-verify.spec.js`), trigger a live file-watch update (`filewatch-live-update.spec.js`), navigate via keyboard (`vertical-slice.spec.js`'s Planner-nav test). Each test asserts on one catalog action's result, no inflation via trivial sub-assertions. |
| 8 | Implementation complexity | see LOC breakdown below | |

### Implementation-complexity LOC breakdown (metric 8)

The scorecard excludes "shared `Glasswork.Core` logic" from this metric —
written with the Avalonia/Uno spikes in mind, which reuse the *existing*
C# Core without reimplementing it. Tauri has no such existing-Core reuse
path; the bounded Rust Core is **new code this framework specifically
required**. Both numbers are reported separately so #381 can decide how to
compare fairly, rather than silently picking one framing:

| Layer | LOC | Included in metric 8? |
|---|---|---|
| Frontend UI (`src/*.js`, `*.html`, `*.css`, excluding vendored libs) | 586 | Yes — framework-specific UI/presentation layer |
| Tauri IPC shell (`src-tauri/src/*.rs`, hand-written, excludes `src-tauri/gen/`) | 211 | Yes — framework-specific UI/presentation layer |
| **Framework-specific UI total** | **797** | — |
| Bounded Rust Core (`core/src/*.rs`) | 608 | **Reported separately, not counted in the 797** — this is new Core-equivalent logic, not "presentation," but also not reusing an existing Core the way Avalonia/Uno do. Flagged for #381 to decide the fair comparison basis (e.g. compare 797 head-to-head, or compare 797+608 against Avalonia/Uno's own incremental C# Core changes, if any). |
| Rust Core tests (`core/tests/*.rs`, for reference only) | 299 | No — tests are not "implementation" |

## PWA-reuse bonus — module classification (provisional)

Locked 6-module list, classified against the locked PWA target (a
Tailscale-only web frontend consuming the same `Glasswork.Core`-exposed
API). This spike's frontend is vanilla JS/HTML/CSS with no Tauri-specific
framework lock-in in the rendering layer itself — only the IPC calls
(`window.__TAURI__.core.invoke(...)`) are Tauri-specific.

| Module | Classification | One-line reason |
|---|---|---|
| My Day list rendering | Reusable-as-is (1.0) | Pure DOM rendering from a JSON task list; the IPC `invoke` call is the only Tauri-specific line, trivially swappable for a `fetch()` to a PWA API. |
| Task card rendering | Reusable-as-is (1.0) | Same — pure template/DOM logic, no Tauri API surface in the card markup itself. |
| Task Detail shell | Reusable-as-is (1.0) | Layout and data-binding are framework-agnostic; only the load/save IPC calls need swapping. |
| Subtask row rendering | Reusable-as-is (1.0) | Circle/text hit-zone split (ADR 0004) is plain DOM + event listeners; no Tauri dependency. |
| Artifact-kind rendering/routing | Adaptable-with-changes (0.5) | Markdown rendering is reusable, but the sandboxed HTML preview currently relies on Tauri's CSP/webview sandboxing config (`tauri.conf.json` CSP + `sandbox` iframe attrs tuned against Tauri's webview); a PWA would need to re-validate the same sandbox guarantee inside a plain browser iframe, which is a real but bounded adaptation. |
| Nav rail | Reusable-as-is (1.0) | Static nav markup + click handlers; the vibrancy/Mica **chrome tinting** is platform-native styling layered on top via CSS classes, not baked into the nav rail's own reusable structure. |

Reuse score = `(5 × 1.0 + 1 × 0.5) / 6 = 0.917`. **Provisional**: this is a
real, defensible classification of this spike's actual code, but per the
scorecard the bonus is *relative-ranked against the other gate-surviving
candidates* — it can't be turned into a final +bonus value until Avalonia
and Uno's own classifications exist.

## Recording evidence — what each artifact proves (read this before scoring)

Two pieces of evidence exist and are **deliberately separate**, not one
combined recording, after three iterative recording attempts revealed a real
capture-timing limitation (not a script bug — see below):

- **`evidence/screen-recording-subtask-reorder.mov`** (7.01s) — shows the
  reorder interaction only: Task Detail open → a single Alt+ArrowDown
  keyboard reorder → the reordered state holding stably (no oscillation).
  Trimmed to remove dead air after the driver tears down the app window.
- **`evidence/filewatch-live-update.json`** (`verdict:
  "LIVE_UPDATE_CONFIRMED"`) + `tests/watcher_live_update.rs` +
  `filewatch-live-update.spec.js` — proves the live file-watch update
  (external frontmatter edit → on-screen update with no restart) via
  structured before/after DOM text and a passing automated test, **not**
  via the recording.

**Why split, not re-recorded a 4th time**: across three recording attempts,
the WebDriver-driven "return to My Day" step in the combined sequence was
never visibly captured before the app window closed (confirmed via 4fps
frame-by-frame analysis — the app was still showing Task Detail at t=6.75s
and gone by t=7.0s, well past the sequence's own ~4.3s estimated completion
time). The root cause (driver/render overhead vs. teardown timing, or a
`screencapture` wall-clock skew) was not fully diagnosed. Given this is an
explicitly disposable, time-boxed prototype, and the live-update behavior
already has strong independent automated proof, this was judged a
reasonable place to stop rather than burn further time-box budget on
recording mechanics. **This should be discussed with TJ, not silently
accepted** — if a combined recording is considered a hard requirement, flag
that back to this ticket.

## Real findings and fixes discovered during this build

Not scorecard requirements, but genuine bugs/gaps found and fixed while
building the spike (documented here for transparency, since a prototype's
job includes surfacing exactly this kind of thing):

1. **Obsidian deep-link extension bug** — the original URI builder dropped
   more than just the trailing `.md` for filenames with compound extensions.
   Fixed in `core/src/obsidian_uri.rs`; regression test:
   `drops_the_md_extension_but_preserves_other_extensions`.
2. **Two fixture-corruption bugs in the WDIO test suite** —
   `vertical-slice.spec.js` and `subtask-reorder.spec.js` originally
   mutated the fixture Vault (via the subtask-toggle and reorder IPC calls)
   without restoring it afterward, silently corrupting the fixed fixture
   for every later screenshot/recording/test run. Fixed by snapshotting the
   fixture file before the mutating action and restoring it in a `finally`
   block in both specs. This is exactly the kind of test-suite integrity
   bug the TDD skill's "seams" discipline is meant to catch early — caught
   late here only because the fixture is filesystem state outside any
   single test's normal in-memory teardown.
3. **WebDriverIO key-chord delivery limitation** — WebdriverIO's `keys()`
   action could not reliably deliver an `Alt+ArrowDown` chord to the Tauri
   webview in this environment. Worked around by dispatching a synthetic
   `KeyboardEvent` via `browser.execute()` instead of relying on native OS
   key injection. This is a test-infrastructure limitation, not a product
   bug — flagged so it isn't mistaken for one if Avalonia/Uno's WDIO-
   equivalent tooling behaves differently.
4. **Real keyboard-accessibility UX finding: focus-restoration oscillation.**
   After a subtask reorder, focus is restored **by list position**, not by
   element identity. Reordering once moves the subtask and correctly
   restores focus to "whatever is now at that position" — but pressing the
   same reorder shortcut repeatedly on a focused row causes the focus to
   oscillate between two positions rather than following the moved item.
   This is a genuine finding worth TJ's HITL judgment (not fixed in this
   spike, since it's plausibly present in the *design*, not just this
   framework's implementation) — is position-based focus restoration
   acceptable, or does this need to become an explicit design requirement
   in a future ticket regardless of which framework wins?

## VoiceOver — required HITL step (blocking)

Automated VoiceOver driving proved unreliable for this ad-hoc-signed dev
binary in this environment (no stable Accessibility-permission grant across
rebuilds; automating VoiceOver's own UI is fundamentally a human-in-the-
loop check, not a good target for scripted automation regardless). This
must be a **live pass by TJ** (or another sighted-but-listening human) on
the real macOS device:

1. Launch the release build (see "Running the spike" below).
2. Enable VoiceOver (Cmd+F5).
3. Tab/swipe through every zone in Gate 4's list: both nav rail items, both
   task-row forms' hit zones, the subtask circle-vs-text hit zones, the
   reorder mechanism (or its keyboard alternative), the HTML preview's
   boundary control.
4. Record pass/fail per zone (reachable + *some* announcement = pass) and,
   for metric 6, whether the announcement includes a **correct** name +
   role + state (not just generically reachable).
5. Report results back to this ticket (or the coordinating session) so
   Gate 4 and metric 6 can be closed out.

## Windows reproduction package

No Windows runner/device was available in this session. To reproduce and
measure on Windows, on a machine matching the scorecard's "Measurement
Environment Freeze" (exact Windows version/build, same hardware model
class, Release build, fixture Vault reset before each run, cold-cache
launch immediately after reboot):

```powershell
# 1. Prerequisites: Rust (stable-msvc), Node.js LTS, Tauri 2 Windows
#    prerequisites (WebView2 runtime, MSVC Build Tools) -- see
#    https://v2.tauri.app/start/prerequisites/#windows
cd docs\prototypes\tauri-core-spike
npm install

# 2. Rust Core tests (framework-agnostic, should pass unmodified on Windows)
cargo test --release

# 3. Release build -- ALWAYS use the Tauri CLI wrapper, never raw
#    `cargo build --release`, so bundling/config/plugins apply correctly:
npx tauri build --no-bundle

# 4. WebDriverIO automated tests -- Windows requires the 'official'
#    driver provider (Microsoft Edge WebDriver) instead of macOS's
#    embedded provider. Edit test/wdio.conf.js:
#      services: [["@wdio/tauri-service", {
#        appBinaryPath: "./target/release/tauri-app.exe",
#        driverProvider: "official"   // <-- was "embedded" on macOS
#      }]]
#    Then run each spec separately (do not batch):
npx wdio run test/wdio.conf.js --spec test/specs/vertical-slice.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/subtask-reorder.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/sandbox-verify.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/filewatch-live-update.spec.js

# 5. Measured evidence -- repeat the same 3-run-median procedure as
#    evidence/measured-performance-macos.json:
#      - cold_launch_ms: reboot immediately before run 1, then 3 runs
#      - interaction_latency_ms: same performance.now() instrumentation
#        in src/main.js, already framework-agnostic
#      - idle_memory_mb: use Get-Process's WorkingSet64 (Windows analog
#        of macOS's `ps -o rss=`), 30s post-launch
#      - installed_package_size: on-disk size of the built .msi/.exe
#        bundle (run `npx tauri build` WITH bundling for this one
#        measurement, noting the wdio-plugin-inflation caveat as on macOS)

# 6. Gate 3 (Obsidian launch) and Gate 4 (Narrator pass) -- manual,
#    same procedure as the macOS VoiceOver HITL step above, substituting
#    Narrator (Windows+Ctrl+Enter).
```

Coordinate a separate Windows session for this only if an appropriate
configured project/device is discoverable — do not fabricate results if
none is available; keep this ticket open and report the gap.

## Not in scope (explicitly excluded)

- Avalonia and Uno spikes (#377, #380) — untouched by this ticket.
- Production migration or any change to `Glasswork.App` / `Glasswork.Core`
  — this spike is fully isolated under `docs/prototypes/tauri-core-spike/`.
- Full product feature parity — only the locked vertical slice + fixture.
- Final scoring/decision — that's ticket
  [Select the cross-platform framework and Core direction](https://github.com/tjegbejimba/Glasswork/issues/381),
  which requires all three spikes' evidence.

## Running the spike locally (macOS)

```bash
cd docs/prototypes/tauri-core-spike
npm install
cargo test --release                       # bounded Rust Core: 20 tests
npx tauri build --no-bundle                 # release build (never raw cargo build --release)
npx wdio run test/wdio.conf.js --spec test/specs/vertical-slice.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/subtask-reorder.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/sandbox-verify.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/filewatch-live-update.spec.js
```

## Fixture integrity (tamper-evidence)

The fixed 3-task fixture in `fixture-vault/` must remain byte-identical to
the state locked by #370 for every screenshot/recording/test to remain
valid evidence. MD5s at time of evidence capture:

```
043616756669e7b1123aa072e23947ab  fixture-vault/budget-q3-review.md
d695b88a3101d5436ebc9d4d49eeddd9  fixture-vault/confirm-tailscale-acl.md
527e1aeeafd01da4c23b797be7e0545b  fixture-vault/renew-domain.md
```

## Framework-specific considerations (non-scoring notes)

Per the scorecard's Phase 2 non-scoring notes section, carried here for
#381's later reference:

- **Two-runtime operational cost**: Tauri pairs a Rust backend process with
  a system WebView frontend — two languages/runtimes in one app, versus a
  single-process C#/XAML app for Avalonia/Uno. This spike's bounded Rust
  Core (608 LOC) is the concrete cost of that split; a production port
  would need to either grow this considerably or keep more logic on the JS
  side (with attendant trust/validation tradeoffs, especially given ADR
  0006's "all rendered content is untrusted" rule).
- **Capability-scoped security model**: Tauri 2's `capabilities/` system is
  a real, structurally-enforced permission boundary for what the frontend
  can invoke — a stronger default security posture than a WebView2 control
  embedded in a C# app with no equivalent capability manifest. Relevant
  context for the untrusted-artifact-rendering requirement (ADR 0006).
- **Dev binary is ad-hoc-signed**, which is why VoiceOver/Accessibility
  permission grants didn't persist across rebuilds in this environment — a
  real production-signed build should not have this friction, so treat the
  automation-instability finding as environment-specific, not a permanent
  Tauri limitation.
