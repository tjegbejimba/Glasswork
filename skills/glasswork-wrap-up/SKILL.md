---
name: glasswork-wrap-up
description: Wrap up a Glasswork task that's done or being parked. Use when the user pastes "Wrap up Glasswork task: <task-id>", asks to finish/close a Glasswork task, or hits the Glasswork app's "Wrap up" button.
---

# Glasswork — Wrap Up

The user is finishing (or parking) a Glasswork task. They pasted a one-liner like `Wrap up Glasswork task: <task-id>` from the Glasswork app's **Wrap up** button. Your job is to close the loop cleanly: leave behind a task file that future-you can understand at a glance.

The task lives at `wiki/todo/<task-id>.md` in the user's wiki vault (typically `C:\Users\toegbeji\Wiki\`).

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md` end-to-end. Parse the YAML frontmatter and the body.
2. **Read recent `## Notes` entries** to understand what actually happened during the work.
3. **Append a wrap-up entry to `## Notes`** capturing what was done and any loose ends:
   ```markdown
   ### <YYYY-MM-DD>
   Wrapping up. <2–4 lines: what was accomplished, what's left, any follow-ups.>
   ```
4. **Propose a status change** to the user:
   - If the work is genuinely complete → propose `status: done`.
   - If it's being parked / handed off → propose another status (e.g. `blocked`, or leaving it as-is) and explain why.
   - **You must get explicit confirmation before changing `status` to `done`.** Never set it silently.
5. **Ask whether to mark substantial work as an accomplishment.** If the work feels meaningful (not a routine fix), propose creating `wiki/accomplishments/<title>-<date>.md` and ask the user to confirm before creating it.
6. **Summarise what you did** at the end of the session: which fields you changed, which files you touched, any open follow-ups the user might want to capture as new tasks.

## Scope notes (V2 Slice 1)

- The current task format is **flat** — there are no nested subtasks to verify yet. Don't try to walk a subtask tree or enforce "every subtask must be done/dropped."
- **Do not move the file** out of `wiki/todo/` in this slice. The "move to `done/`" step is a later slice.
- **Do not rewrite or compress the existing `## Notes` log.** Just append the wrap-up entry. Summarising the log is a later slice.
- Don't touch the `## Related` section. A later slice owns that maintenance.

---

## D8 Guardrails (apply to every action you take)

These are **baked into this skill** and override any user request that conflicts.

### ALLOWED freely
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Write to the wiki vault — the task's `## Notes` section, and other wiki pages (`decisions/`, `concepts/`, etc.) when the user asks.
- **Suggest** task status changes, code changes, or next actions.

### ALLOWED only with explicit confirmation
- Set `status: done` on a task. **Always confirm — even in wrap-up. This is the most common case where the rule matters.**
- Move a task file out of `wiki/todo/` (out of scope this slice anyway).
- Create a `wiki/accomplishments/` page.
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)
- Run any command that mutates external state (git push, ADO comments, file deletes).

### NEVER (hard rules — no override, even if the user asks)
- **Do not send Teams messages, emails, or calendar invites.** Ever. No tool call, no draft, no "here's what to send" that you then offer to send. Even at wrap-up, even if the user says "let people know I'm done" — they do that themselves.
- Do not close or modify ADO work items beyond adding comments (and only with confirmation).
- Do not approve or merge pull requests.
- Do not delete files in the wiki vault.
- Do not modify these task frontmatter fields: `id`, `created`, `ado`.
- Do not commit or push code without an explicit user instruction in this session.

If a request would require breaking a NEVER rule, refuse and explain which guardrail blocked it.
