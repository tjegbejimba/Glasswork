---
name: glasswork-wrap-up
description: Wrap up a Glasswork task that's done or being parked. Use when the user pastes "Wrap up Glasswork task: <task-id>", asks to finish/close a Glasswork task, or hits the Glasswork app's "Wrap up" button.
---

# Glasswork — Wrap Up

The user is finishing (or parking) a Glasswork task. They pasted a one-liner like `Wrap up Glasswork task: <task-id>` from the Glasswork app's **Wrap up** button. Your job is to close the loop cleanly: leave behind a task file that future-you can understand at a glance.

The task lives at `wiki/todo/<task-id>.md` in the user's wiki vault (typically `C:\Users\toegbeji\Wiki\`).

> Full design context lives in the PRD: `~/Wiki/wiki/decisions/glasswork-v2-prd.md` (see decisions **D4** routing, **D6** completion flow, **D8** guardrails, **D9** notes format). Read it if anything below is ambiguous.

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md` end-to-end. Parse the YAML frontmatter and the body.
2. **Read recent `## Notes` entries** (especially the last few `### YYYY-MM-DD` blocks) to understand what actually happened during the work.
3. **Append a wrap-up entry** to `## Notes` using the timestamped log format (see below). 2–4 lines: what was accomplished, what's left, any follow-ups.
4. **Propose a status change** to the user:
   - If the work is genuinely complete → propose `status: done`.
   - If it's being parked / handed off → propose another status (e.g. `blocked`, or leaving it as-is) and explain why.
   - **You must get explicit confirmation before changing `status` to `done`.** Never set it silently. The wrap-up itself is also a CONFIRM action — don't finalize until the user says so.
5. **Ask whether to mark substantial work as an accomplishment.** If the work feels meaningful (not a routine fix), propose creating `wiki/accomplishments/<title>-<date>.md` — this is the **only skill** that creates accomplishment pages — and ask the user to confirm before creating it.
6. **Summarise what you did** at the end of the session: which fields you changed, which files you touched, any open follow-ups the user might want to capture as new tasks.

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
| Quick observation, status update, what-you-just-did log | Inline `## Notes` in the task file | **Default** for anything ephemeral or task-specific |
| A reasoned choice with rationale | `wiki/decisions/<slug>.md` | "We chose X over Y because…" — future-you needs to find it |
| Reusable system understanding / mental model | `wiki/concepts/<slug>.md` | Knowledge that outlives this task and applies elsewhere |
| Debugging story, outage analysis, postmortem | `wiki/incidents/<slug>.md` | Triage notes worth preserving for the next time it happens |
| Person, team, or service interaction | `wiki/entities/<name>.md` | Conversation context worth remembering |
| External link, article, doc that informed the work | `wiki/sources/<slug>.md` | A URL or reference the user might want to cite again |
| **Substantial completed effort** | `wiki/accomplishments/<title>-<date>.md` | **Wrap-up only.** Propose first, create on confirm. |

When you create a new wiki page, drop a `[[wiki-link]]` to it inline in the wrap-up `## Notes` entry so the user can navigate back. Do **not** touch the `## Related` section — a later slice owns that maintenance.

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
- **Finalize the wrap-up itself** (any frontmatter mutation, the `completed: <date>` stamp, etc.). Don't run the whole sequence unattended.
- Move, rename, or delete any wiki page (the "move task to `done/`" step is out of scope this slice anyway).
- Create a `wiki/accomplishments/` page — propose first, then wait.
- Run any command that mutates external state (git push, ADO comments).
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)

### ALLOWED — proceed without asking
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Append the wrap-up entry to the task's `## Notes` section (using the D9 format above).
- Create new wiki pages under `decisions/`, `concepts/`, `incidents/`, `entities/`, `sources/` (per D4 routing) for knowledge surfaced during wrap-up.
- Edit the task body / description.
- Add subtasks to the `## Subtasks` section (e.g. capturing follow-ups).
- Set the task's `status` to `in_progress` (e.g. if the user decides not to wrap after all).
- **Suggest** the wrap-up status change and accomplishment creation.

If a request would require breaking a HARD NO rule, refuse and name which guardrail blocked it. If it falls under CONFIRM, ask once and wait.

## Scope notes

- The current task format is **flat** — there are no nested subtasks to verify yet. Don't try to walk a subtask tree or enforce "every subtask must be done/dropped."
- **Do not move the file** out of `wiki/todo/` in this slice. The "move to `done/`" step is a later slice.
- **Do not rewrite or compress the existing `## Notes` log.** Just append the wrap-up entry. Summarising the log is a later slice.
- Don't touch the `## Related` section. A later slice owns that maintenance.
- All edits to the task file are **targeted** (append a line, insert a header, change one frontmatter field). Never rewrite the whole file.
