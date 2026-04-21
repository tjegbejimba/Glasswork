# Agent contract — writing artifacts to Glasswork tasks

> Audience: agents (LLMs, scripts, CLIs) producing markdown work-products
> for a Glasswork task. Read this once before writing any artifact.

Glasswork surfaces a per-task **Artifacts section** that automatically
picks up markdown files you drop in a sibling folder next to the task.
This document describes the contract you must follow so the app sees
your output, renders it correctly, and never serves a half-written
file.

## Where to write

For a task with id `TASK-1` (the canonical id from the task's frontmatter,
which equals the task's `.md` filename stem), write artifacts to:

```
<vault>/wiki/todo/TASK-1.artifacts/<your-name>.md
```

Rules:

- The folder name is exactly `<task-id>.artifacts` (case-insensitive
  suffix match).
- Only `*.md` files are indexed. Other file types in the folder are
  silently ignored.
- One file per artifact. Don't bundle multiple artifacts into one file.

The folder may not exist yet — create it. Glasswork will start watching
it on the next event.

## Atomic-rename rule (REQUIRED)

Glasswork watches `<task-id>.artifacts/` with a 250ms debounce and reacts
to every `*.md` event. If you write directly to `<name>.md`, the user
may see a half-written file the moment your first byte lands.

**Always:**

1. Write the full content to a temp filename **without** the `.md`
   extension (e.g. `<name>.md.tmp`, `.<name>.md.partial`).
2. When the write is fully flushed and closed, **rename** the temp file
   to the final `<name>.md`.

The rename is atomic on every modern filesystem and is the agent's
"commit" point. Glasswork only reacts to `.md` events, so temp-name
writes will not trigger a refresh until the rename lands.

If you can't rename (e.g. you're posting via an HTTP API), buffer the
content fully in memory and write it in a single `File.WriteAllText`
call. Streaming writes that incrementally append are not safe.

## Optional frontmatter

Artifacts may begin with YAML frontmatter. All fields are optional:

```yaml
---
title: Plan for X
kind: plan          # plan | design | investigation | draft | summary | other
producer: copilot-cli
created_at: 2025-11-09T12:34:56Z
---

# Plan for X

...rest of the markdown body...
```

Only `title` is currently surfaced in the UI (in the Expander header).
The other fields are parsed and stored but not yet displayed; they're
reserved for v1.1+ provenance UI.

## Title resolution (fallback chain)

The Artifacts section picks the row title in this order:

1. **`title:` frontmatter** — if present and non-empty, wins outright.
2. **First `# H1`** in the body — capped at ~80 characters.
3. **Filename without `.md`** — final fallback.

If you care what shows up in the header, set `title:` explicitly.

## Read-only contract

Glasswork **never writes back** to artifact files. Edits happen in the
vault (Obsidian, your editor, your agent). The "Open in Obsidian" button
on each artifact row jumps you straight to the file for editing.

If the user wants to delete an artifact, they delete the file from disk;
Glasswork's watcher refreshes the section automatically.

## Don'ts

- Don't write artifacts for tasks that don't exist. Glasswork won't
  hide them, but they won't render either (no parent task to attach to).
- Don't use the `.artifacts` folder for non-artifact scratch
  (intermediate logs, raw API dumps). Either gitignore those elsewhere
  or use a non-`.md` extension if they must live alongside.
- Don't write file-scheme or `javascript:` links inside artifact
  markdown — Glasswork's link policy strips them at render time.
- Don't rely on a specific filename surviving — users may rename
  artifacts in Obsidian. Use frontmatter `title:` for stability.

## See also

- `UBIQUITOUS_LANGUAGE.md` — definitions for **Artifact** and
  **Artifacts section**.
- `docs/adr/0003-artifact-markdown-rendering.md` — how rendering works
  and what the link policy allows.
- `wiki/decisions/glasswork-artifacts-prd.md` — the full PRD.
