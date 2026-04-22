---
name: glasswork-wrap-up
description: Wrap up a Glasswork task that's done or being parked. Use when the user pastes "Wrap up Glasswork task: <task-id>", asks to finish/close a Glasswork task, or hits the Glasswork app's "Wrap up" button.
---

# Glasswork — Wrap Up

The user is finishing (or parking) a Glasswork task. They pasted a one-liner like `Wrap up Glasswork task: <task-id>` from the Glasswork app's **Wrap up** button. Your job is to close the loop cleanly: leave behind a task file that future-you can understand at a glance.

The task lives at `wiki/todo/<task-id>.md` in the user's wiki vault (typically `C:\Users\toegbeji\Wiki\`).

> Full design context lives in the PRD: `~/Wiki/wiki/decisions/glasswork-v2-prd.md` (see decisions **D4** routing, **D6** completion flow, **D8** guardrails, **D9** notes format). Read it if anything below is ambiguous.

## Process (D6 — seven steps)

1. **Read the task file** at `wiki/todo/<task-id>.md` end-to-end. Parse the YAML frontmatter and the body. Skim any pages referenced in `## Related` so the summary reflects the wider context.
2. **Read recent `## Notes` entries** (especially the last few `### YYYY-MM-DD` blocks) to understand what actually happened during the work.
3. **Write/refresh the `### Summary` block** at the **top** of `## Notes` (see "Summary block" below). 3–6 lines compressing what shipped, what's left, and any follow-ups.
4. **Append today's wrap-up entry** to the timestamped log at the bottom of `## Notes` using the D9 format (see below). 2–4 lines.
5. **Verify subtask accounting** (see "Subtask accounting gate" below). Every `### [ ]` subtask must be done, dropped, or carried over. If anything is unaccounted for, **refuse to mark `status: done`** and surface the unresolved items so the user can resolve them.
6. **Propose a status change** to the user:
   - If the work is genuinely complete → propose `status: done` **and** `completed: <today>`.
   - If it's being parked / handed off → propose another status (e.g. `blocked`, or leaving it as-is) and explain why. Do not stamp `completed:` in this case.
   - **You must get explicit confirmation before changing `status` to `done`.** Never set it silently. The wrap-up itself is also a CONFIRM action — don't finalize until the user says so.
7. **Ask whether to mark substantial work as an accomplishment** (see "Substantial-ness heuristic" below). If yes, propose creating `wiki/accomplishments/<title>-<date>.md` — this is the **only skill** that creates accomplishment pages — and ask the user to confirm before creating it.
8. **On confirmation, finalize:** stamp `status: done` and `completed: YYYY-MM-DD` in the frontmatter, then **move the file** from `wiki/todo/<task-id>.md` to `wiki/todo/done/<task-id>.md` (create `done/` if missing). The Glasswork app's FileWatcherService picks up the move and regenerates `_index.md` / `_today.md` automatically — completed tasks disappear from the active index because the index only scans the top-level `wiki/todo/` directory.
9. **Summarise what you did** at the end of the session: which fields you changed, which files you touched, the new path of the file, any open follow-ups the user might want to capture as new tasks.

## Summary block (top of `## Notes`)

The `### Summary` subsection lives at the **very top** of `## Notes`, immediately under the `## Notes` header and above the timestamped `### YYYY-MM-DD` log. It's the wrap-up's headline: a future reader (or future-you) should be able to read just the summary and understand the outcome without scrolling the log.

```markdown
## Notes

### Summary
Shipped the new BatchOptions defaults in PR #1234. Default `batch_size` is now
100 across `appsettings.json` and the `[[batch-tuning]]` runbook. Two follow-ups
captured as new tasks: deprecate the legacy `MaxBatch` knob, and add a metrics
dashboard for batch latency.

### 2026-04-18
Started work. Plan: read PartitionedBatchProcessor, then trace batch_size config.
…
```

**Rules:**
- The `### Summary` header is fixed — always exactly that, always at the top of `## Notes`.
- **Regenerate or update** the summary on each wrap-up (it's not appended to). If a previous wrap-up wrote one, replace it with the current view. The timestamped log below is **never** rewritten.
- 3–6 lines, prose. Mention the concrete artifacts (PRs, decisions, wiki pages created) and any explicit follow-ups.
- If you do not have enough material to write a meaningful summary, say so and ask the user before fabricating one.
- This is a **targeted edit**: insert/replace just the `### Summary` block. Do not touch the `### YYYY-MM-DD` entries below it.

## Subtask accounting gate

Before you can propose `status: done`, every subtask under `## Subtasks` must be in one of these terminal states:

| State | How it looks | Meaning |
|---|---|---|
| **Done** | `### [x] Title` **or** subtask metadata block contains `- status: done` | Completed in this task. |
| **Dropped** | Subtask metadata block contains `- status: dropped` | Decided not to do — note **why** in the summary or notes. |
| **Carried over** | A new task exists in `wiki/todo/` that picks up the work, **and** the wrap-up summary or today's notes entry mentions it by ID (e.g. `Carried over to [[follow-up-task]]`) | Work continues elsewhere. |

If any subtask is `### [ ]` and isn't carried over, **refuse to mark the parent done**. List the offending subtasks back to the user and ask them to either (a) check them off, (b) mark `status: dropped`, or (c) confirm a follow-up task they want you to create. After they resolve them, re-run the gate.

This is the only place the wrap-up skill walks the subtask tree — keep it shallow (one level, the existing flat `### [ ]` headers under `## Subtasks`).

## Substantial-ness heuristic (accomplishment proposal)

Propose `wiki/accomplishments/<title>-<date>.md` when **two or more** of these apply:

- **Visible artifact:** shipped a PR, a published doc, an outage mitigation, a deployed config, a deck — something with a URL.
- **Multi-day effort:** the timestamped log spans more than two distinct days, or the task has 3+ checked-off subtasks.
- **Cross-cutting:** the work touched multiple services/teams or required pulling in someone outside the immediate team.
- **Reusable knowledge:** the wrap-up created a `decisions/`, `concepts/`, or `incidents/` page (i.e. there's something worth pointing back to).

**Skip** the proposal — don't even ask — for routine fixes: a typo PR, a one-line config tweak, a single-day investigation that didn't change anything, or a task that was abandoned (`status: dropped`).

When in doubt, ask the user with one short sentence ("This felt substantial — want me to draft an accomplishment page?") rather than creating one silently. The CONFIRM gate still applies.

## Notes log format (D9 — Option B, timestamped)

All writes to `## Notes` follow this layout:

```markdown
## Notes

### 2026-04-18
Started work. Plan: read PartitionedBatchProcessor, then trace batch_size config.
Confirmed default 100 in appsettings.json with Atharva.

### 2026-04-19
Resumed. Picking up the config trace from yesterday.
Wrapping up. Landed PR #1234 with the new BatchOptions defaults. Follow-up: update
runbook in `[[batch-tuning]]`.
```

**Rules:**
- One `### YYYY-MM-DD` header per day, not per entry. Before appending the wrap-up note, **check whether today's header already exists** in `## Notes`. If it does, append your wrap-up lines under it. If not, add the header first, then your lines.
- Date-only — no times.
- Append at the **bottom** of the section. Never edit or rewrite older entries.
- Use **targeted edits only** (append/insert under the right header, single-field frontmatter update). Never rewrite the whole task file.

## Tiered context routing (D4)

During wrap-up you may surface knowledge worth preserving beyond the task file. Classify and route automatically — don't ask the user for permission to file knowledge correctly, just do it (and mention what you did).

| Kind of thing | Goes to | Trigger |
|---|---|---|
| Quick observation, status update, what-you-just-did log | Inline `## Notes` in the task file | **Default** for anything ephemeral or task-specific. 1–5 lines per entry. |
| **Standalone document the work produced** (investigation, plan, design, repro, dataset, query collection) | `<id>.artifacts/<name>.md` (artifact) | Multi-paragraph, self-contained, will be re-read end-to-end. Bigger than a `## Notes` entry. |
| A reasoned choice with rationale | `wiki/decisions/<slug>.md` | "We chose X over Y because…" — future-you needs to find it |
| Reusable system understanding / mental model | `wiki/concepts/<slug>.md` | Knowledge that outlives this task and applies elsewhere |
| Debugging story, outage analysis, postmortem | `wiki/incidents/<slug>.md` | Triage notes worth preserving for the next time it happens |
| Person, team, or service interaction | `wiki/entities/<name>.md` | Conversation context worth remembering |
| External link, article, doc that informed the work | `wiki/sources/<slug>.md` | A URL or reference the user might want to cite again |
| **Substantial completed effort** | `wiki/accomplishments/<title>-<date>.md` | **Wrap-up only.** Propose first, create on confirm. |

### Notes vs Artifacts

The most common routing mistake is dumping investigations, plans, and design sketches into `## Notes` when they should be Artifacts.

- **Notes** = a *log of what happened* — short, append-only, timestamped. "Found the bug in `X.cs`. Trying fix Y." 1–5 lines per entry.
- **Artifacts** = a *standalone document* the work produced — multi-paragraph, self-contained, has its own title. Lives at `<id>.artifacts/<name>.md`.

If you find yourself writing more than ~10 lines into a single `## Notes` entry, stop and ask: *"is this a log entry, or is it a document I'll want to re-read?"* If the latter, write it as an Artifact and link to it from the Notes entry.

Examples:

- ✅ Artifact: `triage-icm-626495494.md` — multi-section investigation, repro, root cause hypothesis
- ✅ Artifact: `backlinks-watcher-design.md` — what we're building and why, before implementation
- ✅ Artifact: `telemetry-sweep-queries.md` — collection of KQL queries with descriptions
- ❌ Notes: "Triaged ICM 626495494, root cause is X (see `triage-icm-626495494` artifact)" — log entry pointing at the artifact

At wrap-up, **list any artifacts the work produced** in the `### Summary` block so a future reader knows where the heavy reading lives.

When you create a new wiki page or artifact, drop a `[[wiki-link]]` (or artifact filename) inline in the wrap-up `## Notes` entry so the user can navigate back. Do **not** touch the `## Related` section — a later slice owns that maintenance.

## D8 Guardrails (apply to every action you take)

These are **baked into this skill** and override any user request that conflicts. Wrap-up is the highest-stakes skill — be especially strict about the CONFIRM gates.

### HARD NO — never, under any circumstances (no override, even if the user explicitly asks)
- **Do not send Teams messages, emails, or calendar invites.** Even at wrap-up, even if the user says "let people know I'm done" — they do that themselves. No tool call, no draft, no "here's what to send."
- Do not approve or merge pull requests.
- Do not delete files in the wiki vault.
- Do not close ADO work items.
- Do not modify these task frontmatter fields: `id`, `created`, `ado`.

### CONFIRM — allowed only with explicit user confirmation in this session
- **Transition the task's `status` to `done`** — the headline wrap-up action. Always confirm. One Enter-press is fine, but the checkpoint is mandatory.
- **Stamp `completed: YYYY-MM-DD`** in the frontmatter (only when flipping to `done`).
- **Move the task file** from `wiki/todo/<id>.md` to `wiki/todo/done/<id>.md`. This is the final mechanical step of wrap-up and only runs after the user confirms `done`.
- **Finalize the wrap-up itself** (any frontmatter mutation, the `completed:` stamp, the move). Don't run the whole sequence unattended.
- Move, rename, or delete any **other** wiki page.
- Create a `wiki/accomplishments/` page — propose first, then wait.
- Run any command that mutates external state (git push, ADO comments).
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)

### ALLOWED — proceed without asking
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Append the wrap-up entry to the task's `## Notes` section (using the D9 format above).
- Create artifacts in `<id>.artifacts/<name>.md` for standalone documents surfaced or finalized during wrap-up (see "Notes vs Artifacts" above).
- Create new wiki pages under `decisions/`, `concepts/`, `incidents/`, `entities/`, `sources/` (per D4 routing) for knowledge surfaced during wrap-up.
- Edit the task body / description.
- Add subtasks to the `## Subtasks` section (e.g. capturing follow-ups).
- Set the task's `status` to `in_progress` (e.g. if the user decides not to wrap after all).
- **Suggest** the wrap-up status change and accomplishment creation.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it. If it falls under CONFIRM, ask once and wait.

## Scope notes

- Subtask accounting is **shallow** — walk only the flat `### [ ]` headers under `## Subtasks` (and their immediate metadata blocks for `status: done` / `status: dropped`). Don't recurse — the format is flat.
- **Move the file** to `wiki/todo/done/<id>.md` after the user confirms `done`. Create the `done/` directory if it doesn't exist. Do not delete the file — moving preserves history.
- **Do not rewrite or compress the existing timestamped `### YYYY-MM-DD` log.** The `### Summary` block at the top is the only part of `## Notes` you may rewrite; everything below it is append-only.
- Don't touch the `## Related` section. A later slice owns that maintenance.
- All edits to the task file are **targeted** (append a line, insert/replace the `### Summary` block, change one frontmatter field). Never rewrite the whole file.
