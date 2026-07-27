//! PROTOTYPE ONLY -- Wayfinder ticket #372. Bounded Rust port of the shared
//! vertical slice (My Day, Task Detail, reserved Planner stub) from
//! `Glasswork.Core`. Scope limited to exactly the behavior the fixed 3-task
//! fixture in ticket #370 needs: frontmatter parse/serialize round-trip,
//! task/subtask model, vault load, and file-watch.

pub mod artifact;
pub mod model;
pub mod obsidian_uri;
pub mod parser;
pub mod self_write;
pub mod vault;
pub mod watcher;

pub use model::{GlassworkTask, SubTask};
pub use self_write::SelfWriteCoordinator;
