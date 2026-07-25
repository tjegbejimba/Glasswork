# Cross-platform prototype scorecard

Resolves Wayfinder ticket [Fix the cross-platform prototype scorecard](https://github.com/tjegbejimba/Glasswork/issues/376)
under map [Wayfinder: Choose the cross-platform Glasswork architecture](https://github.com/tjegbejimba/Glasswork/issues/369).

**Status: locked.** This rubric must be committed before any of the three
prototype spikes — [Prototype Tauri with a bounded Rust Core](https://github.com/tjegbejimba/Glasswork/issues/372),
[Prototype Avalonia with the existing C# Core](https://github.com/tjegbejimba/Glasswork/issues/377),
[Prototype Uno with the existing C# Core](https://github.com/tjegbejimba/Glasswork/issues/380) — are unblocked to
start, and stays immutable for the duration of all three spikes. Changing any
gate, weight, metric, or formula after a spike has started requires a fresh
Wayfinder ticket, not a silent edit to this file. This is the anti-gaming
control: nobody, including TJ, adjusts the rubric after seeing how a
framework performs.

Every spike scored against this rubric must implement the identical shared
vertical slice and fixture locked by
[Define the shared cross-platform vertical slice](https://github.com/tjegbejimba/Glasswork/issues/370)
(design-brief prototype:
[cross-platform-vertical-slice-prototype.html](https://github.com/tjegbejimba/Glasswork/blob/d8a6f734f2cbb62649fa2be26d312156512dad4f/docs/prototypes/cross-platform-vertical-slice-prototype.html)).
This scorecard does not redefine that slice — it only defines how the three
resulting spikes get compared.

An interactive calculator for applying this rubric once the three spikes are
built is at
[cross-platform-prototype-scorecard-calculator.html](../prototypes/cross-platform-prototype-scorecard-calculator.html).

## Why the score is 70% visual/interaction judgment, not a fully objective formula

TJ's explicit, considered call (2026-07-23), reached after rejecting a
fully-formulaic multi-category weighted design: the deciding input is his own
hands-on visual and interaction judgment across all three spikes on real
hardware. Objective measurements are real signal but intentionally
subordinate — they inform the call, they don't replace it. The rubric below
exists to make that judgment *reproducible and hard to unconsciously
re-litigate*, and to keep a firm floor under it (the safety gates) and a
secondary, harder-to-fudge check on it (the measured-evidence bucket) — not
to pretend the decision is more mechanical than it is.

## Phase 0 — Hard safety gates

Binary, disqualifying, checked before any scoring. Failing **any** gate
removes that framework from Phase 1 scoring entirely, regardless of how well
it would otherwise score. A disqualified framework can still be discussed
(see Phase 2 override), but only via a fresh ticket that reopens or retests
the failed gate — not by scoring around it.

Each gate has a locked acceptance script, run identically against all three
spikes:

1. **Full slice behavior.** Every required behavior from the shared vertical
   slice is present and functional against the fixed 3-task fixture: My Day
   list with both row forms (rich/card for active tasks, quiet/single-line
   for the low-priority tasks), Task Detail with working drag-reorder of the
   active subtask list, both required Artifact kinds rendering (one Markdown
   artifact through the shared renderer, one untrusted HTML artifact through
   sandboxed preview), the subtask hit-zone split (circle glyph toggles done,
   clicking the text opens detail, no double-click gesture anywhere), live
   file-watch parity on the "Confirm Tailscale ACL update" fixture task, the
   reserved Planner nav entry present with zero Planner content, and keyboard
   navigation reaching nav/rows/subtasks. Acceptance script: walk every item
   in this list against the running spike and mark pass/fail; **any** single
   failure fails this gate. No partial credit, no "mostly working."

2. **Genuine HTML sandbox.** The untrusted HTML artifact's sandboxed preview
   actually sandboxes. Acceptance script: the test HTML artifact contains a
   script that attempts one outbound network request (e.g. `fetch()` to a
   canary URL) and one attempt at parent-window/document access; open the
   preview and confirm via a network monitor / browser dev tools that neither
   succeeds. Passing the source-view fallback instead of the preview does not
   satisfy this gate — the sandboxed preview itself must exist and hold.

3. **Native file/Obsidian launching.** From Task Detail, the "open in
   Obsidian" (or equivalent native-launch) action succeeds on both macOS and
   Windows. Acceptance script: trigger it once per OS; both must open the
   correct file in the correct external application.

4. **Accessibility reachability.** Every interactive element in the slice —
   both nav rail items, both task-row forms' hit zones, the subtask
   circle-vs-text hit zones, the drag-reorder mechanism or its accessible
   keyboard/menu alternative, and the HTML preview's boundary control — is
   reachable by keyboard/assistive-tech focus and receives *some* spoken
   announcement from VoiceOver (macOS) and Narrator (Windows). Acceptance
   script: tab/swipe through every zone on both screen readers on both OSes;
   record pass/fail per zone. Imperfect or generic announcements are
   acceptable ("button" is fine); a zone that focus cannot reach at all is
   not. **Zero** totally-unreachable zones required to pass.

5. **A real automated test passes.** At least one automated desktop UI test
   passes, and it exercises a real fixture interaction (e.g. toggling the
   "Renew domain registration" subtask, or opening Task Detail) — not just an
   app-launch smoke test.

6. **No crash or hang.** Zero crashes and zero hangs (defined as the UI
   failing to respond to input for more than **10 seconds**) anywhere during
   acceptance-evidence capture on either OS.

## Phase 1 — Weighted score, gate survivors only

Combined score = `0.7 × Visual` + `0.3 × Measured`, each bucket on a 0–10
scale, giving a final 0–10.

### 70% — Visual/interaction judgment

Simple average of four 1–10 sub-ratings TJ gives each surviving framework
after hands-on use of all three spikes together (rating all three side by
side in one sitting per sub-criterion reduces anchoring/order bias versus
rating each framework in isolation on a different day):

1. **Platform-native chrome fidelity** — how well the spike matches the
   accepted Variant B design brief (native-style traffic lights and a subtle
   vibrancy-tinted title bar/nav rail on macOS; custom caption buttons and a
   Mica-like tinted title bar/nav rail on Windows) while keeping everything
   else — layout, chips, cards, spacing, color tokens, corner radii —
   identical across platforms.
2. **Shared layout/visual polish** — quality and consistency of the cards,
   chips, segmented progress bar, spacing, and corner radii, independent of
   platform chrome.
3. **Animation/transition smoothness** — drag-reorder motion, and
   toggle/hover/press feedback on rows and subtasks.
4. **Perceived interaction snappiness** — TJ's felt sense of how responsive
   clicks, navigation, and opening Task Detail feel. This is deliberately a
   subjective companion to (not a duplicate of) the stopwatch
   interaction-latency number in the measured bucket below — a framework can
   measure fast but feel janky (dropped frames, abrupt transitions), or
   measure a little slower but feel more fluid.

No sub-criterion outweighs another; average all four for the bucket score.

### 30% — Measured evidence

Average of 8 equally-weighted objective metrics (each normalized 0–10, see
Normalization below), **plus** a PWA-reuse bonus added after that average,
capped so the bucket never exceeds 10.

#### The 8 metrics

| # | Metric | Direction | Measurement procedure |
|---|---|---|---|
| 1 | Cold launch time | lower better | 3-run median, per [#370](https://github.com/tjegbejimba/Glasswork/issues/370)'s acceptance evidence: time from process launch to the My Day list being rendered and interactive. |
| 2 | Task-detail interaction latency | lower better | 3-run median, per #370: time from clicking a task row to Task Detail being fully rendered and interactive. |
| 3 | Idle resident memory | lower better | 3-run median, per #370: resident memory measured 30 seconds after launch with no further interaction. |
| 4 | Installed package size | lower better | Per #370: on-disk installed size, one measurement per OS (average the two if they differ; note both in the raw evidence). |
| 5 | File-watch response latency | lower better | 3-run median: time from saving the external frontmatter edit to the "Confirm Tailscale ACL update" fixture row updating on-screen, timestamped from the same recording already required as acceptance evidence. |
| 6 | Accessibility completeness | higher better | % of the required interactive hit zones (same list as Gate 4) that VoiceOver/Narrator reaches with a **correct** accessible name + role + state (not just "reachable" — Gate 4 already guarantees reachability; this scores quality), averaged across both OSes. |
| 7 | Testability breadth | higher better | Count of **distinct** fixture interactions (from a locked interaction catalog: toggle a subtask, open Task Detail, drag-reorder a subtask, trigger the HTML preview, trigger a live file-watch update, navigate via keyboard) covered by passing automated desktop UI tests. A test counts only if it performs one catalog action and asserts on its result — splitting one behavior into many trivial assertions does not inflate the count. |
| 8 | Implementation complexity | lower better | Total new lines of code in the framework-specific UI/presentation layer only (shared `Glasswork.Core` logic excluded), via `git diff --stat` / `cloc` against a locked include/exclude list (framework UI project(s) only; generated/scaffolded boilerplate the framework itself emits and never requires hand-editing is excluded — note any such exclusions explicitly in the raw evidence so they're auditable). |

#### Normalization: relative ranking among gate-surviving candidates

Each metric is scored by ranking the framework's raw value against the other
**gate-surviving** candidates in this spike round (not fixed absolute
thresholds) — the best of the survivors scores 10, the worst scores 0,
values in between are linearly interpolated by where they fall in that
range. Fewer than 3 survivors still gets ranked among however many remain; a
lone survivor's evidence still gets recorded but there is no "worst" to rank
against — treat it as a 10 on every metric it has valid evidence for
(nothing to compare down against), and rely on the visual bucket and
override discussion for the actual decision.

- **Direction-aware.** For "lower is better" metrics the smallest raw value
  scores 10; for "higher is better" metrics the largest raw value scores 10.
- **Equivalence / noise bands.** Differences too small to be meaningful score
  identically rather than triggering a full 10-vs-0 swing on measurement
  noise. Two values are considered tied if their relative difference is
  within: **10%** for cold launch, interaction latency, file-watch response,
  and implementation-complexity LOC; **5%** for idle memory and package size;
  and an **exact match** for accessibility completeness % and testability
  count (already coarse, discrete measures). Group values into clusters by
  this rule (sort, merge adjacent values within the band), then rank
  *clusters*: all members of the best cluster score 10, all members of the
  worst cluster score 0, a middle cluster's score is interpolated from its
  representative (mean) value's position between the best and worst
  cluster's representative values. If every surviving candidate lands in one
  cluster (no discriminating signal at all), every candidate scores **5** on
  that metric.
- **Invalid runs.** For the 3-run-median metrics, a run that fails to
  complete or capture cleanly is invalid and must be re-run — never averaged
  in partially. Apply this uniformly across all three frameworks.

Average the 8 metric scores (0–10) for the base measured score.

#### PWA-reuse bonus

Up to **+1.0** point, added after the 8-metric average, capped so the total
measured-evidence bucket never exceeds 10. Per the map's standing note ("PWA
UI-code reuse is a scored bonus, not a requirement"), this sits outside the 8
equally-weighted metrics rather than diluting them as a 9th equal slice.

To keep the bonus from resting on a loose self-estimate (a real risk: it can
swing up to 0.3 of the final combined score), it is computed from a **fixed
module inventory**, defined once before any spike starts, against a locked
PWA target (a Tailscale-only web frontend consuming the same
`Glasswork.Core`-exposed API the desktop spikes use):

- **Locked module list** (6 modules, one shared across all three spikes):
  My Day list rendering, Task card rendering, Task Detail shell, Subtask row
  rendering, Artifact-kind rendering/routing, Nav rail.
- Each spike classifies each of its 6 modules as **Reusable-as-is** (1.0),
  **Adaptable-with-changes** (0.5), or **Not-reusable** (0.0) against that
  locked PWA target, and records the classification with a one-line reason
  per module in its own resolution (auditable, not just a number).
- Reuse score = `(reusable × 1.0 + adaptable × 0.5) / 6`, then relative-ranked
  among the gate-surviving candidates the same way as the 8 metrics above
  (higher is better; best of survivors = full +1.0 bonus, worst = +0,
  interpolated between; ties within the module-count granularity score
  identically — no percentage noise band needed since classification is
  already coarse).
- **Missing evidence** for the PWA classification scores 0 for that
  candidate's bonus, same rule as below.

#### Missing evidence

If a spike fails to produce reproducible evidence for any of the 8 metrics or
the PWA classification, that metric (or the bonus) scores the **worst
possible value (0)** for that framework — no exclusion from the average, no
retest carve-out, no exception for a "shared harness failure." This is
deliberately the harshest option: it removes any incentive, however small, to
skip an inconvenient measurement or to litigate whose fault a missing
measurement was. The team either captures the evidence, or accepts the zero.

## Phase 2 — Decision

- The combined 70/30 score is **advisory**, not binding. TJ retains final
  authority to select a different gate-surviving framework than the
  top-scoring one, but any such override must be written out and justified
  in the resolution of the downstream decision ticket,
  [Select the cross-platform framework and Core direction](https://github.com/tjegbejimba/Glasswork/issues/381).
  An override may only choose among **gate survivors** — a framework that
  failed a Phase 0 gate cannot be selected without a fresh ticket that
  reopens or retests that gate; the override authority does not bypass the
  gates.
- **Ties.** Candidates within **0.10** of each other on the unrounded
  combined score (0–10 scale, before any display rounding) are treated as
  tied. Break the tie first by comparing the 30% measured-evidence bucket
  alone (more objective than the visual bucket); if still tied within 0.10
  there, it falls to TJ's override judgment.
- **Framework-specific considerations.** A non-scoring notes section travels
  alongside the score for each candidate — e.g. Tauri's capability-scoped
  security model and two-runtime operational cost, Uno's WinUI-migration
  code-sharing claim and its verification, Avalonia's single-process
  simplicity. These inform TJ's override judgment but are never counted in
  the 70/30 number.

## Evidence freeze process

Each spike's raw evidence — screenshots, the drag-reorder/file-watch
recording, VoiceOver/Narrator transcripts, all measured values, LOC counts,
and PWA module classifications — must be captured and linked from that
spike's own resolution **before** any value is entered into the calculator
or a combined score is computed or viewed for any framework. Nothing in the
raw evidence is edited after a preview score has been computed.

## Measurement environment freeze

Locked once, before any spike starts, and held identical across all three:

- Exact macOS version/build and exact Windows version/build.
- Same hardware model per OS across all three spikes.
- Release (not Debug) build configuration for every spike.
- The fixture Vault reset to an identical known state before each
  measurement run.
- Spike build/run order alternated across repeated measurement sessions to
  avoid systematic ordering bias (e.g. thermal state, disk cache warmth).
- "Cold launch" defined precisely: a reboot (or equivalent guaranteed
  cold-cache state) immediately precedes the first of the 3 runs.

## Formula summary

```
For each metric m in the 8 equally-weighted measured metrics:
  score(framework, m) = clusterRankedScore(framework, m)  // 0–10, see Normalization

measuredBase(framework) = mean(score(framework, m) for m in 1..8)
pwaBonus(framework)     = clusterRankedScore(framework, "pwa reuse") scaled to [0, 1.0]
measured(framework)     = min(10, measuredBase(framework) + pwaBonus(framework))

visual(framework) = mean(chromeFidelity, layoutPolish, animationSmoothness, snappiness)  // TJ's 1–10 ratings

combined(framework) = 0.7 × visual(framework) + 0.3 × measured(framework)

winner = argmax(combined) over gate-surviving frameworks,
         subject to TJ's advisory override (must be written & justified),
         with ties (within 0.10) broken by measured(framework) alone, then override.
```
