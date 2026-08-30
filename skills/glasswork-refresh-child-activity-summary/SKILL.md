---
name: glasswork-refresh-child-activity-summary
description: 'Refresh a Parent Task Child activity summary without changing lifecycle state. Use when the user pastes "Refresh Child activity summary for Glasswork task: <task-id>" or asks to refresh a Parent Task summary.'
---

# Glasswork - Refresh Child Activity Summary

Refresh Child activity summary for Glasswork task: <task-id> is a lifecycle-neutral command. It rebuilds the Parent Task's stable rolling summary Artifact and does not change Task lifecycle status, descendant status, hierarchy, or session state.

The Task's persisted normalized type is authoritative. If the Task is missing or is not a Parent Task, stop with a precise `task_not_found` or `not_parent` outcome. See [the session launch guidance](../../docs/research/copilot-session-launch.md); this command never launches or coordinates child sessions.

## Process

1. Call `get_child_activity_summary_context` with the Parent Task ID.
2. If capture fails, report its exact error code and message. Do not write a generic Artifact as a fallback.
3. Generate concise Markdown grouped by direct child using only the returned payload. Do not use chat transcripts, email, Teams, WorkIQ, ambient Vault context, or other conversational state.
4. Call `refresh_child_activity_summary` with the exact returned `parent_revision`, `descendant_count`, `read_basis`, `expected_summary_revision`, and `generated_at`, plus the generated content and a new idempotency key.
5. If the commit reports a stale read basis or Resource Revision conflict, discard the generated content, repeat capture, regenerate from the new payload, and retry once with a new idempotency key. Surface any subsequent failure exactly.
6. Report the resulting Artifact path, descendant count, and generation time. Do not mutate status or append a lifecycle Notes entry.

Never write `child-activity-summary.md` through generic file or Artifact APIs; its guarded refresh contract owns that reserved Artifact.
