//! TDD seam: the **serialized task payload** the Presentation layer consumes
//! over IPC. Per `CONTEXT.md` ("Presentation holds no domain logic"), the
//! My Day row-form decision (`is_rich` / `show_as_card`) and the blocked
//! derivation are the Core's to make — the frontend must read them, never
//! recompute them. These tests pin that they actually cross the wire, which
//! is what the duplicated JS copies in `src/main.js` were papering over.

use glasswork_core_spike::model::{GlassworkTask, SubTask, TaskView};

fn subtask(text: &str, status: Option<&str>) -> SubTask {
    SubTask {
        text: text.to_string(),
        is_completed: false,
        status: status.map(|s| s.to_string()),
        ..Default::default()
    }
}

fn json_of(task: &GlassworkTask) -> serde_json::Value {
    serde_json::to_value(TaskView::of(task)).unwrap()
}

#[test]
fn serialized_payload_exposes_the_row_form_derivations() {
    let task = GlassworkTask {
        id: "budget-q3-review".into(),
        title: "Budget Q3 review".into(),
        status: "in_progress".into(),
        subtasks: vec![subtask("Pull actuals", Some("in_progress"))],
        ..Default::default()
    };

    let json = json_of(&task);

    assert_eq!(json["is_rich"], serde_json::json!(true));
    assert_eq!(json["show_as_card"], serde_json::json!(true));
    assert_eq!(json["is_blocked"], serde_json::json!(false));
}

#[test]
fn a_task_with_no_subtasks_and_no_notes_is_quiet_not_a_card() {
    let task = GlassworkTask {
        id: "renew-domain".into(),
        title: "Renew domain registration".into(),
        status: "todo".into(),
        priority: "low".into(),
        ..Default::default()
    };

    let json = json_of(&task);

    assert_eq!(json["is_rich"], serde_json::json!(false));
    assert_eq!(json["show_as_card"], serde_json::json!(false));
}

#[test]
fn a_rich_task_that_is_done_stops_showing_as_a_card() {
    let task = GlassworkTask {
        id: "budget-q3-review".into(),
        status: "done".into(),
        subtasks: vec![subtask("Pull actuals", Some("done"))],
        ..Default::default()
    };

    let json = json_of(&task);

    assert_eq!(json["is_rich"], serde_json::json!(true));
    assert_eq!(json["show_as_card"], serde_json::json!(false));
}

#[test]
fn notes_alone_are_enough_to_make_a_task_rich() {
    let task = GlassworkTask {
        id: "confirm-tailscale-acl".into(),
        status: "todo".into(),
        notes: "Waiting on the ACL diff to land.".into(),
        ..Default::default()
    };

    assert_eq!(json_of(&task)["is_rich"], serde_json::json!(true));
}

#[test]
fn blocked_status_crosses_the_wire_as_a_derived_flag() {
    let task = GlassworkTask {
        id: "confirm-tailscale-acl".into(),
        status: "blocked".into(),
        ..Default::default()
    };

    assert_eq!(json_of(&task)["is_blocked"], serde_json::json!(true));
}

#[test]
fn the_view_still_carries_every_underlying_task_field() {
    // The derivations are additive: flattening must not drop the fields the
    // frontend already renders, or Task Detail silently loses content.
    let task = GlassworkTask {
        id: "budget-q3-review".into(),
        title: "Budget Q3 review".into(),
        status: "in_progress".into(),
        priority: "high".into(),
        due: Some("2026-07-28".into()),
        description: "Reconcile Q3 actuals.".into(),
        notes: "Ping finance first.".into(),
        subtasks: vec![subtask("Pull actuals", Some("in_progress"))],
        ..Default::default()
    };

    let json = json_of(&task);

    assert_eq!(json["id"], serde_json::json!("budget-q3-review"));
    assert_eq!(json["title"], serde_json::json!("Budget Q3 review"));
    assert_eq!(json["priority"], serde_json::json!("high"));
    assert_eq!(json["due"], serde_json::json!("2026-07-28"));
    assert_eq!(json["description"], serde_json::json!("Reconcile Q3 actuals."));
    assert_eq!(json["notes"], serde_json::json!("Ping finance first."));
    assert_eq!(json["subtasks"][0]["text"], serde_json::json!("Pull actuals"));
}
