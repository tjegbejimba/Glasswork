//! Bounded port of `Glasswork.Core.Services.FileWatcherService`: watches a
//! Vault folder for external `.md` edits (Obsidian, an agent, `git pull`) and
//! emits one change event per affected file so the My Day row can update
//! without an app restart -- exactly the "Confirm Tailscale ACL update"
//! fixture's live file-watch parity requirement (#370).

use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{channel, Receiver};

pub struct TaskFileChange {
    pub path: PathBuf,
}

/// Keeps the underlying OS watch handle alive for as long as the caller holds
/// this value; dropping it stops the watch.
pub struct WatchHandle(#[allow(dead_code)] RecommendedWatcher);

pub fn watch(vault_dir: &Path) -> notify::Result<(WatchHandle, Receiver<TaskFileChange>)> {
    let (tx, rx) = channel();

    let mut watcher = notify::recommended_watcher(move |res: notify::Result<Event>| {
        if let Ok(event) = res {
            if matches!(
                event.kind,
                EventKind::Modify(_) | EventKind::Create(_)
            ) {
                for path in event.paths {
                    if path.extension().and_then(|e| e.to_str()) == Some("md") {
                        let _ = tx.send(TaskFileChange { path });
                    }
                }
            }
        }
    })?;

    watcher.watch(vault_dir, RecursiveMode::NonRecursive)?;

    Ok((WatchHandle(watcher), rx))
}
