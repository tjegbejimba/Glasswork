# Ubiquitous Language

> Canonical terms used across Glasswork code, UI, docs, and conversations
> with agents. Use these terms exactly. Aliases listed are explicitly
> rejected — do not use them.

| Term | Definition | Aliases to avoid |
|---|---|---|
| **Vault** | The Obsidian folder Glasswork reads tasks from. Source of truth for all task data. Currently `%UserProfile%\Wiki\wiki\todo`. | folder, directory, repo, store |
| **Task** | A work item represented by one `.md` file in the vault. May have subtasks, notes, ADO links, due date. | item, work item (work item is overloaded — use only when explicitly referring to ADO) |
| **Subtask** | A child step inside a task. Has its own status (`todo`, `in_progress`, `blocked`, `done`, `dropped`), title, optional notes. Lives inline in the parent's `.md` body. | step, todo (step is fine in user copy; subtask is canonical in code) |
| **Subtask row** | The list-item template that renders one subtask in TaskDetail. Two interactive hit zones: the **circle glyph** (single-click toggles done) and the **subtask text** (single-click opens `SubtaskDetailDialog`). Hand cursor on the text advertises the affordance. Same model in active and completed lists. See ADR 0004. | subtask line, subtask item |
| **Active subtask list** | The `ListView` rendering subtasks not in `done` or `dropped` status. Supports drag-reorder. Each row carries the full action set (My Day toggle, `⋯` More menu). | open subtasks, pending subtasks |
| **Completed subtask list** | The `ItemsControl` inside the `Completed (n)` `Expander`, rendering subtasks in `done` or `dropped` status. Same hit-zone model as active rows. Action set is reduced: no My Day toggle on the row; `⋯` More menu omits "Set due date." Hidden when zero completed. | done list, archive list |
| **Description** | The prose markdown that appears between a task's frontmatter and its `## Subtasks` heading. Answers "what is this task about." Stored on the model as `Task.Description` (was `Task.Body`). Source of `Blurb`. | body, body text (former `Body` was renamed for clarity — see ADR 0002) |
| **Notes** *(task-level)* | The prose markdown in a task's `## Notes` section. Free-form scratch — primarily human-written. Examples: "remember to ask X", "blocked on Y until Friday", quick decision log. Distinct from `SubTask.Notes` (per-subtask prose) and from Artifacts (agent-produced deliverables). | task notes, scratch (use "Notes" in UI; "task notes" only when disambiguating from subtask notes in conversation) |
| **Artifact** | A markdown work-product attached to a task — typically agent-produced (plan, design, investigation, draft, summary). Stored as `*.md` files in `<vault>/wiki/todo/<taskId>.artifacts/`. **Read-only in the app**; the vault and Obsidian/agents are the editing surfaces. Distinct from Notes (free-form scratch) and Description (the task's framing prose). | attachment (attachment implies binary upload), document, output |
| **Artifacts section** | Collapsible-list surface in TaskDetail rendered beneath Notes. Hidden when zero artifacts. One `Expander` per artifact, ordered by mtime descending; the newest auto-expands and the rest start collapsed. Each header carries a relative-time badge and an "Open in Obsidian" link; the body renders the artifact markdown read-only. | artifacts panel, artifacts list, attachments section |
| **Page** | A top-level navigation destination in the app shell (My Day, Backlog, Work Log, Settings). Glasswork keeps "Page" rather than the todo-app convention "Smart List." | screen, view, tab, smart list |
| **My Day** | The default landing page. Shows tasks the user has marked for today (`MyDay` field on a task is today's date). | today, dashboard, home (Home is reserved for the future Home Dashboard) |
| **Backlog** | Page showing tasks not done and not on My Day. | inbox, queue, anytime |
| **Work Log** | Page showing completed tasks. | done, archive, logbook, completed |
| **Active task** | A task that has rich state worth surfacing in the row — at least one subtask, a blocker, or a body summary. Renders as a **card** by default. | rich task |
| **Quiet task** | A task with no rich state to surface (no subtasks, no blocker, empty body). Renders as a **single-line row**. Cannot be expanded — there is nothing to expand. | simple task, plain task |
| **Adaptive row** | The list-row template that automatically picks card form vs. single-line form based on whether the task is active or quiet. | smart row, dynamic row |
| **Card form** | The expanded row rendering: title row + segmented progress bar + current-step + (optional) blocker row + (optional) blurb row. | expanded row, big row |
| **Single-line form** | The compact row rendering: title row only. | collapsed row, compact row, condensed row |
| **Collapsed** | A user-overridden state on an active task that forces it to render in single-line form even though it would otherwise be a card. **Asymmetric**: only active tasks can be collapsed; quiet tasks have no expand state. Persists across navigation and restarts via `IUiStateService`. | hidden, minimized, folded |
| **Segmented progress bar** | The per-subtask visual: one segment per subtask, colored by subtask status (done = accent, in-progress = lighter accent, blocked = red, dropped = gray, todo = empty outline). Falls back to a continuous bar with `n of m done` text when subtask count exceeds 12. | progress chart, status bar, subtask chart |
| **Current step** | The `Title` of the first subtask in `in_progress` status. Shown to the right of the progress bar in card form. | active step, current subtask, on-deck |
| **Blocker row** | Conditional row in card form, rendered only when at least one subtask is `blocked`. Shows the blocker subtask's title or first line of its notes, prefixed with a red 🚫. | block row, stuck row |
| **Blurb** | A one-line summary of what a task is about, shown in card form below the progress/blocker rows. **Source today**: first non-blank line of the task `Description` (with leading `#`, `>`, and link wrappers stripped, truncated at 80 chars). Never sourced from `Notes` or Artifacts — only from `Description`. **Future extension**: explicit `summary:` frontmatter field, when present, takes precedence. | summary line, blurb (term is fine; just don't confuse with the future `summary:` field) |
| **Chip** | A small inline metadata badge on the title row. Three kinds today: priority chip, due chip, ADO chip. | tag (tag is reserved for vault tags), pill, badge |
| **Priority chip** | Title-row chip showing task priority (`low`, `med`, `high`, `urgent`). Color-coded. Hidden when priority is unset. | — |
| **Due chip** | Title-row chip showing relative due date (`overdue` red, `today` orange, `≤ 3 days` accent, `future` neutral). Hidden when no due date. | due date, deadline |
| **ADO chip** | Title-row chip showing linked Azure DevOps work item (`ADO #1234`). Hidden when no link. Click jumps to ADO. | work item link, ado link |
| **Empty state** | The visual rendered when a page has no tasks to show. Headline + sub copy + up to 2 CTAs. One reusable `EmptyState` control covers all pages. | placeholder, blank state, zero state |
| **Footer status bar** | A thin (~28px) strip at the bottom of `MainWindow` showing vault path, task counts, watcher state, and last-reload time. | status strip, status line, footer |
| **UI state** | Persistent app-local user preferences that **do not belong in the vault**. Stored in `%LocalAppData%\Glasswork\ui-state.json` via `IUiStateService`. Examples: collapsed task ids, future sidebar pane state. | preferences (reserved for actual user settings), settings (reserved for the Settings page), local state |
| **Self-write** | A vault file write originated by Glasswork itself. Tracked by `SelfWriteTracker` so the file watcher doesn't treat it as an external edit. | own write, internal write |
| **Home Dashboard** *(future)* | A planned future surface aggregating tasks plus non-task wiki notes from the vault. **Not built yet.** `HomePage` is being deleted in the current slice; a future dashboard will be a fresh design, not a revival. | home (use only with the "Dashboard" suffix to avoid ambiguity with the deleted `HomePage`) |
| **Inbox** *(deferred)* | The common todo-app concept of a triage queue for new tasks. **Not in Glasswork.** Backlog plays a similar role today. If/when added, would sit above Backlog. | triage, queue, new |

## Naming conventions

- **Code symbols**: `PascalCase` for types, `camelCase` for parameters and locals, `_camelCase` for private fields. Match the term spelling exactly (`SubTask`, not `Subtask` or `Sub_Task`).
- **UI labels**: title case for headings (`My Day`), sentence case for sub-copy (`Pick something from your backlog.`).
- **Frontmatter keys**: `snake_case` (`my_day`, `ado_link`, future `summary`).

## When this file is wrong

If you find code or copy using a different word for a concept defined here, **change the code** to match this file (not the other way around). If a new concept emerges, add it here in the same PR that introduces it.
