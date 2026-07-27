// PROTOTYPE ONLY -- Wayfinder ticket #372. Bounded port of
// `Glasswork.Core.Models.ObsidianUriBuilder.ForVaultRelativePath`. Builds an
// `obsidian://open?vault=<name>&file=<path>` deep link so "open in Obsidian"
// launches the actual vault + file, matching production behavior, instead
// of merely opening the raw markdown file with the OS default handler.
use std::path::{Component, Path, PathBuf};

/// Lexically normalize a path (resolve `.` and `..` segments) without
/// touching the filesystem -- mirrors .NET's `Path.GetFullPath`, which does
/// not require the path to exist. `std::fs::canonicalize` isn't usable here
/// because the vault/task paths in tests (and some real launches) may not
/// exist relative to the process's actual CWD.
fn lexically_normalize(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for component in path.components() {
        match component {
            Component::ParentDir => {
                out.pop();
            }
            Component::CurDir => {}
            other => out.push(other.as_os_str()),
        }
    }
    out
}

/// Build a URI that opens a vault-relative markdown (or artifact) path in
/// Obsidian. The vault name is derived from the leaf folder name of
/// `vault_root`. Returns `None` if the resolved path escapes the vault root.
pub fn for_vault_relative_path(vault_root: &str, vault_relative_path: &str) -> Option<String> {
    if vault_root.trim().is_empty() || vault_relative_path.trim().is_empty() {
        return None;
    }

    let root_full = lexically_normalize(Path::new(vault_root));
    let file_full = lexically_normalize(&root_full.join(vault_relative_path));

    let relative = file_full.strip_prefix(&root_full).ok()?;
    if relative.as_os_str().is_empty() || relative.components().next() == Some(Component::ParentDir)
    {
        return None;
    }

    let vault_name = root_full.file_name()?.to_string_lossy().to_string();
    build_uri(&vault_name, relative)
}

fn build_uri(vault_name: &str, relative_path: &Path) -> Option<String> {
    let mut relative = relative_path.to_string_lossy().replace('\\', "/");
    if let Some(stripped) = relative.strip_suffix(".md") {
        relative = stripped.to_string();
    }

    let encoded_path = relative
        .split('/')
        .filter(|s| !s.is_empty())
        .map(urlencode_segment)
        .collect::<Vec<_>>()
        .join("/");
    if encoded_path.is_empty() {
        return None;
    }

    Some(format!(
        "obsidian://open?vault={}&file={}",
        urlencode_segment(vault_name),
        encoded_path
    ))
}

/// Minimal percent-encoding sufficient for vault names and path segments
/// (no external dependency needed for this bounded spike).
fn urlencode_segment(segment: &str) -> String {
    let mut out = String::new();
    for byte in segment.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(byte as char)
            }
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}
