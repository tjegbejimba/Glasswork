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
- [x] macOS: automated tests passing (61 total — 48 Rust Core, 13 WebDriverIO)
- [x] macOS: HITL evidence artifacts prepared (screenshots + recording) for
      TJ to review
- [ ] macOS: measured evidence is **incomplete** — 3 of 8 metrics fully meet
      their locked procedure, 1 is measured with an interpretation note, and
      4 have real gaps. See "Measured evidence" below; nothing here is ready
      for the calculator.
- [ ] macOS: **native drag-reorder is unverified** — automated drag input
      could not be delivered to the webview, so only the keyboard alternative
      is covered. Gate 1 stays partial until TJ drags a subtask by hand.
- [ ] macOS: the drag-reorder / file-watch **recording is stale** — it
      predates the three-Page-shell correction and still shows Task Detail as
      an inline expansion. It needs re-recording (a HITL capture: the display
      must be awake and unlocked), together with the metric-5 file-watch
      timing it should carry. See "Recording evidence" below.
- [ ] macOS: native-chrome screenshots need a HITL re-capture — see
      "Screenshot evidence" below.
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

### Correction history

An earlier revision of this spike overstated parts of its evidence. A
two-axis code review against the locked specs caught it, and the following
were fixed rather than argued down (all are real changes, not re-wordings):

- **Live file-watch used the wrong fixture task.** #370 names "Confirm
  Tailscale ACL update" as the file-watch demo task; the test exercised
  `renew-domain`. Now corrected, and the evidence regenerated.
- **The HTML sandbox gate claimed PASS without testing the network probe.**
  It only checked that the parent title was unmutated and that
  `allow-scripts` was absent — inferring the network block rather than
  observing it. Now proven with a local canary HTTP server plus a control
  request that proves the detector works before the negative result counts.
- **Untrusted Vault prose was injected raw into `innerHTML`.** A live XSS:
  an `onerror` payload in a Task's Notes actually executed. Now escaped, with
  regression tests carrying real executable payloads.
- **Planner shipped explanatory Page content**, contradicting #370's "no
  Planner content, not even a static layout". The nav entry is now reserved
  and non-routable, and its Page renders nothing.
- **Task Detail was an inline row expansion**, not the third Page of #370's
  three-page shell. It is now a distinct Page with its own navigation.
- **Measured evidence was summarized as "8 metrics captured"** when several
  did not meet their locked procedure. Each metric now carries an explicit
  status and, where applicable, the exact gap.

A second review round then caught four more, also fixed rather than argued
down:

- **Vault values were still unescaped in attribute contexts.** A subtask's
  `status` went raw into a `class` attribute; a payload closing the quote
  executed. Escaped, with a regression test. (A companion test confirms the
  Markdown link `href` path was *not* exploitable — the earlier escape pass
  already neutralized it — and is kept as a guard.)
- **The fixture was missing the `in_progress` subtask state** #370 requires,
  which is also why the My Day card's "Current:" line rendered empty.
- **Gate 3 was marked PASS on unit tests**, not the observed native launch
  its acceptance script requires. Retracted.
- **Interaction latency was stamped at first paint** while Artifact rows
  still showed placeholders, reporting ~1 ms against a "fully rendered and
  interactive" definition. Corrected to ~13 ms.

A third and fourth round caught six more:

- **"Open externally" opened the parent task, not the Artifact** — the
  filename was destructured out of the payload and then discarded.
- **`read_artifact` had a path-traversal hole.** It built the path with a
  bare `join` on a frontend-supplied filename; an absolute path would have
  silently discarded the Vault base entirely (`join("/etc/passwd")` escapes
  with no `..` involved). Now routed through `vault::resolve_contained`, with
  six tests covering traversal, absolute paths, and the legitimate cases.
- **Untrusted HTML defaulted to Preview**, but ADR 0015 specifies "HTML →
  **Source** view (default) plus an opt-in **Preview**", created lazily on
  click. Auto-rendering agent-authored HTML the user never asked to see is
  exactly what that ADR rules out. Source is now the default.
- **"Trigger the HTML preview" was claimed as a covered catalog interaction**
  while the test merely waited for an iframe that rendered by default —
  nothing was triggered. Now that Preview is opt-in, the test genuinely
  clicks it, so the claim is true.
- **The Rust test count was understated** in the run instructions.
- **The stale recording was only footnoted**, not listed as outstanding.

A fifth round found the Artifact-path fix was only half done:

- **Containment was lexical only, so a symlink still escaped.** A link living
  *inside* the Vault but pointing outside passes every `..`/absolute check and
  still yields foreign content once opened — and `read_artifact` then calls
  `read_to_string`, which follows symlinks. Reading now goes through
  `vault::canonical_contained`, which canonicalizes both sides so the
  comparison is between real paths. Tests cover an escaping symlink, a
  legitimate in-Vault symlink (which must still work), a non-existent path
  (refused rather than assumed safe), and a sibling directory whose name
  merely prefixes the Vault root (`/x/vault-evil` vs `/x/vault`).

A sixth round found three more:

- **External markdown links were launched as an Obsidian file open.** Clicking
  an `https://` link inside an Artifact invoked the deep-link command with the
  *current task*, so it opened the task in Obsidian rather than the link.
  ADR 0006 routes such links through one policy and launches allowed ones as
  URLs. There is now a Core-side `is_allowed_external_url` (http/https only,
  case-insensitive, rejecting whitespace/control-character bypasses like
  `java\tscript:`) and an `open_external_url` command that re-validates before
  launching — the render-time gate is a convenience, not the boundary.
- **Artifact reads were still check-then-read.** Now read from a single opened
  handle instead of re-resolving the path. A residual check-then-open race
  remains and is documented at the call site rather than claimed closed:
  eliminating it needs `openat`/`O_NOFOLLOW`, which std does not expose
  portably, and it requires an attacker who already has write access inside
  the Vault.
- **The README claimed a "non-existent path" test that did not exist.** It
  does now.

A seventh round found two more:

- **The link policy was a scheme-prefix check, not a URL check.** `https://`
  with no host passed, as did misdirection forms like
  `https://example.com@evil.test` (reads as example.com, resolves to
  evil.test) and backslash variants that move the effective authority
  boundary. Since the result is handed to the OS to launch, the authority is
  now validated too: non-empty host required, embedded credentials refused,
  backslashes refused. Ordinary links with ports, paths, queries and
  fragments still pass.
- **Gate 1 was claimed PASS although native drag-reorder is unverified.**
  WebDriver could not deliver real drag/pointer input to the webview, so the
  automated test covers the accessible keyboard alternative. Gate 1 is now
  recorded as PARTIAL — the scorecard allows no partial credit, so it needs
  TJ's hands-on drag check to close.

## What was built

- **Bounded Rust Core** (`core/`) — a from-scratch Rust reimplementation of
  *only* the slice-required subset of `Glasswork.Core`'s behavior: task
  frontmatter parsing/serialization round-trip, vault folder loading,
  file-watch change detection, self-write suppression (so the app's own
  writes don't re-trigger its own watcher), Obsidian deep-link URI building
  with vault-escape rejection, and HTML/Markdown artifact-kind
  classification + sandboxed-HTML CSP policy. It is **not** a port of the
  whole product — no Planner, no backlinks, no UI state, no full task
  catalog beyond the fixed fixture. 48 passing tests (`cargo test --release`,
  see `evidence/automated-ui-test-log.txt`).
- **Tauri 2 shell** (`src-tauri/`) — thin IPC layer exposing the Core's
  operations to the frontend (`src-tauri/src/lib.rs`, 224 LOC), plus
  platform-conditional window chrome config
  (`tauri.macos.conf.json`: overlay title bar + `macOSPrivateApi` for native
  traffic lights and vibrancy; `tauri.windows.conf.json`: `decorations:
  false` + `transparent: true` for the custom-caption/Mica-tint path).
- **Frontend** (`src/`, vanilla JS/HTML/CSS, 644 LOC) — the three-Page shell
  (My Day, Task Detail as its own Page reached by navigation, and a reserved
  **non-routable** Planner nav entry that renders no Page content at all),
  with rich/quiet My Day row forms, subtask rows with the
  circle-vs-text hit-zone split per ADR 0004, sandboxed HTML artifact
  preview, reserved Planner nav stub with zero content) plus the accepted
  Deliberate Adaptation chrome split: native traffic lights + subtle
  vibrancy tint on macOS, custom caption buttons + Mica-like tint on
  Windows, identical shared layout otherwise.
- **Fixed 3-task fixture** (`fixture-vault/`) — byte-identical to the
  fixture locked in #370: `budget-q3-review.md` (rich card, active, four
  subtasks covering done / in_progress / blocked / todo, plus a Markdown and
  an untrusted HTML artifact), `confirm-tailscale-acl.md` (quiet/single-line
  row, medium priority — it exists purely to demonstrate live file-watch),
  `renew-domain.md` (quiet/single-line row, low priority). MD5s recorded
  below for tamper-evidence.

## Hard safety gates (Phase 0) — macOS results

Locked acceptance scripts from the scorecard, run against the macOS release
build:

| # | Gate | macOS result |
|---|---|---|
| 1 | Full slice behavior | **PARTIAL — one item unverified.** Present and automatically verified: both My Day row forms, Task Detail as a distinct Page, both Artifact kinds, the ADR 0004 hit-zone split, live file-watch on the "Confirm Tailscale ACL update" task, the reserved Planner nav entry with zero content, keyboard reach to nav/rows/subtasks (`vertical-slice.spec.js`, `filewatch-live-update.spec.js`, `sandbox-verify.spec.js`, `planner-stub-verification.json`). **Not verified: native drag-reorder.** HTML5 drag handlers are implemented, but WebDriver could not deliver real drag/pointer input to this webview, so `subtask-reorder.spec.js` exercises the *accessible keyboard alternative* via a synthetic `KeyboardEvent` and asserts the new order round-trips through the Core. That proves the reorder pipeline, not the drag gesture #370 describes — dragging is a HITL check for TJ. |
| 2 | Genuine HTML sandbox | **PASS**, now on direct evidence for *both* probes — `evidence/html-sandbox-verification.json` records a local canary server seeing 1 control request (proving the detector works) and **0** requests from the sandboxed artifact, alongside an unmutated parent title and unset parent flag. Corroborated by `tests/artifact_sandbox.rs` and `sandbox-verify.spec.js`. |
| 3 | Native file/Obsidian launching | **NOT YET VERIFIED on either OS.** The scorecard's acceptance script is "trigger it once per OS; both must open the correct file in the correct external application" — that is an observed native launch, which has not been done. What exists is unit coverage of the URI builder (`core/tests/obsidian_uri.rs`, including a real compound-extension bug found and fixed) and the removal of the ADR 0006-rejected default-handler fallback. An earlier revision of this README marked macOS PASS on that basis; that was an overstatement and is retracted. Needs a HITL launch check on both macOS and Windows. |
| 4 | Accessibility reachability | **NOT YET CONFIRMED.** Keyboard-focus reachability for every zone (including the reserved Planner entry, which uses `aria-disabled` precisely so it stays focusable) is exercised by the automated specs, but the spoken-announcement half needs a human running VoiceOver. **Windows/Narrator: NOT YET RUN.** |
| 5 | A real automated test passes | **PASS** — 61 tests total (48 Rust Core, 13 WebDriverIO), each WebDriverIO spec exercising a real fixture interaction rather than an app-launch smoke test. |
| 6 | No crash or hang | **PASS** on macOS across all evidence-capture sessions; no crash or >10s unresponsive period observed. **Windows: NOT YET RUN.** |

**Gates 1, 3, 4 and 6 are not PASS.** Gate 1 is partial: native drag-reorder
is implemented but unverified (see the row above), and the scorecard allows no
partial credit — "**any** single failure fails this gate" — so it must be
closed out by TJ's hands-on drag check. Gate 3 needs an observed native Obsidian
launch on both OSes (unit-tested URI building is not the acceptance script);
Gate 4 needs live VoiceOver + Narrator passes; Gate 6 needs the Windows run. Per the gate definitions
("succeeds on both macOS and Windows" / "both VoiceOver ... and Narrator"),
this ticket treats them as open — intentionally, not as an oversight.

## Measured evidence (Phase 1, 30% bucket) — macOS raw numbers

Raw per-run values in `evidence/measured-performance-macos-raw.jsonl`;
per-metric status and gaps in `evidence/measured-performance-macos.json`.

**Nothing here is ready for the calculator**, for two independent reasons:
the scorecard normalizes by relative ranking among gate-surviving candidates
(and Avalonia/Uno don't exist yet), *and* four of the eight metrics do not
yet meet their locked measurement procedure. Per the scorecard's
missing-evidence rule those score **0** if they stay uncaptured — that is
the honest current position, not a placeholder to be quietly upgraded later.

| # | Metric | macOS value | Status |
|---|---|---|---|
| 1 | Cold launch time | 95 ms (median of 118 / 40 / 95) | **Procedure not met.** The wide spread is itself a warning sign. The Measurement Environment Freeze defines cold launch as following a reboot or guaranteed cold cache. No reboot/`purge` was available here, so these are warm-cache launches. Must be re-measured in a frozen environment. |
| 2 | Task-detail interaction latency | 13 ms (median of 16 / 13 / 13) | **Measured.** Delta from the navigation click until every Artifact load has settled and the re-render is committed. An earlier revision stamped this at first paint while Artifact rows still read "loading…", reporting ~1 ms — that did not meet the scorecard's "fully rendered and interactive" wording and has been corrected, which is why the number went up. Still excludes the final compositor paint, so it is a lower bound. |
| 3 | Idle resident memory | 96.3 MB (median of 96.3 / 96.3 / 96.1) | **Measured**, with a comparability caveat: this is RSS of the Tauri **main process only**, and WKWebView runs page content in separate WebKit helper processes — so it undercounts, in a way single-process Avalonia/Uno spikes will not. #381 needs whole-process-tree accounting before comparing. One earlier 45.7 MB sample was an incomplete launch and was re-run, not averaged in, per the invalid-run rule. |
| 4 | Installed package size | app bundle 15 MB / DMG 4.3 MB | **Partial — one OS only.** The scorecard averages both OSes; Windows is pending. Also inflated by the test-only WebDriver bridge, which a production build would feature-gate out. |
| 5 | File-watch response latency | *no value* | **Not measured.** The *behavior* is proven (`evidence/filewatch-live-update.json`, verdict `LIVE_UPDATE_CONFIRMED`, on the locked Tailscale ACL task) and a single WebDriver-polled observation of ~23 ms exists, but the metric requires a 3-run median timestamped from the acceptance recording. The single observation is deliberately **not** presented as the metric value. |
| 6 | Accessibility completeness | *no value* | **Not measured.** Requires live VoiceOver + Narrator passes scoring correct name/role/state per zone, averaged across both OSes. HITL, and half of it needs a Windows device. |
| 7 | Testability breadth | **6 / 6** catalog interactions | **Measured.** toggle a subtask, open Task Detail, drag-reorder (the accessible keyboard alternative, not the native drag gesture — see Gate 1), trigger the HTML preview (a genuine click on the opt-in Preview control, asserted absent beforehand), trigger a live file-watch update, navigate via keyboard — each asserted by a spec performing that one action, with no inflation via trivial sub-assertions. |
| 8 | Implementation complexity | 868 LOC framework UI (+635 Core, reported separately) | **Measured, with an interpretation note** — see below. |

### Implementation-complexity LOC breakdown (metric 8)

The scorecard excludes "shared `Glasswork.Core` logic" from this metric —
written with the Avalonia/Uno spikes in mind, which reuse the *existing*
C# Core without reimplementing it. Tauri has no such existing-Core reuse
path; the bounded Rust Core is **new code this framework specifically
required**. Both numbers are reported separately so #381 can decide how to
compare fairly, rather than silently picking one framing:

| Layer | LOC | Included in metric 8? |
|---|---|---|
| Frontend UI (`src/*.js`, `*.html`, `*.css`, excluding vendored libs) | 644 | Yes — framework-specific UI/presentation layer |
| Tauri IPC shell (`src-tauri/src/*.rs`, hand-written, excludes `src-tauri/gen/`) | 224 | Yes — framework-specific UI/presentation layer |
| **Framework-specific UI total** | **868** | — |
| Bounded Rust Core (`core/src/*.rs`) | 635 | **Reported separately, not counted in the 868** — this is new Core-equivalent logic, not "presentation," but also not reusing an existing Core the way Avalonia/Uno do. Flagged for #381 to decide the fair comparison basis (e.g. compare 868 head-to-head, or compare 868+635 against Avalonia/Uno's own incremental C# Core changes, if any). |
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

## Screenshot evidence — and what it does *not* show

`evidence/screenshot-*.png` are **WebDriver captures of the webview**, so
they show layout, content and interaction states faithfully but **not** the
native window chrome (macOS traffic lights, vibrancy tint). Those are the
subject of the scorecard's "Platform-native chrome fidelity" sub-rating, so
this gap matters and is called out rather than glossed:

- The native chrome **was** verified by direct inspection of a real OS-level
  screen capture during this session — native traffic lights, the vibrancy
  tinted nav rail, and the dimmed reserved Planner entry all rendered as the
  Variant B brief specifies.
- A fresh OS-level capture could not be re-taken after the final fixture fix:
  the machine's display slept and then locked, and `screencapture` cannot
  photograph an app window in that state. **Capturing the chrome screenshots
  is therefore a HITL step**, same as the VoiceOver pass — it needs a human
  at an unlocked display. TJ will in any case be judging chrome fidelity
  hands-on against the live app, which is stronger evidence than a PNG.

There is deliberately **no** `screenshot-planner-stub.png`. A correct Planner
stub renders identically to My Day (that is the whole contract), so the image
proved nothing — two byte-identical PNGs. It is replaced by
`evidence/planner-stub-verification.json`, which records the assertions that
actually establish the contract: entry present, `aria-disabled`, keyboard
focusable, click does not route, Planner Page content is the empty string.

## Recording evidence — what each artifact proves (read this before scoring)

Two pieces of evidence exist and are **deliberately separate**, not one
combined recording, after three iterative recording attempts revealed a real
capture-timing limitation (not a script bug — see below):

- **`evidence/screen-recording-subtask-reorder.mov`** (7.01s) — shows the
  reorder interaction only: Task Detail open → a single Alt+ArrowDown
  keyboard reorder → the reordered state holding stably (no oscillation).
  Trimmed to remove dead air after the driver tears down the app window.
  **Note:** this recording predates the three-Page-shell correction, so it
  shows Task Detail as an inline row expansion. The reorder interaction it
  demonstrates is unchanged, but the navigation model in it is stale — the
  current shape is in `evidence/screenshot-task-detail-blocked-subtask.png`.
  It should be re-recorded alongside the pending file-watch timing capture.
- **`evidence/filewatch-live-update.json`** (`verdict:
  "LIVE_UPDATE_CONFIRMED"`) + `tests/watcher_live_update.rs` +
  `filewatch-live-update.spec.js` — proves the live file-watch update
  (external frontmatter edit → on-screen update with no restart) on the
  **locked "Confirm Tailscale ACL update" fixture task** (#370), via
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
4. **Live XSS through untrusted Task prose.** Description/Notes were
   interpolated straight into `innerHTML`. An `<img onerror>` payload in a
   Task's Notes genuinely executed — confirmed by a failing test before the
   fix (`Expected: false, Received: true`). Notable because an `escapeHtml`
   helper already existed in the same file and was applied to Artifact
   content and wiki links, just not to task prose: the boundary was
   inconsistent rather than absent. Now a single module-scope helper, with
   prose routed through the same bounded Markdown renderer as Artifacts, and
   `untrusted-content.spec.js` carrying real executable payloads.
5. **Presentation was re-deriving domain rules.** `isRich` / `showAsCard`
   existed in both `core/src/model.rs` and `src/main.js`. The Core copy is
   now the only one: `TaskView` serializes the derivations onto the payload
   (`core/tests/task_view.rs`), so the UI reads the decision instead of
   recomputing it.
6. **The Obsidian launcher had an ADR 0006-rejected fallback**, handing raw
   Vault files to the OS default handler when a deep link couldn't be built
   — an arbitrary-editor/data-loss risk that also masked broken deep links.
   Removed, and the `opener:allow-open-path` capability withdrawn so the
   privileged bridge no longer exists to be called.
7. **Real keyboard-accessibility UX finding: focus-restoration oscillation.**
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
npx wdio run test/wdio.conf.js --spec test/specs/untrusted-content.spec.js
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
cargo test --release                       # bounded Rust Core: 48 tests
npx tauri build --no-bundle                 # release build (never raw cargo build --release)
npx wdio run test/wdio.conf.js --spec test/specs/vertical-slice.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/untrusted-content.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/subtask-reorder.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/sandbox-verify.spec.js
npx wdio run test/wdio.conf.js --spec test/specs/filewatch-live-update.spec.js
```

Run each spec as its own `wdio run` invocation — batching them is unreliable
with this driver. Specs that mutate the fixture snapshot and restore it, so
the locked 3-task fixture stays byte-identical (see Fixture integrity below).

## Fixture integrity (tamper-evidence)

The fixed 3-task fixture in `fixture-vault/` must remain byte-identical to
the state locked by #370 for every screenshot/recording/test to remain
valid evidence. MD5s at time of evidence capture (`budget-q3-review.md` changed from an
earlier revision: its second subtask was `todo`, which left the fixture
without the `in_progress` state #370 requires and made the card's "Current:"
line render empty. Now `in_progress`, matching the locked fixture spec):

```
7d1100f3da795d367d825106f7b490d2  fixture-vault/budget-q3-review.md
d695b88a3101d5436ebc9d4d49eeddd9  fixture-vault/confirm-tailscale-acl.md
527e1aeeafd01da4c23b797be7e0545b  fixture-vault/renew-domain.md
```

## Framework-specific considerations (non-scoring notes)

Per the scorecard's Phase 2 non-scoring notes section, carried here for
#381's later reference:

- **Two-runtime operational cost**: Tauri pairs a Rust backend process with
  a system WebView frontend — two languages/runtimes in one app, versus a
  single-process C#/XAML app for Avalonia/Uno. This spike's bounded Rust
  Core (635 LOC) is the concrete cost of that split; a production port
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
