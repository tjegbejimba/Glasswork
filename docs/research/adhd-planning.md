# Research: ADHD-Aware Capacity Planning for Glasswork's Planner

**Status:** Research and product analysis only. No production code implemented,
no other file modified. Not medical advice; nothing here diagnoses or treats
ADHD — it summarizes public research to inform interaction design.

**Question:** Before defining "estimates" for a capacity-first Planner page fed
by My Day, what actually helps ADHD adults plan realistic days *without*
forcing unreliable duration guesses — and what is the smallest capacity model
that fits Glasswork's existing Task/Subtask/My Day architecture?

**Relationship to existing research:** `docs/research/day-planner.md` already
covers the *time-of-day / calendar* half of this problem (Microsoft Graph
feasibility, WinUI scheduler gaps, Operon/Sunsama/TaskNotes/Motion/Reclaim
comparators, the recommended staged rollout from "capacity cue" → "local
Planner" → "read-only ICS overlay" → "Graph"). This document does not repeat
that ground. It answers the *estimation* half that document deliberately left
open: what capacity unit to plan against when the user cannot reliably predict
duration.

---

## TL;DR — decision-oriented summary

1. **The core clinical picture is real but nuanced, not a slogan.** ADHD is
   linked to measurable time-perception/estimation impairment, but the
   strongest quantitative evidence is lifespan-wide and concentrated in time
   *discrimination/reproduction* paradigms; clean *adult* time-*estimation*
   evidence is explicitly described as "scarce" and "mixed" in the most
   recent adult-focused review (Mette, 2023). "Time blindness" is a popular,
   clinically descriptive shorthand — not a validated diagnostic construct,
   not in DSM-5-TR/ICD-11, and not independently measured by any named
   instrument. Use it (if at all) only as shorthand for the underlying
   measured constructs, never as a claimed diagnosis or a marketed "fix."
2. **The planning fallacy is a universal human bias (Kahneman & Tversky,
   1979), not an ADHD-specific one.** ADHD adults independently show
   positive-illusory-bias/optimism about their own performance (Springer,
   2023) and documented time-estimation difficulty, which plausibly compound
   the general planning fallacy — but no study directly measures "ADHD
   amplifies the planning fallacy" as its own effect. This matters: Glasswork
   should design for *everyone's* planning fallacy, with ADHD as a reason the
   guardrails must be low-friction and default-driven, not a reason to invent
   ADHD-only claims.
3. **Almost none of the popular ADHD productivity tactics have ADHD-specific
   trial evidence.** Chunking and reminders sit inside one well-designed adult
   ADHD CBT RCT (Safren et al., 2010) as bundled skills, not isolated
   variables. Visual timers have exactly one relevant peer-reviewed study, in
   children, with mixed results. Body doubling is coaching-practice folklore
   with only a 2024 self-report survey and a 2025 EEG pilot — no controlled
   trial. Ranges-vs-point-estimates, buffers, WIP limits, energy/context
   matching, and "now/not now" framing are clinical consensus or plausible
   extrapolations from general decision science — not ADHD-tested
   interventions. The report is explicit about this everywhere it applies;
   see Q2.
4. **Product precedent converges on the same escape hatch: never require an
   accurate number.** Every reviewed tool that takes estimation seriously
   gives the user a way out of typing a real duration — a default (Structured:
   15 min slider default; Llama Life: 25 min default), an imputed value
   (Amazing Marvin: 20 min/task, 45 min/project when unset), a genuinely
   unscheduled bucket (Tiimo "Anytime," Structured "Inbox"), or no duration
   concept at all (Goblin Tools' breakdown-only Magic ToDo). The two products
   that go further — Amazing Marvin and Sunsama — separately track **actual**
   time against **planned** time and quietly correct future defaults from
   history, rather than asking for a better guess up front.
5. **Recommendation:** Glasswork's Planner should plan by **coarse size
   buckets on Subtasks, with an always-available default bucket** (never
   block a keep/defer decision on having an estimate), summed against a
   **fixed daily capacity budget that meetings shrink automatically**, and
   rendered as the **proportional read-only timeline** the prototype already
   validated. Historical-actual learning and optional timers are legitimate
   v2+ ideas, explicitly deferred, not required for a useful v1. See Q5.

---

## Q1 — ADHD and time-related cognition: what does high-quality evidence say?

### 1. Prospective time estimation / time perception

**Evidence strength: meta-analytic and lifespan-wide, but the adult-specific
slice is explicitly described as scarce and mixed.**

- Metcalfe, McFeaters & Voyer (2023), *Developmental Neuropsychology*, is a
  systematic review and meta-analysis of time-perception deficits in ADHD
  across the lifespan (~824 aggregated effect sizes), reporting a moderate
  aggregate deficit (Hedges' g ≈ 0.688). Working memory significantly
  moderated the deficit in under-18s, but **not** in adults, where ADHD
  subtype was the significant moderator instead. DOI:
  [10.1080/87565641.2023.2293712](https://doi.org/10.1080/87565641.2023.2293712).
- A 2022 meta-analysis of ~55 studies (summarized by the Faraone-group site
  ADHDEvidence.org) found the most consistent, low-heterogeneity impairments
  in **time discrimination** and **time reproduction** paradigms, with
  smaller-but-significant effects for time **estimation** and **production**:
  [adhdevidence.org summary](https://www.adhdevidence.org/blog/time-blindness-found-to-be-a-consistent-feature-of-adhd).
- Mette (2023), *International Journal of Environmental Research and Public
  Health* 20(4):3098 — the key adult-specific caveat. This narrative review
  concludes the **adult** literature is "very scarce" and mixed: some studies
  find clear estimation/reproduction/management deficits, others find no
  clear association, and methodology/diagnostic protocols vary widely across
  studies. [PMC9962130](https://pmc.ncbi.nlm.nih.gov/articles/PMC9962130/).
- Sonuga-Barke's dual/triple-pathway models treat timing deficits as a third
  neuropsychological dimension of ADHD alongside inhibition and delay
  aversion (discussed in Mette 2023) — a theoretical framing, not a single
  measured effect.

**Takeaway for Glasswork:** the strongest signal is about *perceiving how
long something already took or is taking* (discrimination/reproduction), not
about *predicting a future duration* (production/estimation) — and the
prediction-specific adult evidence is the weakest link in the chain. This is
exactly the operation a Planner asks users to do, so the product should not
assume the meta-analytic effect size transfers cleanly to "TJ will mis-predict
subtask durations by X%." It should instead design around the honestly-known
fact: prediction is hard and unreliable for this population, without
overclaiming a specific mechanism or magnitude.

### 2. The planning fallacy: general bias, not an ADHD-specific one

**The planning fallacy is a universal human cognitive bias. Direct evidence
that ADHD specifically amplifies it (as its own measured effect) is weak —
support is inferential, not directly demonstrated.**

- Coined by Kahneman & Tversky (1979), *Intuitive Prediction: Biases and
  Corrective Procedures*, TIMS Studies in Management Science 12:313–327: the
  systematic tendency to underestimate time/cost/risk for one's own future
  tasks, documented in general (non-clinical) populations.
- Mechanism: reliance on the "inside view" (plan-specific optimistic
  scenarios) instead of the "outside view" (base rates from similar past
  tasks) — Kahneman & Lovallo (1993), *Timid Choices and Bold Forecasts*,
  Management Science 39(1):17–31; Lovallo & Kahneman (2003), *Delusions of
  Success*, Harvard Business Review.
- Classic empirical demonstration in general populations: Buehler, Griffin &
  Ross (1994), *Exploring the Planning Fallacy*, Journal of Personality and
  Social Psychology 67(3):366–381 (recommend verifying directly before
  quoting — not independently re-opened during this research pass).
- No study was found that directly measures "the planning fallacy" as an
  ADHD-vs-control construct. The honest chain is: ADHD adults show measured
  time-estimation difficulty (§1) and positive-illusory-bias/optimism about
  their own performance (§5) → which *plausibly* compounds the general
  planning fallacy, but this compounding has not been directly demonstrated.

**Product framing:** say "the planning fallacy is a universal bias that
affects everyone; ADHD is independently associated with time-estimation
difficulty and optimism about one's own performance, which plausibly make its
effects worse" — not "ADHD causes the planning fallacy" or "research shows
ADHD amplifies the planning fallacy."

### 3. Working memory / executive function and duration estimation & planning

**EF/working-memory deficits are empirically well established in ADHD; their
causal link specifically to *duration estimation* is a theoretical model,
partly empirically supported in children, less so in adults.**

- Barkley (1997), *Behavioral Inhibition, Sustained Attention, and Executive
  Functions*, Psychological Bulletin 121(1):65–94, and *ADHD and the Nature
  of Self-Control* (Guilford, 1997): reframes ADHD around a foundational
  behavioral-inhibition deficit that cascades into four executive functions,
  including **nonverbal working memory** — which Barkley explicitly frames as
  encompassing the internal "sense of time" and hindsight/forethought. This is
  the theoretical/consensus foundation for treating time-sense as a
  working-memory-dependent capacity in ADHD, not a single measured effect.
- Thomas E. Brown's clinical EF model (*Attention Deficit Disorder: The
  Unfocused Mind*, 2005) groups ADHD impairment into Activation, Focus,
  Effort, Emotion, Memory, and Action — "Activation" (organizing,
  prioritizing, starting) and "Memory" directly implicate planning and
  initiation. Also a conceptual/clinical model, not a single empirical
  measurement.
- The empirical bridge is the Metcalfe/Voyer (2023) finding above: working
  memory significantly moderated time-perception deficits in children but
  **not** in adults — meaning the WM→timing causal story is better supported
  in children than in adults. State this nuance explicitly rather than
  treating "WM causes bad time estimates" as settled for adults.
- CHADD (professional organization), [Executive Function
  Skills](https://chadd.org/about-adhd/executive-function-skills/), lists
  task initiation and working memory among the EF domains affected in ADHD —
  a professional-org summary, not itself a primary study.

### 4. Task initiation difficulty (distinct from time estimation)

**Task initiation is a recognized EF domain (Barkley's "activation" /
Brown's "Activation") that is conceptually distinct from time estimation and
should not be conflated with it in product language.**

- *Time estimation* is a perceptual/predictive error — misjudging how long
  something takes or how much time has passed.
- *Task initiation difficulty* is a volitional/activation failure — the
  person may know accurately what the task is and even want to do it, but
  cannot generate the "go" signal to start ("task paralysis"). These commonly
  co-occur in ADHD but are separate EF problems.
- The bulk of specific "task initiation in ADHD" material is
  clinical/coaching description grounded in Barkley's inhibition model and
  Brown's Activation cluster, rather than a dedicated validated experimental
  paradigm isolating task-initiation as its own measured construct. Treat
  this as clinical-consensus-grounded-in-EF-theory, not a specific effect
  size.

**Why this distinction matters for Glasswork:** a Planner that solves
*estimation* (how big is this subtask) does nothing for *initiation* (can I
start it at all). A capacity model that only reduces prediction friction may
leave a user with an accurate, realistic day plan they still cannot start.
The interview questions in Q5 explicitly probe which failure mode TJ
actually experiences.

### 5. Overcommitment / optimism bias in scheduling

**There is a real, adult-measured ADHD construct here — positive illusory
bias (PIB) — but it concerns self-perception of competence, not scheduling
behavior directly. "Overcommitment in scheduling" as its own construct is
inferred, not directly measured.**

- *Positive Illusory Bias and Self-Handicapping in Adults with ADHD* (2023),
  Journal of Psychopathology and Behavioral Assessment, DOI:
  [10.1007/s10862-023-10084-2](https://link.springer.com/article/10.1007/s10862-023-10084-2)
  — documents PIB (overestimating one's own performance/competence) in adult
  ADHD samples. PIB research is historically stronger in children; this is a
  more recent adult replication.
- Delay aversion (Sonuga-Barke) — preference for immediate over delayed
  outcomes — is empirically established in ADHD and plausibly contributes to
  last-minute/overcommitted scheduling patterns; see the Cambridge
  *Psychological Medicine* assessment of memory, delay aversion, timing, and
  inhibition in ADHD (paywalled abstract, cited in the consolidated source
  list).
- No study directly measures "saying yes to too much / overpacking a
  calendar" as an ADHD construct. This is a reasonable inference from PIB +
  time-estimation deficits + impulsivity, not a directly demonstrated effect.

### 6. "Time blindness" — origin and scientific status

**This is the single most important framing decision in this document: "time
blindness" is a popular, clinically descriptive term, not a validated
clinical construct.**

- It does not appear as a diagnostic criterion in DSM-5-TR or ICD-11, and
  there is no independently validated "time blindness scale" or dedicated
  instrument. What *is* measured are its component behaviors — time
  estimation, reproduction, production, and duration discrimination (the
  paradigms in §1).
- The term is widely credited to/popularized by Russell Barkley, associated
  with his self-regulation work and his related notion of "temporal myopia."
  Every source found attributing the exact coinage to Barkley is secondary
  (coaching sites, explainer pages); no primary Barkley publication using the
  literal phrase was independently verified during this research pass.
  Treat "coined by Barkley" as plausible but not primary-verified; "popularized
  by / associated with Barkley" is the safer claim.
- Nonprofit/professional explainer: Understood.org, [ADHD and Time
  Blindness](https://www.understood.org/en/articles/adhd-time-blindness) —
  descriptive, not primary evidence.

**Guidance for Glasswork's copy and this report:** use "time blindness," if
at all, only as shorthand for the *cluster* of measurable time-estimation/
perception difficulties described in §1 — never as a diagnosis, never
claiming a dedicated "time blindness test," and never citing "studies on
time blindness" (cite the underlying paradigm studies instead). Given the
adult evidence is explicitly "scarce and mixed" (Mette, 2023), Glasswork
should also avoid any UI copy implying the app *measures* or *fixes* time
blindness.

---

## Q2 — Behavioral/environmental supports: evidence vs. consensus vs. anecdote

Each item is explicitly rated. Ratings, from strongest to weakest:
**[Empirical — ADHD sample]** → **[Empirical — general population, extrapolated to ADHD]**
→ **[Clinical consensus / expert opinion]** → **[Anecdotal / popular, no credible evidence found]**.

| Support | Rating | Evidence basis |
|---|---|---|
| **Externalizing time** (visible/physical vs. mental tracking) | Clinical consensus, indirect empirical support | Core Barkley-model recommendation; embedded (not isolated) inside RCT-tested CBT (Safren 2010) |
| **Visual timers** (e.g., Time Timer) | Empirical, but thin and pediatric | One relevant peer-reviewed study — children 7–9, reduced anticipatory anxiety/inattentive behavior but no significant effect on task performance, high individual variability. No adult ADHD RCT found. [MDPI 15(12):243](https://www.mdpi.com/2254-9625/15/12/243) |
| **Implementation intentions** ("if-then" plans) | Strong general empirical; ADHD-specific evidence is strong in **children**, sparse in adults | Gollwitzer (1999), *American Psychologist* 54(7):493–503; Gollwitzer & Sheeran (2006) meta-analysis, 94 studies, d≈0.65; ADHD-child studies: Gawrilow & Gollwitzer (2008), Gawrilow et al. (2011, 2013). Adult-ADHD RCT evidence is sparse — extrapolated, not directly tested. |
| **Task chunking** | Empirical, but bundled inside a multi-component package, not isolated | Explicit taught skill inside Safren et al. (2010) JAMA RCT (N=86 adults with residual ADHD symptoms on medication; CBT superior to relaxation+support, gains held to 12 months). Independent effect of chunking alone not isolated. |
| **Estimating from past actuals / historical data** | General empirical (reference-class forecasting); not ADHD-specific | Flyvbjerg (2006), *From Nobel Prize to Project Management*; formalizes Kahneman's "outside view." No ADHD-specific test exists — a reasonable extrapolation, not an ADHD-validated intervention. |
| **Ranges/confidence intervals instead of point estimates** | Anecdotal/popular design heuristic; no primary study (general or ADHD) found | Related in spirit to interval forecasting in the Kahneman tradition, but no direct trial located either in general or ADHD populations. State as untested-but-plausible. |
| **Buffers / padding estimates** | Clinical consensus; no dedicated trial | Logical corrective for documented underestimation; not independently tested as a protocol. |
| **Limiting work-in-progress / concurrent tasks** | Clinical consensus / anecdotal for ADHD specifically; empirical anchor is task-switching cost literature | Task-switching/set-shifting costs are larger in ADHD adults in the general literature, which implies fewer concurrent switches helps — but "limit WIP" itself has no ADHD trial. |
| **Body doubling / accountability partners** | Anecdotal/popular; emerging research only | Coaching-practice origin (attributed to Linda Anderson, ~1996, via secondary sources only). A 2024 self-report survey (~220 neurodivergent people) and a 2025 EEG pilot are the first attempts at objective study — no controlled efficacy trial exists. The "accountability" element overlaps with the RCT-tested "involve a significant other" module in Safren CBT. |
| **Reminders (external cueing)** | Empirical, bundled inside RCT-tested CBT | Explicit skill in Safren (2010); grounded in documented prospective-memory impairment in ADHD. Not isolated as its own variable. |
| **Transition time between tasks** | Deficit empirical (switch costs); intervention itself is clinical consensus | Enlarged switch costs are documented in ADHD adults generally; scheduling slack around transitions is a consensus recommendation, not separately trialed. |
| **Energy/context matching** (task type ↔ energy/time of day) | Clinical consensus / anecdotal; no ADHD-specific efficacy study | General circadian/chronotype literature plus ADHD's association with delayed circadian phase (noted in Mette 2023) gives indirect plausibility only. |
| **"Now / not now" binary framing** | Theoretical/consensus, derived from Barkley's temporal-myopia model | Not an empirically tested UI/scheduling intervention. Accurate to describe as "aligned with Barkley's model"; inaccurate to claim it is "evidence-based to outperform prioritization schemes." |

**Cross-cutting honesty summary.** Best-supported: lifespan time-perception
deficits, implementation intentions (general + ADHD-child), Safren's
multi-component adult CBT package (chunking/reminders bundled), positive
illusory bias, general task-switch costs. Weakly/indirectly supported
(consensus or extrapolation only): externalizing time, buffers, WIP limits,
transition buffers, energy matching, now/not-now framing,
ranges-vs-points, historical/reference-class estimating (general, not
ADHD-tested). Handle with particular care in copy: "time blindness" (§1.6)
and "body doubling" (coaching origin, preliminary evidence only).

Two clinical guidelines were checked directly for relevance: **NICE NG87**
(*Attention deficit hyperactivity disorder: diagnosis and management*,
[nice.org.uk/guidance/ng87](https://www.nice.org.uk/guidance/ng87)) recommends
ADHD-adapted CBT — including time-management/organization skills — for adults
with residual impairment on medication, consistent with Safren's approach.
**CADDRA** (*Canadian ADHD Practice Guidelines*, 4.1 ed.,
[caddra.ca](https://www.caddra.ca/canadian-adhd-practice-guidelines/)) lists
psychosocial supports (routines, planners, reminders, task-breakdown, CBT) in
its practice recommendations; a related peer-reviewed review from a CADDRA
work group — *Psychosocial Interventions for ADHD* (2022), Brain Sciences
12(8):1023, [MDPI](https://www.mdpi.com/2076-3425/12/8/1023) — covers CBT and
organizational-skills training in adults. AAP guidance is pediatric-focused
and does not directly address adult time-management interventions; it was not
a useful primary source for this question.

---

## Q3 — Product precedent: concrete interaction patterns (first-party docs only)

Sourced from official help centers, docs, and (for the one open-source
example) the actual repository — not marketing listicles. Every claim below
traces to a specific first-party URL or repo file.

### Tiimo (tiimoapp.com)

- **Duration model:** exact per-task time, or the task can be marked
  **"Anytime"** with no fixed time — explicitly framed as the flexibility
  escape hatch: *"Tasks appear on your timeline with a time or as
  'Anytime'... Try 'Anytime' tasks if you need flexibility."* The AI
  "Co-planner" suggests structured tasks with times. ([Tasks and
  to-dos](https://www.tiimoapp.com/faq/manage-tasks), no visible article date)
- **Capacity guidance is advisory, not mechanical:** *"Only schedule what
  feels realistic: a couple of tasks per day is enough"* and *"it's okay to
  let go of what no longer fits."* No documented over-capacity warning or
  auto-rollover.
- **Focus timer** starts automatically for scheduled tasks; **+1** adds a
  minute; *"Finished early? Drag the timer to the end to check it off."* No
  actual-vs-estimated log. ([Focus
  timer](https://www.tiimoapp.com/faq/focus-timer))
- The proportional-block-height claim commonly seen in marketing is **not**
  stated verbatim in the fetched help articles — flagged as unverified in
  first-party docs.

### Structured (structured.app)

- **Duration model:** exact duration via a slider, **defaulting to 15
  minutes**, adjustable, with a documented trick for genuinely open items:
  *"set the duration to 1 minute"* to manually control start/end without
  being duration-driven. Tasks without a time/date go to an **Inbox**.
  ([Create, Edit & Delete
  Tasks](https://help.structured.app/en/articles/338050), no visible date)
- **Visual proportional timeline** is the product's core metaphor — duration
  drives block size on the day timeline.
- **Focus Timer** counts down to task end; Pro **Intervals** (Pomodoro ≥45
  min remaining, Deep Focus ≥90 min remaining) recalculate on skip. No
  estimate-vs-actual logging documented. ([Focus
  Timer](https://help.structured.app/en/articles/331010))

### Sunsama (help.sunsama.com) — capacity-modeling angle (new, beyond existing research)

- **Two tracked values per task: planned time and actual time.** *"If you
  complete the task without adding actual time, the planned time will count
  as actual time if you've enabled that setting."* ([Planned and Actual
  Times](https://help.sunsama.com/docs/usage-guides/tasks/planned-and-actual-times/),
  no visible date)
- **Workload counter** cycles Total / Work / **Actual vs. planned** views per
  day — an explicit estimate-vs-reality surface, not a proportional timeline.
- **Over-capacity signal is a colored threshold, not a hard block:** the
  counter turns **yellow** approaching the configured workload threshold and
  **red** when planned time exceeds it.
- Multi-day rollover shows per-day actual/planned alongside cumulative
  task-card totals — relevant precedent for avoiding double-counting a task
  that spans days.

### Amazing Marvin (amazingmarvin.com)

- **Imputed defaults when no estimate exists — the most directly reusable
  pattern found:** *"Tasks without the duration estimate are worth 20
  minutes while projects are worth 45 minutes... Time tracking data will be
  used if available, else time estimates, else a rough estimate."* ([Day
  Planning](https://help.amazingmarvin.com/en/articles/5066364-day-planning),
  modified 2026-01-25)
- **Historical average baseline:** shown alongside today's planned total —
  "average duration of completed tasks over the last 30 days" — plus an
  emoji capacity gauge referencing "Christina's research on how many hours
  of focused work a person can do per day" (framed at roughly 3–4 hours of
  quality focus).
- **Events reduce available work hours** — the same meeting-shrinks-capacity
  pattern recommended in `docs/research/day-planner.md`.
- **Energy indicators** (Focus/Energy/Physical) allow energy-matched task
  selection, independent of duration.
- Marketing page explicitly names the anti-pattern Glasswork should avoid:
  *"You overestimate your capacity... you're 'failing' to complete your plan
  every single day"* — framed as the problem an estimate-vs-actual + rollover
  system solves. ([Time
  Estimates](https://amazingmarvin.com/features/time-estimates/))

### Goblin Tools — Magic ToDo (goblin.tools)

- **No duration concept at all.** Magic ToDo's entire function is task
  **breakdown**, controlled by a "spiciness" slider that produces more or
  fewer granular subtasks. Duration estimation lives in a separate sibling
  tool ("Estimator"), not in the breakdown flow. Explicit accuracy caveat:
  *"Nothing returned by any of the tools should be taken as a statement of
  truth, only guesswork."* ([About](https://goblin.tools/About))
- **Relevance to Glasswork:** this is the reference pattern for
  "decomposition instead of estimation" — useful for making a Subtask
  startable, but orthogonal to capacity planning.

### Llama Life (llamalife.co)

- **Duration is optional with a graceful default:** *"add a duration in
  minutes. If no duration is added, it will default to 25 min. (TIP: If this
  is challenging, try starting with shorter timers eg 5 min, and breaking big
  tasks into smaller ones.)"* ([Getting
  started](https://intercom.help/llama-life/en/articles/6453017-getting-started-with-llama-life),
  modified 2026-03-24)
- Single-active-task list + countdown timer model — no proportional day
  timeline. The default duration is the entire "unknown duration" answer:
  the user is never blocked from starting.
- No documented rollover, auto-carry-forward, or actual-vs-estimated
  "vibe check" in first-party help docs (these appear only in
  marketing/third-party material and are not asserted here as verified
  product behavior).

### Motion & Reclaim

Not re-researched in depth here; `docs/research/day-planner.md` already
documents their algorithmic auto-scheduling around durations and working
hours, which remains an explicit non-goal for Glasswork (see Q5).

### Open-source example: `nicky-mc/spoonful` ("Spoonful Planner")

A small React/Supabase/Clerk hobby project implementing Spoon Theory
(Christine Miserandino) as a day-planning mechanic — the clearest
open-source precedent for **energy-bucket + fixed token-budget** planning
without duration estimates:

- **Coarse energy-tier buckets** instead of durations: tasks are tagged
  `tier_1` / `tier_2` / `tier_3` (`TaskManager.jsx:8-10, 74-79`).
- **Fixed daily "spoon" token budget:** balance starts at 10
  (`useState(10)`); completing a task subtracts its spoon cost
  (`TaskManager.jsx:9, 37-40, 81`).
- **Low-shame recharge, not punitive rollover:** separate "Regeneration
  Tasks" *add* spoons back on completion (`TaskManager.jsx:42-46, 97-106`) —
  a self-care/recovery mechanic rather than carrying a debt forward.
- Caveats: small hobby project, hard-coded budget/tier counts, free-form
  (not bucketed) spoon cost per task, no visual proportional timeline, no
  WIP cap. Useful as a concrete existence proof of the pattern, not as a
  polished reference implementation.

### Comparison table

| Product | Capacity model | Visual proportional timeline? | Unknown-duration handling | Rollover / low-shame recovery | Actual-vs-estimated learning |
|---|---|---|---|---|---|
| Tiimo | Exact time; no capacity total | Timeline (proportionality unverified in help docs) | "Anytime" tasks | No auto-rollover; soft advice only | No |
| Structured | Exact duration, slider default 15 min | Yes — core metaphor | Inbox (no time) / 1-min trick | Not documented | No |
| Sunsama | Planned time → workload counter + threshold (yellow/red) | No (numeric readout, not a grid) | Planned time optional | Per-day vs. cumulative rollover shown | **Yes** — explicit actual-vs-planned |
| Amazing Marvin | Estimate + imputed defaults (20/45 min) + 30-day average + focus-hours cap | No (planning header + emoji gauge) | **Imputed default minutes** | Auto-carry-forward (marketing-named anti-pattern) | **Yes** — tracking else estimate else rough |
| Goblin Tools | None — breakdown only | No | n/a | None | None |
| Llama Life | Optional duration, default 25 min | No (single-task list) | **25-min default** | Not documented first-party | Not documented first-party |
| `spoonful` (OSS) | Energy tiers + fixed spoon budget | No | n/a (no duration concept) | **Regeneration tasks recharge** | No |

**Cross-product convergence relevant to Glasswork:** every serious tool
either (a) supplies a default/imputed duration so an estimate is never
mandatory, or (b) abandons duration entirely in favor of a coarser unit
(spoons/tiers). None of the reviewed tools force a blocking numeric-minutes
prompt with no escape hatch. The proportional timeline and the
imputed-default/historical-learning pattern are the two most transferable
ideas; algorithmic auto-scheduling (Motion/Reclaim) is the pattern to
explicitly avoid, consistent with `docs/research/day-planner.md`.

---

## Q4 — Capacity models compared for Glasswork

Evaluated against: friction to use when duration is genuinely unknown,
fit with the evidence in Q1/Q2, fit with product precedent in Q3, and fit
with Glasswork's existing Task/Subtask/My Day/UI-State architecture.

| Model | What it asks of the user | Evidence/precedent fit | Fit for Glasswork |
|---|---|---|---|
| **Exact manual duration estimates** | A specific number of minutes per Subtask | Directly stresses the weakest-evidenced skill (adult time *estimation*, Mette 2023 "scarce and mixed") and invites the general planning fallacy (Q1 §2) with no correction built in | Poor as the primary/only model — highest friction, least evidence support |
| **Coarse size buckets** (S/M/L or 15/30/60/90+) | A categorical judgment, not a number | No direct ADHD RCT, but plausible extrapolation from chunking's role inside Safren's positive RCT (reduces the cognitive load of precision-seeking); Goblin Tools' "spiciness" shows categorical-slider judgments are an accepted lower-friction pattern elsewhere | **Strong fit** — cheap to answer, cheap to default, maps directly onto a Subtask field |
| **Ranges with uncertainty** | Two numbers (a low and high bound) | No primary study found, general or ADHD-specific (Q2) — an unvalidated design heuristic | Weak fit — more input burden than buckets for no demonstrated benefit; do not prioritize |
| **Historical actuals learned from completed similar Subtasks** | Nothing, once seeded — the system infers | General strong evidence for reference-class/outside-view forecasting (Flyvbjerg 2006) correcting optimism bias, not ADHD-specific; strongest live precedent is Amazing Marvin's 30-day average and Sunsama's actual-vs-planned | **Strong fit as a v2 layer** — but has a cold-start problem (no history on day one) and a bootstrapping problem in a single-user local vault (no cross-user data). Best treated as an enhancement over buckets, not a replacement. |
| **Fixed daily slot/token budgets** | Nothing per item — capacity is a whole-day resource | Directly targets overcommitment/positive-illusory-bias evidence (Q1 §5) without requiring the user to predict anything; validated pattern in Amazing Marvin's imputed-total framing and the `spoonful` OSS spoon-budget model | **Strong fit** — pairs naturally with meeting-driven capacity shrinkage already scoped in `docs/research/day-planner.md` |
| **Count/WIP caps** | Nothing — a cap on number of kept items | Consensus-level support via general task-switching-cost literature (not ADHD-specific-trial-tested); simplest possible mental model | **Good fallback / v0 fit** — zero estimation burden at all, but coarser signal than a bucket-based budget (a cap of "5 items" doesn't distinguish five 10-minute items from five 90-minute items) |
| **Planning by available gaps / "fits or doesn't fit"** | A binary judgment per item, evaluated against remaining visible capacity | Matches Barkley's "now/not now" temporal framing (theoretical, not tested) and is the mechanism the disposable prototype already validated (keep/defer + proportional timeline) | **Strong fit** — this is the actual interaction Glasswork will render; buckets/budgets are what feed the judgment, not a competing model |
| **Optional timer/actual tracking** | Nothing required; opt-in retrospective logging | Retrospective (not predictive) logging avoids the estimation problem entirely and is the on-ramp to historical learning; precedent in Sunsama's task timer and Structured/Tiimo/Llama Life focus timers | **Good v2 fit, opt-in only** — never require it; it is the data-collection mechanism that eventually powers historical actuals |
| **Hybrid** (buckets + fixed daily budget + gap-fit judgment, with optional timer feeding future historical defaults) | Coarse categorical input, capacity awareness at the day level, a binary keep/defer decision | Synthesizes the best-evidenced/best-precedented pieces of every other row without inheriting their individual weaknesses | **Recommended** — see Q5 |

---

## Q5 — Recommendation: the smallest useful, low-friction model

### The model

1. **Unit of planning = the Subtask**, matching Glasswork's existing "Today's
   subtasks" concept (ADR 0008) — the atomic thing a user actually does in a
   session. Tasks/PBIs are not separately estimated; see double-counting
   rule below.
2. **Coarse size bucket, not a duration field.** A small fixed set — e.g.
   `S` / `M` / `L` / `XL` (or minute-anchored equivalents: 15 / 30 / 60 /
   90+) — chosen once per Subtask, with **no numeric-minutes entry surface
   in v1.** This is deliberately the lowest-precision unit product precedent
   supports (Q3/Q4), avoiding the exact-estimate friction point the evidence
   says is genuinely hard (Q1 §1, §2).
3. **Unknown duration is never a blocker.** Every Subtask that has no chosen
   bucket defaults to a fixed size (e.g. `M`) automatically — mirroring
   Amazing Marvin's imputed 20-minute/45-minute defaults and Llama Life's
   25-minute default (Q3). A keep/defer decision must never wait on the user
   picking a bucket; the default exists so the Planner always has *some*
   number to render with, and the user can override it later or never.
4. **Fixed daily capacity budget**, expressed in the same bucket unit (e.g.
   "6 buckets of headroom today" or a minutes-equivalent total), consistent
   with the fixed-slot/token-budget row in Q4. This directly targets the
   overcommitment/positive-illusory-bias evidence (Q1 §5) without asking the
   user to do arithmetic or predict anything.
5. **Meetings shrink the budget automatically**, reusing the read-only
   ICS/Graph capacity approach already scoped in `docs/research/day-planner.md`
   ("Events reduce the amount of available work hours" — the same pattern
   documented in Amazing Marvin, Q3). This is additive to that document, not
   a redesign of it.
6. **The proportional read-only timeline is a rendering, not an input
   surface.** Kept Subtasks draw blocks sized by their bucket (defaulted or
   chosen) in keep-order; meetings draw their real blocks; nothing is
   dragged, resized, or clock-time-typed by the user. This matches the
   disposable prototype's validated winner and keeps the surface strictly
   read-only, consistent with Glasswork's existing read-only-artifact
   philosophy (CONTEXT.md §2) rather than becoming a second editable
   calendar.
7. **Historical actuals and optional timers are explicitly deferred**, not
   part of v1. If validation in Stage 2/3 below shows buckets alone
   under-correct for chronic underestimation, add an *opt-in* timer
   (Sunsama/Structured/Llama Life pattern) purely to accumulate actuals, and
   only later let those actuals nudge future default bucket sizes (Amazing
   Marvin's "time tracking else estimate else rough estimate" cascade). Never
   require the timer; never surface "accuracy" as a score.

### Avoiding double-counting between parent Tasks and Subtasks

Glasswork's Task Model already has a "container-only host" rule for PBIs —
a PBI is pulled into a view to host its in-My-Day children without
independently counting itself as "in My Day" (ADR 0016, ADR 0017). Capacity
accounting should follow the identical shape:

- **Leaf Tasks with Subtasks:** only the kept Subtasks' buckets count toward
  the day's capacity total. The parent Task itself never separately
  contributes a size — it is a grouping label, not an additional unit of
  work, exactly as a PBI is a grouping label and not itself "in My Day"
  (ADR 0017 §"container-only host").
- **Quiet/leaf Tasks with no Subtasks:** the Task itself is the atomic unit
  and carries its own bucket (defaulted if unset) — this is the direct
  Subtask-equivalent case for tasks that never got broken down.
- **PBIs (containers):** never contribute their own size under any
  circumstance; only their **Today's children** (ADR 0017) that are
  themselves kept for today contribute buckets, mirroring the existing rule
  that a PBI never self-promotes to My Day on its own due date.
- This keeps capacity accounting a pure function over the same "Today's
  subtasks" / "Today's children" collections My Day already computes — no
  new aggregation concept, just a new field (bucket size) summed over an
  existing collection.

### Language that avoids shame or false precision

- Call it a **"size"** or **bucket**, not an "estimate" — "estimate" implies
  a prediction the user is expected to get right; "size" is a coarse,
  low-stakes categorical judgment closer to Goblin Tools' "spiciness" framing.
- Frame the daily total as **"capacity" or "headroom"**, never "productivity"
  or "workload you must clear" — capacity is a constraint to plan around, not
  a target to hit.
- When something doesn't fit or doesn't get done, use **"not today" /
  "carried" / "still open"** — never "missed," "failed," "overdue" (in this
  context), or any framing that implies the user did something wrong. This
  is a direct, deliberate contrast with Amazing Marvin's own marketing
  language naming "you're 'failing' to complete your plan every single day"
  as the anti-pattern (Q3) — Glasswork should describe the same overcommitment
  problem without adopting failure language to describe its symptom.
- Avoid "time blindness" and "accuracy" in user-facing copy given the
  evidence caveats in Q1 §6 and the deliberate absence of any
  accuracy-scoring feature (see non-goals below). If a historical-learning
  feature ships later, describe it as the app "getting to know your pace,"
  not "correcting your errors" or "tracking your accuracy."

### Explicit non-goals

- **No algorithmic auto-scheduling or conflict optimization** (the
  Motion/Reclaim pattern) — already a non-goal in `docs/research/day-planner.md`
  and reaffirmed here for the estimation layer: buckets feed a keep/defer
  judgment and a read-only render, never an automatic placement engine.
- **No mandatory numeric-minutes input anywhere in v1.** If a future power-user
  affordance adds exact minutes, it must remain optional and never gate the
  keep/defer flow.
- **No "estimation accuracy" score, streak, or leaderboard.** The evidence
  base for ADHD-specific optimism bias (Q1 §5) is a reason to remove
  pressure, not to gamify correction.
- **No range/confidence-interval input in v1** — Q4 found no evidence
  benefit over buckets, and it adds input burden.
- **No draggable/resizable timeline** — the timeline stays read-only,
  consistent with Glasswork's broader "Artifacts are read-only" philosophy
  and the day-planner.md non-goal against becoming a calendar client.
- **No diagnosis-adjacent copy** — no claims of measuring, diagnosing, or
  treating "time blindness" or ADHD generally; the app manages tasks, it does
  not manage symptoms.

### Staged validation plan

1. **Stage 0 — Interview TJ** (see questions below) before building
   anything, to find out whether the actual friction is the *number* itself,
   the *emotional weight of committing*, or something else entirely (e.g.,
   task initiation, per Q1 §4, which no capacity model fixes).
2. **Stage 1 — Cheapest prototype:** coarse buckets (with default) +
   keep/defer + proportional read-only timeline, **no fixed daily budget,
   no meetings, no history.** Test whether seeing proportional block sizes
   alone changes what TJ chooses to keep.
3. **Stage 2 — Add the fixed daily capacity budget**, reduced by real
   meetings (reusing the day-planner.md ICS-first approach). Test whether a
   visible over-capacity signal (a Sunsama-style yellow/red, not a hard
   block) changes keep/defer decisions differently than Stage 1 alone.
4. **Stage 3 — Add an opt-in timer**, purely for logging actuals, with zero
   scoring/accuracy framing. Test appetite before building anything that
   *uses* the logged data.
5. **Stage 4 (only if Stages 1–3 show buckets alone under-correct) —
   Historical-actual learning:** let logged actuals nudge future default
   bucket sizes per Subtask-type/tag, following Amazing Marvin's cascade
   ("tracking data else estimate else rough estimate"). This is the only
   stage that requires meaningful new engineering (an actuals store keyed
   by some notion of "similar" subtasks) — defer it until the cheaper stages
   prove insufficient.

### Interview / prototype questions for TJ

- When My Day feels unrealistic, what's the first thing you notice — too
  many items on the list, or the wrong items on the list?
- Have you ever tried giving a task a duration anywhere (Outlook, a to-do
  app, a calendar block)? What actually happened — did you ignore it, guess
  low on purpose, or avoid entering a number at all?
- If Glasswork asked for "small / medium / large" instead of a number of
  minutes, would that feel meaningfully easier, or like the same problem
  wearing a different costume?
- Would you rather cap your day by a *number of items* you're allowed to
  keep, or by a *rough time budget* the items have to fit inside?
- What does a "bad" planning day actually look like for you — did you
  schedule too much, or could you not start what was already scheduled?
  (This distinguishes overcommitment from task-initiation difficulty —
  Q1 §4 — which this Planner model does not address.)
- If a subtask runs long or short, do you want Glasswork to quietly
  remember that for next time, or would that feel like being tracked or
  judged?
- When something doesn't get done today, what wording would feel honest
  without feeling like failure — "carried over," "still open," "not today,"
  something else?
- Would meetings automatically shrinking your available capacity feel
  helpful, or like one more thing being decided for you without asking?
- Looking at the disposable prototype's proportional timeline: does seeing
  blocks sized by bucket (not real minutes) feel informative or feel falsely
  precise?

---

## Q6 — Candidate `UBIQUITOUS_LANGUAGE.md` terms (assessment only — file not edited)

The following concepts surfaced by this research would need canonical
definitions in `UBIQUITOUS_LANGUAGE.md` **if and when** the Planner is
actually built (per that file's own rule: "If a new concept emerges, add it
here in the same PR that introduces it"). None are added now — this is a
research artifact, and per this task's boundaries, no other file is modified.

| Candidate term | Why it would need a definition | Notes |
|---|---|---|
| **Planner** | Already used informally in `docs/research/day-planner.md` as the proposed page name, alongside existing pages (My Day, Backlog, Work Log, Settings) | Should be formalized only when/if the page ships, per the existing "Page" entry's pattern |
| **Size bucket** (or **Bucket**) | The coarse duration proxy on a Subtask (S/M/L/XL or equivalent) | Needs to be clearly distinguished from "estimate" in the glossary itself — the term choice *is* the shame-avoidance mechanism from Q5, so getting the definition's wording right matters |
| **Daily capacity** (or **Capacity budget** / **Headroom**) | The day's total planning budget after meetings are subtracted | Should explicitly cross-reference how it composes with the "In My Day today" rule and PBI container-only-host rule, to keep the double-counting rule (Q5) discoverable |
| **Keep / defer** | The binary decision verb pair driving the prototype's core interaction | Should be checked against whatever verbs the actual disposable prototype used, to avoid introducing a synonym UBIQUITOUS_LANGUAGE.md would then have to reject |
| **Proportional day timeline** | The read-only rendering surface fed by buckets + meetings | Needs an explicit "Aliases to avoid" entry ruling out "calendar" or "schedule," to preserve the read-only/non-editable boundary this research recommends |
| **Actual time** / **Historical actual** | Only relevant if Stage 3/4 (Q5) ships | Do not add speculatively; add only in the PR that introduces opt-in timing, per the file's stated rule |

**Do not add as a domain term:** *time blindness*. Per Q1 §6, it is not a
validated construct and should not appear in Glasswork's product vocabulary,
UI copy, or glossary — the underlying measurable behaviors (estimation,
buckets, capacity) are the actual domain concepts worth naming.

---

## Consolidated source list

### Clinical / academic (Q1, Q2)

1. Metcalfe KB, McFeaters CD, Voyer D (2023). *Time-Perception Deficits in
   ADHD: A Systematic Review and Meta-Analysis.* Developmental
   Neuropsychology. DOI:
   [10.1080/87565641.2023.2293712](https://doi.org/10.1080/87565641.2023.2293712).
   Peer-reviewed meta-analysis, lifespan sample, g≈0.688.
2. Mette C (2023). *Time Perception in Adult ADHD: Findings from a
   Decade — A Review.* International Journal of Environmental Research and
   Public Health 20(4):3098.
   [PMC9962130](https://pmc.ncbi.nlm.nih.gov/articles/PMC9962130/).
   Peer-reviewed narrative review, adult-focused; describes evidence as
   scarce/mixed.
3. ADHDEvidence.org (Faraone research group). Blog summary of a 2022,
   ~55-study time-perception meta-analysis.
   [Link](https://www.adhdevidence.org/blog/time-blindness-found-to-be-a-consistent-feature-of-adhd).
   Professional-org/researcher blog; trace to primary before quoting figures.
4. Kahneman D, Tversky A (1979). *Intuitive Prediction: Biases and
   Corrective Procedures.* TIMS Studies in Management Science 12:313–327.
   Original theory — the planning fallacy.
5. Kahneman D, Lovallo D (1993). *Timid Choices and Bold Forecasts: A
   Cognitive Perspective on Risk Taking.* Management Science 39(1):17–31.
   Peer-reviewed — the "outside view."
6. Lovallo D, Kahneman D (2003). *Delusions of Success.* Harvard Business
   Review. Practitioner/peer-adjacent.
7. Buehler R, Griffin D, Ross M (1994). *Exploring the "Planning Fallacy":
   Why People Underestimate Their Task Completion Times.* Journal of
   Personality and Social Psychology 67(3):366–381. Peer-reviewed, general
   population; verify directly before quoting.
8. Barkley RA (1997). *Behavioral Inhibition, Sustained Attention, and
   Executive Functions: Constructing a Unifying Theory of ADHD.*
   Psychological Bulletin 121(1):65–94; and *ADHD and the Nature of
   Self-Control* (Guilford, 1997). Theoretical model — EF, time,
   "temporal myopia."
9. Brown TE (2005). *Attention Deficit Disorder: The Unfocused Mind in
   Children and Adults.* Yale University Press. Clinical EF model.
10. CHADD. *Executive Function Skills.*
    [chadd.org/about-adhd/executive-function-skills](https://chadd.org/about-adhd/executive-function-skills/).
    Professional organization page.
11. *A comprehensive assessment of memory, delay aversion, timing,
    inhibition, decision-making and variability in ADHD: advancing beyond
    the three-pathway models.* Psychological Medicine (Cambridge).
    [Link](https://www.cambridge.org/core/journals/psychological-medicine/article/abs/comprehensive-assessment-of-memory-delay-aversion-timing-inhibition-decision-making-and-variability-in-attention-deficit-hyperactivity-disorder-advancing-beyond-the-threepathway-models/A01EC55EE9EBD87E093A432D6E76D0E3).
    Peer-reviewed.
12. *Positive Illusory Bias and Self-Handicapping in Adults with ADHD*
    (2023). Journal of Psychopathology and Behavioral Assessment. DOI:
    [10.1007/s10862-023-10084-2](https://link.springer.com/article/10.1007/s10862-023-10084-2).
    Peer-reviewed, ADHD adult sample.
13. Understood.org. *ADHD and Time Blindness.*
    [understood.org/en/articles/adhd-time-blindness](https://www.understood.org/en/articles/adhd-time-blindness).
    Nonprofit/professional explainer, descriptive not primary.
14. Gollwitzer PM (1999). *Implementation Intentions: Strong Effects of
    Simple Plans.* American Psychologist 54(7):493–503. DOI:
    [10.1037/0003-066X.54.7.493](https://doi.org/10.1037/0003-066X.54.7.493).
    Original theory.
15. Gollwitzer PM, Sheeran P (2006). *Implementation Intentions and Goal
    Achievement: A Meta-Analysis of Effects and Processes.* Advances in
    Experimental Social Psychology 38:69–119. DOI:
    [10.1016/S0065-2601(06)38002-1](https://www.socmot.uni-konstanz.de/sites/default/files/06_Gollwitzer_Sheeran_Implementation_Intentions_And_Goal.pdf).
    Meta-analysis, 94 studies, d≈0.65.
16. Gawrilow C, Gollwitzer PM (2008). *Implementation Intentions Facilitate
    Response Inhibition in Children with ADHD.* Cognitive Therapy and
    Research 32(2):261–280. Peer-reviewed, ADHD children.
17. Gawrilow C, Gollwitzer PM, Oettingen G (2011). *If-Then Plans Benefit
    Delay of Gratification Performance in Children with and without ADHD.*
    Cognitive Therapy and Research 35:442–455. Peer-reviewed, ADHD children.
18. Gawrilow C, et al. (2013). *Mental Contrasting with Implementation
    Intentions (MCII) Improves Self-Regulation of Goal Pursuit in
    Schoolchildren at Risk for ADHD.* Motivation and Emotion 37:134–145.
    Peer-reviewed, ADHD-risk children.
19. Systematic review/meta-analysis of implementation intentions in
    children (2025/2026). British Journal of Psychology. PubMed:
    [41784001](https://pubmed.ncbi.nlm.nih.gov/41784001/). Meta-analysis.
20. Safren SA, Sprich S, Mimiaga MJ, et al. (2010). *Cognitive Behavioral
    Therapy vs Relaxation with Educational Support for Medication-Treated
    Adults With ADHD.* JAMA 304(8):875–880.
    [jamanetwork.com/journals/jama/fullarticle/186469](https://jamanetwork.com/journals/jama/fullarticle/186469).
    RCT, adult ADHD, N=86; chunking/reminders/adaptive thinking as taught
    skills.
21. *Time on Their Side: How Visual Timers Affect Anticipatory Anxiety...*
    European Journal of Investigation in Health, Psychology and Education
    15(12):243.
    [mdpi.com/2254-9625/15/12/243](https://www.mdpi.com/2254-9625/15/12/243).
    Peer-reviewed experiment, children, mixed results.
22. Flyvbjerg B (2006). *From Nobel Prize to Project Management: Getting
    Risks Right.* Project Management Journal.
    [pmi.org/learning/library/nobel-project-management-reference-class-forecasting-8068](https://www.pmi.org/learning/library/nobel-project-management-reference-class-forecasting-8068).
    Peer-reviewed, reference-class forecasting.
23. NICE (2018, updated). *NG87: Attention deficit hyperactivity disorder:
    diagnosis and management.*
    [nice.org.uk/guidance/ng87](https://www.nice.org.uk/guidance/ng87).
    Clinical guideline.
24. CADDRA (2020). *Canadian ADHD Practice Guidelines*, 4.1 ed.
    [caddra.ca/canadian-adhd-practice-guidelines](https://www.caddra.ca/canadian-adhd-practice-guidelines/).
    Clinical guideline.
25. *Psychosocial Interventions for ADHD* (CADDRA-affiliated work group,
    2022). Brain Sciences 12(8):1023.
    [mdpi.com/2076-3425/12/8/1023](https://www.mdpi.com/2076-3425/12/8/1023).
    Peer-reviewed review.
26. Body-doubling origin attribution (Linda Anderson, ~1996) — via
    coaching/secondary sources only, no primary publication verified; 2025
    EEG pilot cited as the first attempt at objective study. Anecdotal/
    emerging — flagged, not independently verified during this pass.

### Product precedent (Q3)

27. Tiimo. *Tasks and to-dos.*
    [tiimoapp.com/faq/manage-tasks](https://www.tiimoapp.com/faq/manage-tasks).
    First-party help center, no visible article date.
28. Tiimo. *Focus timer.*
    [tiimoapp.com/faq/focus-timer](https://www.tiimoapp.com/faq/focus-timer).
    First-party help center, no visible article date.
29. Structured. *How to Create, Edit & Delete Tasks.*
    [help.structured.app/en/articles/338050](https://help.structured.app/en/articles/338050).
    First-party help center, no visible date.
30. Structured. *How to Use the Focus Timer.*
    [help.structured.app/en/articles/331010](https://help.structured.app/en/articles/331010).
    First-party help center, no visible date.
31. Sunsama. *Planned and Actual Times.*
    [help.sunsama.com/docs/usage-guides/tasks/planned-and-actual-times](https://help.sunsama.com/docs/usage-guides/tasks/planned-and-actual-times/).
    First-party docs, no visible date.
32. Amazing Marvin. *Day Planning.*
    [help.amazingmarvin.com/en/articles/5066364-day-planning](https://help.amazingmarvin.com/en/articles/5066364-day-planning).
    First-party help center, modified 2026-01-25.
33. Amazing Marvin. *Time Estimates* (feature page).
    [amazingmarvin.com/features/time-estimates](https://amazingmarvin.com/features/time-estimates/).
    First-party marketing/feature page, no visible date.
34. Amazing Marvin. *Day Planning* (feature page).
    [amazingmarvin.com/features/day-planning](https://amazingmarvin.com/features/day-planning/).
    First-party marketing/feature page, no visible date.
35. Goblin Tools. *About / Our Mission.*
    [goblin.tools/About](https://goblin.tools/About). First-party, no
    visible date.
36. Llama Life. *Getting started with Llama Life.*
    [intercom.help/llama-life/en/articles/6453017-getting-started-with-llama-life](https://intercom.help/llama-life/en/articles/6453017-getting-started-with-llama-life).
    First-party help center, modified 2026-03-24.
37. `nicky-mc/spoonful` (GitHub, public repository). README and
    `src/components/TaskManager.jsx`, `src/components/InfoModal.jsx`.
    Open-source project implementing Spoon Theory as energy-tier +
    fixed-token-budget planning; small hobby project, cited for its
    concrete pattern, not for effectiveness claims.

### Repo context consulted (not cited as external evidence)

- `CONTEXT.md` — bounded contexts, three-tier prose model, PBI
  container-only-host rule.
- `UBIQUITOUS_LANGUAGE.md` — canonical terms; confirms no existing
  Estimate/Duration/Capacity/Planner terms, i.e., this is greenfield domain
  territory.
- `docs/adr/0008-my-day-virtual-promotion-via-subtasks.md`,
  `docs/adr/0013-my-day-date-scoped-pins.md`,
  `docs/adr/0016-task-type-pbi-distinction.md`,
  `docs/adr/0017-my-day-pbi-container-grouping.md` — My Day promotion rules
  and the PBI container-only-host precedent this document's double-counting
  rule reuses.
- `docs/research/day-planner.md` — existing calendar/capacity-cue research;
  this document deliberately does not repeat its Microsoft Graph/ICS/WinUI
  feasibility findings or its Operon/Sunsama/Motion/Reclaim comparator table,
  only extends it with the estimation-specific angle.
