//! TDD seam: `watcher::watch` — the public "notify me when a task file
//! changes on disk" contract that backs My Day's live file-watch parity
//! requirement (#370's "Confirm Tailscale ACL update" fixture task).

use glasswork_core_spike::watcher;
use std::fs;
use std::time::Duration;

#[test]
fn detects_external_edit_to_a_watched_file() {
    let dir = tempfile::tempdir().unwrap();
    let file_path = dir.path().join("confirm-tailscale-acl.md");
    fs::write(&file_path, "---\nid: confirm-tailscale-acl\ntitle: Confirm Tailscale ACL update\nstatus: todo\npriority: medium\ncreated: 2026-07-20\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n").unwrap();

    let (_watch_handle, rx) = watcher::watch(dir.path()).expect("watcher starts");

    // Simulate an external frontmatter edit (e.g. Obsidian or an agent).
    std::thread::sleep(Duration::from_millis(200));
    fs::write(&file_path, "---\nid: confirm-tailscale-acl\ntitle: Confirm Tailscale ACL update\nstatus: in_progress\npriority: medium\ncreated: 2026-07-20\n---\n\n## Subtasks\n\n## Notes\n\n## Related\n").unwrap();

    let event = rx
        .recv_timeout(Duration::from_secs(5))
        .expect("a change event arrives without restarting anything");
    assert!(event.path.ends_with("confirm-tailscale-acl.md"));
}
