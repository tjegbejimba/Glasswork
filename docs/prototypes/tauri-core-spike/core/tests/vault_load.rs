//! TDD seam: `vault::load_all` — the public "read every task file in a Vault
//! folder" contract the bounded Core must support for My Day to render.

use glasswork_core_spike::vault;
use std::fs;

fn write_task(dir: &std::path::Path, filename: &str, content: &str) {
    fs::write(dir.join(filename), content).unwrap();
}

const QUIET_TASK_MD: &str = "---\n\
id: renew-domain\n\
title: Renew domain registration\n\
status: todo\n\
priority: low\n\
created: 2026-07-20\n\
due: 2026-07-24\n\
---\n\
\n\
## Subtasks\n\
\n\
## Notes\n\
\n\
## Related\n\
\n";

const WATCHER_TASK_MD: &str = "---\n\
id: confirm-tailscale-acl\n\
title: Confirm Tailscale ACL update\n\
status: todo\n\
priority: medium\n\
created: 2026-07-20\n\
due: 2026-07-24\n\
---\n\
\n\
## Subtasks\n\
\n\
## Notes\n\
\n\
## Related\n\
\n";

#[test]
fn loads_every_markdown_task_file_in_the_vault_folder() {
    let dir = tempfile::tempdir().unwrap();
    write_task(dir.path(), "renew-domain.md", QUIET_TASK_MD);
    write_task(dir.path(), "confirm-tailscale-acl.md", WATCHER_TASK_MD);
    // Non-task markdown (arbitrary Obsidian wiki note) must not crash the load,
    // mirroring "Vault is also the user's personal wiki" from CONTEXT.md --
    // but since it has no frontmatter it's skipped rather than erroring the batch.
    write_task(dir.path(), "random-wiki-note.md", "# Just a note\n\nNo frontmatter here.\n");

    let tasks = vault::load_all(dir.path()).expect("vault loads");

    let mut ids: Vec<&str> = tasks.iter().map(|t| t.id.as_str()).collect();
    ids.sort();
    assert_eq!(ids, vec!["confirm-tailscale-acl", "renew-domain"]);
}

#[test]
fn empty_vault_folder_loads_zero_tasks() {
    let dir = tempfile::tempdir().unwrap();
    let tasks = vault::load_all(dir.path()).unwrap();
    assert!(tasks.is_empty());
}
