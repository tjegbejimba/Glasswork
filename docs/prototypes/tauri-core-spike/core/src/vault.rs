//! Bounded port of `Glasswork.Core.Services.VaultService.LoadAll`: reads every
//! `.md` file directly under a Vault folder and parses the ones that are
//! Glasswork task files (have frontmatter). Non-task markdown (the user's
//! other Obsidian wiki notes) is silently skipped rather than failing the
//! batch, matching CONTEXT.md's "vault is also the user's personal wiki" rule.

use crate::model::GlassworkTask;
use crate::parser;
use std::io;
use std::path::Path;

pub fn load_all(vault_dir: &Path) -> io::Result<Vec<GlassworkTask>> {
    let mut tasks = Vec::new();
    for entry in std::fs::read_dir(vault_dir)? {
        let entry = entry?;
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("md") {
            continue;
        }
        let content = std::fs::read_to_string(&path)?;
        if let Ok(task) = parser::parse(&content) {
            tasks.push(task);
        }
    }
    Ok(tasks)
}
