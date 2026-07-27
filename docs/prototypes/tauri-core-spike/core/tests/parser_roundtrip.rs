//! TDD seam: `parser::parse` / `parser::serialize` — the public frontmatter
//! round-trip contract the bounded Core must preserve from the real
//! `Glasswork.Core` Vault file format (see `FrontmatterParser.cs`).

use glasswork_core_spike::parser;

const BUDGET_TASK_MD: &str = "---\n\
id: budget-q3-review\n\
title: Prepare Q3 budget review\n\
status: in_progress\n\
priority: high\n\
created: 2026-07-20\n\
due: 2026-07-24\n\
ado_title: 'ADO #4821'\n\
---\n\
\n\
Quarterly budget review for the NAS hosting line items.\n\
\n\
## Subtasks\n\
\n\
### [x] Collect Q2 actuals\n\
- status: done\n\
\n\
### [ ] Reconcile NAS hosting costs\n\
- status: in_progress\n\
\n\
### [ ] Get sign-off from manager\n\
- status: blocked\n\
- blocker: waiting on manager availability until Thursday\n\
\n\
### [ ] Send summary to finance\n\
- status: todo\n\
\n\
## Notes\n\
\n\
Waiting on manager Thursday standup.\n\
\n\
## Related\n\
\n";

#[test]
fn parses_frontmatter_fields() {
    let task = parser::parse(BUDGET_TASK_MD).expect("valid fixture parses");
    assert_eq!(task.id, "budget-q3-review");
    assert_eq!(task.title, "Prepare Q3 budget review");
    assert_eq!(task.status, "in_progress");
    assert_eq!(task.priority, "high");
    assert_eq!(task.due.as_deref(), Some("2026-07-24"));
    assert_eq!(task.ado_title.as_deref(), Some("ADO #4821"));
}

#[test]
fn parses_description_separately_from_subtasks_section() {
    let task = parser::parse(BUDGET_TASK_MD).unwrap();
    assert_eq!(
        task.description,
        "Quarterly budget review for the NAS hosting line items."
    );
}

#[test]
fn parses_four_subtasks_with_status_and_blocker_metadata() {
    let task = parser::parse(BUDGET_TASK_MD).unwrap();
    assert_eq!(task.subtasks.len(), 4);

    assert_eq!(task.subtasks[0].text, "Collect Q2 actuals");
    assert!(task.subtasks[0].is_completed);
    assert_eq!(task.subtasks[0].status.as_deref(), Some("done"));

    assert_eq!(task.subtasks[1].text, "Reconcile NAS hosting costs");
    assert_eq!(task.subtasks[1].status.as_deref(), Some("in_progress"));

    let blocked = &task.subtasks[2];
    assert_eq!(blocked.status.as_deref(), Some("blocked"));
    assert_eq!(
        blocked.metadata.get("blocker").map(String::as_str),
        Some("waiting on manager availability until Thursday")
    );

    assert_eq!(task.subtasks[3].status.as_deref(), Some("todo"));
}

#[test]
fn parses_notes_section() {
    let task = parser::parse(BUDGET_TASK_MD).unwrap();
    assert_eq!(task.notes, "Waiting on manager Thursday standup.");
}

#[test]
fn missing_frontmatter_delimiters_is_an_error() {
    let result = parser::parse("no frontmatter here");
    assert!(result.is_err());
}

#[test]
fn round_trips_through_serialize_then_parse() {
    let original = parser::parse(BUDGET_TASK_MD).unwrap();
    let serialized = parser::serialize(&original);
    let reparsed = parser::parse(&serialized).expect("serialized output re-parses");

    assert_eq!(reparsed.id, original.id);
    assert_eq!(reparsed.title, original.title);
    assert_eq!(reparsed.status, original.status);
    assert_eq!(reparsed.due, original.due);
    assert_eq!(reparsed.description, original.description);
    assert_eq!(reparsed.notes, original.notes);
    assert_eq!(reparsed.subtasks.len(), original.subtasks.len());
    for (a, b) in reparsed.subtasks.iter().zip(original.subtasks.iter()) {
        assert_eq!(a.text, b.text);
        assert_eq!(a.status, b.status);
        assert_eq!(a.metadata, b.metadata);
    }
}

#[test]
fn parses_blocked_task_metadata() {
    let blocked_md = "---\n\
id: t2\n\
title: Renew domain registration\n\
status: blocked\n\
priority: low\n\
created: 2026-07-20\n\
due: 2026-07-24\n\
blocked_reason: waiting on registrar\n\
blocked_at: '2026-07-23T10:00:00Z'\n\
blocked_from_status: todo\n\
---\n\
\n\
## Subtasks\n\
\n\
## Notes\n\
\n\
## Related\n\
\n";
    let task = parser::parse(blocked_md).unwrap();
    assert_eq!(task.status, "blocked");
    assert_eq!(task.blocked_reason.as_deref(), Some("waiting on registrar"));
    assert_eq!(task.blocked_from_status.as_deref(), Some("todo"));
}
