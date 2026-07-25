# Meeting transcript sync

`meeting-transcript-sync` is the only v1 **Review source**. It turns normalized meeting recap records into Automation Review Queue proposals without carrying raw transcript text into Glasswork.

## Source contract

The normalized recap contract is intentionally narrow:

- stable meeting id
- meeting start date/time
- title
- organizer
- attendance (`attended`, `unknown`, `not-attended`)
- one usable Teams meeting / recap URL
- grounded summary
- decisions
- action items

Raw transcript text, verbatim speaker turns, and opaque WorkIQ response blobs are out of bounds for the adapter, the unmatched-meeting state file, and queue submissions.

## Matching and proposals

Automatic Task qualification requires both:

1. one deterministic anchor (`Task ID`, exact `Task` title, linked `ADO` / `PR` identifier, or unique project term), and
2. one separate corroborator from Task Description, Notes, subtasks, tags, or Links.

Semantic overlap and organizer/attendee overlap can help rank already-qualified matches, but they never qualify a Task on their own.

Allowed proposal types are:

- meeting note
- status change
- block Task
- unblock Task
- blocker reason change
- due date change
- subtask addition
- structured Link addition

Forbidden fields are never emitted by this source: Task title, Description, parent, Task type, priority, My Day, and scheduled date.

## Privacy and trust notes

- Queue submissions and unmatched-meeting state persist only bounded recap fields.
- Missing or unusable meeting URLs are skipped with diagnostics instead of creating a blind Review item.
- Explicitly not-attended meetings can still qualify, but their Review items carry a visible attendance label.
- Unmatched meetings are retained locally for seven days for manual attachment, then expire into source-owned dedupe state so replayed old meetings do not resurface indefinitely.
- Manual attachment bypasses only automatic Task matching. It does not weaken any proposal evidence rule.

## Production gate

Production WorkIQ ingestion remains disabled in code until #391. Fixture-backed adapters are the only supported way to exercise the source end-to-end in this slice.
