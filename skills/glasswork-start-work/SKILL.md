---
name: glasswork-start-work
description: Start fresh work on a Glasswork task. Use when the user pastes "Start work on Glasswork task: <task-id>", asks to begin a Glasswork task, or kicks off a new task from the Glasswork app's "Start work" button.
---

# Glasswork — Start Work

You are picking up a Glasswork task for the first time in this session. The user pasted a one-liner like `Start work on Glasswork task: <task-id>` from the Glasswork app's **Start work** button.

The task lives as a markdown file in the user's wiki vault under `wiki/todo/<task-id>.md`. The vault root is typically `C:\Users\toegbeji\Wiki\` (confirm via the user's environment if unsure).

## Process

1. **Read the task file** at `wiki/todo/<task-id>.md`. Parse the YAML frontmatter and the body.
2. **Orient**: state the title, status, priority, due date, and any ADO link out loud so the user can confirm you grabbed the right task.
3. **Skim the description** (the body before any `## Notes` / `## Subtasks` section). Identify what's being asked.
4. **Plan**: propose a short, ordered list of next steps. Keep it lightweight — this is the kickoff, not the whole project plan.
5. **Confirm** with the user before doing any work: which step do they want you to start on?
6. **Append a kickoff entry to `## Notes`** (if the section exists; otherwise create it at the bottom of the file). Format:
   ```markdown
   ## Notes

   ### <YYYY-MM-DD>
   Started work. <one-line summary of the plan you and the user agreed on>.
   ```
   Date-only is fine. Add new entries at the bottom of the section.

## Scope notes (V2 Slice 1)

- The current task format is **flat** — there are no nested subtasks yet, just whatever the file contains. Don't try to traverse a subtask tree.
- Don't touch the `## Related` section. A later slice will own that maintenance.
- Don't try to do tiered context routing yet. Keep notes inline in the task file.

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
