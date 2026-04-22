---
name: glasswork-start-work
description: Start fresh work on a Glasswork task. Use when the user pastes "Start work on Glasswork task: <task-id>", asks to begin a Glasswork task, or kicks off a new task from the Glasswork app's "Start work" button.
---

# Glasswork — Start Work

You are picking up a Glasswork task for the first time in this session. The user pasted a one-liner like `Start work on Glasswork task: <task-id>` from the Glasswork app's **Start work** button.

The task lives as a markdown file in the user's wiki vault under `wiki/todo/<task-id>.md`. The vault root is typically `C:\Users\toegbeji\Wiki\` (confirm via the user's environment if unsure).

> Full design context lives in the PRD: `~/Wiki/wiki/decisions/glasswork-v2-prd.md` (see decisions **D4** routing, **D8** guardrails, **D9** notes format). Read it if anything below is ambiguous.

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md`. Parse the YAML frontmatter and the body.
2. **Orient**: state the title, status, priority, due date, and any ADO link out loud so the user can confirm you grabbed the right task.
3. **Skim the description** (the body before any `## Notes` / `## Subtasks` section). Identify what's being asked.
4. **Plan**: propose a short, ordered list of next steps. Keep it lightweight — this is the kickoff, not the whole project plan.
5. **Confirm** with the user before doing any work: which step do they want you to start on?
6. **Append a kickoff entry** to `## Notes` using the timestamped log format (see below). One line is fine: "Started work. <one-line summary of the plan>."

## Subtask status protocol

Subtasks live under `## Subtasks` as `### [ ] Title` headers (or `### [x]` when done), with optional `- key: value` metadata lines beneath them. The `- status:` line is what the app uses to render the colored at-a-glance bar on My Day and Backlog rows. **Keep it accurate** — it's how future-you (and the user) sees what's in flight without opening every task.

When you start working on a specific subtask, set its status to `in_progress` by adding or updating the `- status: in_progress` line under that subtask's `### [ ]` header. If the line doesn't exist, insert it directly after the header.

When you hit a hard block (waiting on a person, a decision, an external system, a deploy), set the subtask to `blocked` and add a one-line `- blocker: <reason>` underneath. Drop the `blocker:` line when status leaves `blocked`.

When the subtask is finished, flip the header from `### [ ]` to `### [x]`. The status field auto-clears to "done" — you don't need to write `- status: done`.

**Don't churn.** Update only on real transitions (start work, hit block, finish). Don't toggle status on every tool call or partial step. If you're not sure whether a step counts as "starting" the subtask, leave the status alone.

```markdown
## Subtasks

### [ ] Trace batch_size config end-to-end
- status: in_progress
- my_day: true

### [ ] Wait for Atharva to confirm default
- status: blocked
- blocker: Atharva OOO until Mon, asked in Teams DM

### [x] Read PartitionedBatchProcessor

### [ ] Write follow-up doc
```

## Notes log format (D9 — Option B, timestamped)

All writes to `## Notes` follow this layout:

```markdown
## Notes

### 2026-04-18
Started work. Plan: read PartitionedBatchProcessor, then trace batch_size config.
Confirmed default 100 in appsettings.json with Atharva.

### 2026-04-19
Resumed. Picking up the config trace from yesterday.
```

**Rules:**
- One `### YYYY-MM-DD` header per day, not per entry. Before appending, **check whether today's header already exists** in `## Notes`. If it does, append your new lines under it. If not, add the header first, then your lines.
- Date-only — no times.
- Append at the **bottom** of the section. Never edit older entries.
- Use **targeted edits only** (append/insert) — never rewrite the whole task file.
- If the task has no `## Notes` section yet, create one at the bottom of the file (above `## Related` if it exists).

## Tiered context routing (D4)

As you work, classify each piece of context and write it to the right place. Routing is automatic — don't ask the user for permission to file knowledge correctly, just do it (and mention what you did).

| Kind of thing | Goes to | Trigger |
|---|---|---|
| Quick observation, status update, what-you-just-did log | Inline `## Notes` in the task file | **Default** for anything ephemeral or task-specific. 1–5 lines per entry. |
| **Standalone document the work produced** (investigation, plan, design, repro, dataset, query collection) | `<id>.artifacts/<name>.md` (artifact) | Multi-paragraph, self-contained, will be re-read end-to-end. Bigger than a `## Notes` entry. |
| A reasoned choice with rationale | `wiki/decisions/<slug>.md` | "We chose X over Y because…" — future-you needs to find it |
| Reusable system understanding / mental model | `wiki/concepts/<slug>.md` | Knowledge that outlives this task and applies elsewhere |
| Debugging story, outage analysis, postmortem | `wiki/incidents/<slug>.md` | Triage notes worth preserving for the next time it happens |
| Person, team, or service interaction | `wiki/entities/<name>.md` | Conversation context worth remembering ("Atharva owns X") |
| External link, article, doc that informed the work | `wiki/sources/<slug>.md` | A URL or reference the user might want to cite again |
| Substantial completed effort | `wiki/accomplishments/<title>-<date>.md` | **Only on wrap-up.** Do not create from this skill. |

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

When you create a new wiki page or artifact, drop a `[[wiki-link]]` (or artifact filename) inline in the relevant `## Notes` entry so the user can navigate back. Do **not** touch the `## Related` section — a later slice owns that maintenance.

## D8 Guardrails (apply to every action you take)

These are **baked into this skill** and override any user request that conflicts.

### HARD NO — never, under any circumstances (no override, even if the user explicitly asks)
- **Do not send Teams messages, emails, or calendar invites.** No tool call, no draft, no "here's what to send" that you then offer to send. If the user wants to communicate with another human, they do it themselves.
- Do not approve or merge pull requests.
- Do not delete files in the wiki vault.
- Do not close ADO work items.
- Do not modify these task frontmatter fields: `id`, `created`, `ado`.

### CONFIRM — allowed only with explicit user confirmation in this session
- Move, rename, or delete any wiki page.
- Transition a task's `status` to `done`.
- Complete a wrap-up (the wrap-up skill owns that gate; not relevant from here, but listed for completeness).
- Run any command that mutates external state (git push, ADO comments, file deletes on disk outside the wiki).
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)

### ALLOWED — proceed without asking
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Append to the task's `## Notes` section (using the D9 format above).
- Create artifacts in `<id>.artifacts/<name>.md` for standalone documents (see "Notes vs Artifacts" above).
- Create new wiki pages under `decisions/`, `concepts/`, `incidents/`, `entities/`, `sources/` (per D4 routing).
- Edit the task body / description.
- Add subtasks to the `## Subtasks` section.
- Set the task's `status` to `in_progress`.
- **Suggest** task status changes, code changes, or next actions.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it. If it falls under CONFIRM, ask once and wait.

## Scope notes

- Subtasks live under `## Subtasks` as `### [ ] Title` blocks with optional `- key: value` metadata. Don't nest subtasks under subtasks — the format is one level deep.
- Don't touch the `## Related` section. A later slice will own that.
- All edits to the task file are **targeted** (append a line, insert a header, change one frontmatter field). Never rewrite the whole file.
