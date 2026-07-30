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

/// Resolve a caller-supplied Artifact location against the Vault root,
/// returning `None` if the result would fall outside it.
///
/// The frontend supplies both the Artifact folder and the filename, so this
/// is a trust boundary. `Path::join` alone is unsafe here: joining an
/// absolute path silently discards the base, so `join("/etc/passwd")` escapes
/// without any `..` present. Containment is therefore checked explicitly,
/// lexically (no filesystem access), mirroring the same approach
/// `obsidian_uri` already takes for deep links.
///
/// **Lexical only.** This cannot see through symlinks, so it is *not*
/// sufficient on its own before reading a file — use [`canonical_contained`]
/// for that. This function is for cases where no read happens (e.g. building
/// a deep link), or as the first stage of the stricter check.
pub fn resolve_contained(
    vault_root: &Path,
    artifact_folder: &str,
    filename: &str,
) -> Option<std::path::PathBuf> {
    if artifact_folder.trim().is_empty() || filename.trim().is_empty() {
        return None;
    }

    let candidate = Path::new(artifact_folder).join(filename);
    if candidate.is_absolute() {
        return None;
    }

    let root = lexically_normalize(vault_root);
    let full = lexically_normalize(&root.join(&candidate));

    // `strip_prefix` succeeding is the containment proof; a leading `..` in
    // the remainder would mean the normalization walked back out of the root.
    let relative = full.strip_prefix(&root).ok()?;
    if relative.as_os_str().is_empty()
        || relative.components().next() == Some(std::path::Component::ParentDir)
    {
        return None;
    }

    Some(full)
}

fn lexically_normalize(path: &Path) -> std::path::PathBuf {
    let mut out = std::path::PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::ParentDir => {
                out.pop();
            }
            std::path::Component::CurDir => {}
            other => out.push(other.as_os_str()),
        }
    }
    out
}

/// Filesystem-level containment: like [`resolve_contained`], but also follows
/// symlinks and re-checks the *real* location.
///
/// [`resolve_contained`] is purely lexical, which cannot see through a
/// symlink that lives inside the Vault but points outside it — such a path
/// passes every `..`/absolute check and still yields foreign content when
/// opened. Any caller that is about to actually *read* the file must use this
/// function; the lexical one is only sufficient for building a link.
///
/// Both sides are canonicalized so the comparison is between real paths (this
/// also resolves a symlinked Vault root, e.g. macOS `/tmp` -> `/private/tmp`).
/// A path that does not exist is refused rather than assumed safe.
pub fn canonical_contained(
    vault_root: &Path,
    artifact_folder: &str,
    filename: &str,
) -> Option<std::path::PathBuf> {
    let lexical = resolve_contained(vault_root, artifact_folder, filename)?;

    let real_root = vault_root.canonicalize().ok()?;
    let real_path = lexical.canonicalize().ok()?;

    if real_path.starts_with(&real_root) {
        Some(real_path)
    } else {
        None
    }
}
