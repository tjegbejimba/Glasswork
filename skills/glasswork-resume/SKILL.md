---
name: glasswork-resume
description: Resume an in-flight Glasswork task. Use when the user pastes "Resume Glasswork task: <task-id>", asks to pick a Glasswork task back up, or hits the Glasswork app's "Resume" button.
---

# Glasswork — Resume

You are picking a Glasswork task back up after a break. The user pasted a one-liner like `Resume Glasswork task: <task-id>` from the Glasswork app's **Resume** button. There is prior context in the task file you need to reload before doing anything.

The task lives at `wiki/todo/<task-id>.md` in the user's wiki vault (typically `C:\Users\toegbeji\Wiki\`).

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md` end-to-end. Parse the YAML frontmatter and the body.
2. **Find where work left off**: look at the `## Notes` section. The **last `###` dated entry tells you where the user left off.** That entry is the most recent ground truth — read it carefully.
3. **Reload context** by reading any `[[wiki-link]]` references mentioned in recent notes (decisions, concepts, etc.) so you have the same mental model the user had last time.
4. **Re-orient out loud**: in 2–4 sentences, summarise:
   - What this task is about.
   - What was done last (per the most recent `###` note).
   - What looks like the obvious next step.
5. **Ask the user** whether your read of "where we left off" matches theirs and what they want to tackle now. Don't dive into work until they confirm.
6. **Append a resume entry to `## Notes`** when you actually start doing something:
   ```markdown
   ### <YYYY-MM-DD>
   Resumed. <one-line summary of what we agreed to tackle next>.
   ```
   New entries go at the bottom. Date-only is fine unless there are already multiple entries today.

## Scope notes (V2 Slice 1)

- The current task format is **flat** — no nested subtasks yet. Just read what the file contains.
- Don't rewrite the existing `## Notes` log — append only.
- Don't touch the `## Related` section. A later slice owns that.

---

## D8 Guardrails (apply to every action you take)

These are **baked into this skill** and override any user request that conflicts.

### ALLOWED freely
- Read from any source: code, wiki vault, ADO, ICM, EngHub, EV2, the web.
- Write to the wiki vault — the task's `## Notes` section, and other wiki pages (`decisions/`, `concepts/`, etc.) when the user asks.
- **Suggest** task status changes, code changes, or next actions.

### ALLOWED only with explicit confirmation
- Set `status: done` on a task. (Always confirm — even when the user clearly said "done".)
- Move a task file out of `wiki/todo/`.
- Create a `wiki/accomplishments/` page.
- Write source code. (You may **read** code freely; write only when the user explicitly says so.)
- Run any command that mutates external state (git push, ADO comments, file deletes).

### NEVER (hard rules — no override, even if the user asks)
- **Do not send Teams messages, emails, or calendar invites.** Ever. No tool call, no draft, no "here's what to send" that you then offer to send. If the user wants to communicate with another human, they do it themselves.
- Do not close or modify ADO work items beyond adding comments (and only with confirmation).
- Do not approve or merge pull requests.
- Do not delete files in the wiki vault.
- Do not modify these task frontmatter fields: `id`, `created`, `ado`.
- Do not commit or push code without an explicit user instruction in this session.

If a request would require breaking a NEVER rule, refuse and explain which guardrail blocked it.
