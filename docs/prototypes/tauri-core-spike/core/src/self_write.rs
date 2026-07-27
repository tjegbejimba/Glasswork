//! Bounded port of `Glasswork.Core.Services.SelfWriteCoordinator`: suppresses
//! watcher echoes from writes the app itself just made, so toggling a
//! subtask or reordering doesn't fire a spurious "external change" event on
//! top of the deliberate one. Any code in this spike that writes the Vault
//! must register the write here first (mirrors CONTEXT.md's cross-cutting rule).

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, Instant};

pub struct SelfWriteCoordinator {
    inner: Mutex<HashSet<PathBuf>>,
    ttl: Duration,
    expirations: Mutex<Vec<(PathBuf, Instant)>>,
}

impl Default for SelfWriteCoordinator {
    fn default() -> Self {
        Self::new(Duration::from_secs(2))
    }
}

impl SelfWriteCoordinator {
    pub fn new(ttl: Duration) -> Self {
        Self {
            inner: Mutex::new(HashSet::new()),
            ttl,
            expirations: Mutex::new(Vec::new()),
        }
    }

    /// Call immediately before writing `path` from within the app.
    pub fn mark_self_write(&self, path: &Path) {
        let owned = path.to_path_buf();
        self.inner.lock().unwrap().insert(owned.clone());
        self.expirations.lock().unwrap().push((owned, Instant::now() + self.ttl));
    }

    /// Call from the watcher callback: true means suppress (this was our own
    /// write, echoed back by the OS), false means a genuine external change.
    pub fn should_suppress(&self, path: &Path) -> bool {
        self.reap_expired();
        self.inner.lock().unwrap().remove(path)
    }

    fn reap_expired(&self) {
        let now = Instant::now();
        let mut expirations = self.expirations.lock().unwrap();
        let mut inner = self.inner.lock().unwrap();
        expirations.retain(|(path, expires_at)| {
            if *expires_at <= now {
                inner.remove(path);
                false
            } else {
                true
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn suppresses_a_change_it_just_marked_as_its_own_write() {
        let coordinator = SelfWriteCoordinator::default();
        let path = PathBuf::from("/vault/budget-q3-review.md");
        coordinator.mark_self_write(&path);
        assert!(coordinator.should_suppress(&path));
    }

    #[test]
    fn does_not_suppress_a_change_it_never_marked() {
        let coordinator = SelfWriteCoordinator::default();
        let path = PathBuf::from("/vault/confirm-tailscale-acl.md");
        assert!(!coordinator.should_suppress(&path));
    }

    #[test]
    fn a_suppression_is_single_use_so_the_next_real_external_edit_still_fires() {
        let coordinator = SelfWriteCoordinator::default();
        let path = PathBuf::from("/vault/budget-q3-review.md");
        coordinator.mark_self_write(&path);
        assert!(coordinator.should_suppress(&path));
        assert!(!coordinator.should_suppress(&path));
    }
}
