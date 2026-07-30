//! Bounded task/subtask model — a subset of `Glasswork.Core.Models.GlassworkTask`
//! / `SubTask` covering only the fields the shared vertical slice (#370) reads:
//! My Day row rendering (rich card vs quiet), Task Detail (Description, Notes,
//! subtasks with drag-reorder, blocked metadata), and the fixture's chips
//! (priority, due, ADO). Field names mirror the C# Core exactly so a future
//! reader can diff the two contracts at a glance.

use std::collections::HashMap;

#[derive(Debug, Clone, PartialEq, Eq, Default, serde::Serialize)]
pub struct SubTask {
    pub text: String,
    pub is_completed: bool,
    pub status: Option<String>,
    pub metadata: HashMap<String, String>,
    pub notes: String,
}

impl SubTask {
    /// Mirrors `SubTask.IsEffectivelyDone`: an explicit `status` wins over the
    /// checkbox character; falls back to the checkbox when status is absent.
    pub fn is_effectively_done(&self) -> bool {
        match self.status.as_deref() {
            Some("done") | Some("dropped") => true,
            None => self.is_completed,
            _ => false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default, serde::Serialize)]
pub struct GlassworkTask {
    pub id: String,
    pub title: String,
    pub status: String,
    pub priority: String,
    pub created: Option<String>,
    pub due: Option<String>,
    pub ado_title: Option<String>,
    pub blocked_reason: Option<String>,
    pub blocked_at: Option<String>,
    pub blocked_from_status: Option<String>,
    pub description: String,
    pub notes: String,
    pub subtasks: Vec<SubTask>,
}

impl GlassworkTask {
    /// Mirrors `GlassworkTask.IsRich`: has explicit status/metadata/notes
    /// depth beyond a bare title -- here approximated by "has any subtasks or
    /// notes", which is exactly what distinguishes the fixture's card task
    /// from its two quiet tasks.
    pub fn is_rich(&self) -> bool {
        !self.subtasks.is_empty() || !self.notes.trim().is_empty()
    }

    /// Mirrors `GlassworkTask.ShowAsCard` for the fixture's two states: a
    /// rich task not yet done renders as a card; everything else is quiet.
    pub fn show_as_card(&self) -> bool {
        self.is_rich() && self.status != "done"
    }

    pub fn is_blocked(&self) -> bool {
        self.status == "blocked"
    }
}

/// The serialized shape the Presentation layer receives over IPC.
///
/// `CONTEXT.md` puts domain derivations in the Core, not in Presentation, so
/// the My Day row-form decision travels *with* the task rather than being
/// recomputed by whatever UI happens to be rendering it. Borrowing + flatten
/// keeps the methods above the single source of truth — there is no second
/// copy of the rule to drift.
#[derive(Debug, serde::Serialize)]
pub struct TaskView<'a> {
    #[serde(flatten)]
    task: &'a GlassworkTask,
    is_rich: bool,
    show_as_card: bool,
    is_blocked: bool,
}

impl<'a> TaskView<'a> {
    pub fn of(task: &'a GlassworkTask) -> Self {
        Self {
            task,
            is_rich: task.is_rich(),
            show_as_card: task.show_as_card(),
            is_blocked: task.is_blocked(),
        }
    }
}
