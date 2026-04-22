---
name: glasswork-resume
description: Resume an in-flight Glasswork task. Use when the user pastes "Resume Glasswork task: <task-id>", asks to pick a Glasswork task back up, or hits the Glasswork app's "Resume" button.
---

# Glasswork — Resume

You are picking a Glasswork task back up after a break. The user pasted a one-liner like `Resume Glasswork task: <task-id>` from the Glasswork app's **Resume** button. There is prior context in the task file you need to reload before doing anything.

The task lives at `wiki/todo/<task-id>.md` in the user's wiki vault (typically `C:\Users\toegbeji\Wiki\`).

> Full design context lives in the PRD: `~/Wiki/wiki/decisions/glasswork-v2-prd.md` (see decisions **D4** routing, **D8** guardrails, **D9** notes format). Read it if anything below is ambiguous.

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md` end-to-end. Parse the YAML frontmatter and the body.
2. **Find where work left off** — this is the most important step:
   - Locate the `## Notes` section.
   - Find the **last `### YYYY-MM-DD` dated entry** in that section. Everything under that header (until the next `###` or section break) is the most recent ground truth.
   - **Start your turn by quoting that entry verbatim** so the user can verify you read it correctly. Example opener: *"Last entry under `### 2026-04-18`: 'Confirmed default 100 in appsettings.json with Atharva.' Picking up from there…"*
3. **Reload supporting context** by reading any `[[wiki-link]]` references mentioned in that last entry (decisions, concepts, etc.) so you have the same mental model the user had when they stopped.
4. **Re-orient out loud** in 2–4 sentences:
   - What this task is about.
   - What was done last (the quoted entry).
   - What looks like the obvious next step.
5. **Ask the user** whether your read of "where we left off" matches theirs and what they want to tackle now. Don't dive into work until they confirm.
6. **Append a resume entry** to `## Notes` when you actually start doing something (using the timestamped log format below). One line is fine: "Resumed. <what we agreed to tackle now>."

## Subtask status protocol

Subtasks live under `## Subtasks` as `### [ ] Title` headers (or `### [x]` when done), with optional `- key: value` metadata lines beneath them. The `- status:` line is what the app uses to render the colored at-a-glance bar on My Day and Backlog rows. **Keep it accurate** — it's how future-you (and the user) sees what's in flight without opening every task.

When you resume work on a specific subtask, set its status to `in_progress` by adding or updating the `- status: in_progress` line under that subtask's `### [ ]` header. If the line doesn't exist, insert it directly after the header. If a previously-`blocked` subtask is now unblocked, switch its status and remove the `- blocker:` line.

When you hit a hard block (waiting on a person, a decision, an external system, a deploy), set the subtask to `blocked` and add a one-line `- blocker: <reason>` underneath. Drop the `blocker:` line when status leaves `blocked`.

When the subtask is finished, flip the header from `### [ ]` to `### [x]`. The status field auto-clears to "done" — you don't need to write `- status: done`.

**Don't churn.** Update only on real transitions (start work, hit block, finish). Don't toggle status on every tool call or partial step.

```markdown
## Subtasks

### [ ] Trace batch_size config end-to-end
- status: in_progress
- my_day: true

### [ ] Wait for Atharva to confirm default
- status: blocked
- blocker: Atharva OOO until Mon, asked in Teams DM

### [x] Read PartitionedBatchProcessor
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
- Append at the **bottom** of the section. Never edit or rewrite older entries — append-only.
- Use **targeted edits only** (append/insert under the right header). Never rewrite the whole task file.
- The "where did we leave off?" pointer is **always the last `### YYYY-MM-DD` block** — this skill depends on that invariant.

## Tiered context routing (D4)

As you work, classify each new piece of context and write it to the right place. Routing is automatic — don't ask the user for permission to file knowledge correctly, just do it (and mention what you did).

| Kind of thing | Goes to | Trigger |
|---|---|---|
| Quick observation, status update, what-you-just-did log | Inline `## Notes` in the task file | **Default** for anything ephemeral or task-specific |
| A reasoned choice with rationale | `wiki/decisions/<slug>.md` | "We chose X over Y because…" — future-you needs to find it |
| Reusable system understanding / mental model | `wiki/concepts/<slug>.md` | Knowledge that outlives this task and applies elsewhere |
| Debugging story, outage analysis, postmortem | `wiki/incidents/<slug>.md` | Triage notes worth preserving for the next time it happens |
| Person, team, or service interaction | `wiki/entities/<name>.md` | Conversation context worth remembering |
| External link, article, doc that informed the work | `wiki/sources/<slug>.md` | A URL or reference the user might want to cite again |
| Substantial completed effort | `wiki/accomplishments/<title>-<date>.md` | **Only on wrap-up.** Do not create from this skill. |

When you create a new wiki page, drop a `[[wiki-link]]` to it inline in the relevant `## Notes` entry so the user can navigate back. Do **not** touch the `## Related` section — a later slice owns that maintenance.

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
- Complete a wrap-up (handled by the `glasswork-wrap-up` skill — don't shortcut into it from here).
- Run any command that mutates external state (git push, ADO comments, file deletes on disk outside the wiki).
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)

### ALLOWED — proceed without asking
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Append to the task's `## Notes` section (using the D9 format above).
- Create new wiki pages under `decisions/`, `concepts/`, `incidents/`, `entities/`, `sources/` (per D4 routing).
- Edit the task body / description.
- Add subtasks to the `## Subtasks` section.
- Set the task's `status` to `in_progress`.
- **Suggest** task status changes, code changes, or next actions.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it. If it falls under CONFIRM, ask once and wait.

## Scope notes

- Subtasks live under `## Subtasks` as `### [ ] Title` blocks with optional `- key: value` metadata. Don't nest subtasks under subtasks — the format is one level deep.
- Don't rewrite the existing `## Notes` log — append only.
- Don't touch the `## Related` section. A later slice owns that.
- All edits to the task file are **targeted** (append a line, insert a header, change one frontmatter field). Never rewrite the whole file.
