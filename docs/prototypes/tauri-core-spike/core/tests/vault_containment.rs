//! TDD seam: resolving a caller-supplied Artifact path against the Vault.
//!
//! The IPC layer takes an Artifact filename from the frontend, so the Vault
//! root is a trust boundary: a traversal or absolute path must not be able to
//! read a file outside the Vault. `Path::join` is dangerous here -- joining an
//! absolute path silently discards the base -- so containment has to be
//! checked explicitly rather than assumed from the join.

use glasswork_core_spike::vault;
use std::path::Path;

#[test]
fn resolves_a_normal_artifact_file_inside_the_vault() {
    let resolved = vault::resolve_contained(
        Path::new("/vault/fixture-vault"),
        "budget-q3-review.artifacts",
        "report.html",
    );
    assert_eq!(
        resolved,
        Some(Path::new("/vault/fixture-vault/budget-q3-review.artifacts/report.html").to_path_buf())
    );
}

#[test]
fn refuses_a_parent_traversal_in_the_filename() {
    assert_eq!(
        vault::resolve_contained(
            Path::new("/vault/fixture-vault"),
            "budget-q3-review.artifacts",
            "../../../etc/passwd",
        ),
        None
    );
}

#[test]
fn refuses_an_absolute_filename() {
    // The dangerous case: `join` on an absolute path throws the base away, so
    // without an explicit check this would happily read /etc/passwd.
    assert_eq!(
        vault::resolve_contained(
            Path::new("/vault/fixture-vault"),
            "budget-q3-review.artifacts",
            "/etc/passwd",
        ),
        None
    );
}

#[test]
fn refuses_a_traversal_in_the_artifact_folder_segment() {
    assert_eq!(
        vault::resolve_contained(
            Path::new("/vault/fixture-vault"),
            "../../../etc",
            "passwd",
        ),
        None
    );
}

#[test]
fn refuses_an_empty_filename() {
    assert_eq!(
        vault::resolve_contained(
            Path::new("/vault/fixture-vault"),
            "budget-q3-review.artifacts",
            "",
        ),
        None
    );
}

#[test]
fn allows_a_nested_subfolder_that_stays_inside_the_vault() {
    let resolved = vault::resolve_contained(
        Path::new("/vault/fixture-vault"),
        "budget-q3-review.artifacts",
        "nested/report.html",
    );
    assert_eq!(
        resolved,
        Some(
            Path::new("/vault/fixture-vault/budget-q3-review.artifacts/nested/report.html")
                .to_path_buf()
        )
    );
}

// --- Symlink escape ------------------------------------------------------
// Lexical containment alone cannot see through a symlink: a link that lives
// *inside* the Vault but points outside passes every `..`/absolute check and
// still reads foreign content once opened. Closing that needs the real
// filesystem, so it is a separate function from the pure lexical one.

#[test]
fn canonical_contained_accepts_a_real_file_inside_the_vault() {
    let vault = tempfile::tempdir().unwrap();
    let artifacts = vault.path().join("budget-q3-review.artifacts");
    std::fs::create_dir_all(&artifacts).unwrap();
    std::fs::write(artifacts.join("report.html"), "<p>ok</p>").unwrap();

    let resolved = vault::canonical_contained(
        vault.path(),
        "budget-q3-review.artifacts",
        "report.html",
    );
    assert!(resolved.is_some(), "a genuine in-vault artifact must resolve");
}

#[cfg(unix)]
#[test]
fn canonical_contained_refuses_a_symlink_that_escapes_the_vault() {
    let vault = tempfile::tempdir().unwrap();
    let outside = tempfile::tempdir().unwrap();
    std::fs::write(outside.path().join("secret.txt"), "TOP SECRET").unwrap();

    let artifacts = vault.path().join("budget-q3-review.artifacts");
    std::fs::create_dir_all(&artifacts).unwrap();
    // Lives inside the Vault, points outside it.
    std::os::unix::fs::symlink(
        outside.path().join("secret.txt"),
        artifacts.join("leak.html"),
    )
    .unwrap();

    assert_eq!(
        vault::canonical_contained(vault.path(), "budget-q3-review.artifacts", "leak.html"),
        None,
        "a symlink pointing outside the Vault must be refused"
    );
}

#[cfg(unix)]
#[test]
fn canonical_contained_allows_a_symlink_that_stays_inside_the_vault() {
    let vault = tempfile::tempdir().unwrap();
    let artifacts = vault.path().join("budget-q3-review.artifacts");
    std::fs::create_dir_all(&artifacts).unwrap();
    std::fs::write(artifacts.join("real.html"), "<p>ok</p>").unwrap();
    std::os::unix::fs::symlink(artifacts.join("real.html"), artifacts.join("alias.html")).unwrap();

    assert!(
        vault::canonical_contained(vault.path(), "budget-q3-review.artifacts", "alias.html")
            .is_some(),
        "a symlink resolving back inside the Vault is legitimate and must be allowed"
    );
}

#[test]
fn canonical_contained_refuses_a_lexical_traversal_too() {
    let vault = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(vault.path().join("budget-q3-review.artifacts")).unwrap();

    assert_eq!(
        vault::canonical_contained(vault.path(), "budget-q3-review.artifacts", "../../etc/passwd"),
        None
    );
}

#[test]
fn canonical_contained_refuses_a_missing_file_rather_than_assuming_it_is_safe() {
    // Containment is decided from the *real* path, so a file that isn't there
    // has no real path to check. Refusing is the conservative answer: it must
    // not fall through to "no traversal detected, therefore fine".
    let vault = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(vault.path().join("budget-q3-review.artifacts")).unwrap();

    assert_eq!(
        vault::canonical_contained(
            vault.path(),
            "budget-q3-review.artifacts",
            "does-not-exist.html"
        ),
        None
    );
}

#[test]
fn canonical_contained_refuses_a_sibling_dir_whose_name_merely_prefixes_the_vault() {
    // `starts_with` on Path compares whole components, so "/tmp/vault-evil"
    // must not count as inside "/tmp/vault". Pinning this because a naive
    // string-prefix implementation would wrongly allow it.
    let base = tempfile::tempdir().unwrap();
    let vault_root = base.path().join("vault");
    let evil_sibling = base.path().join("vault-evil");
    std::fs::create_dir_all(vault_root.join("budget-q3-review.artifacts")).unwrap();
    std::fs::create_dir_all(&evil_sibling).unwrap();
    std::fs::write(evil_sibling.join("secret.txt"), "TOP SECRET").unwrap();

    #[cfg(unix)]
    std::os::unix::fs::symlink(
        evil_sibling.join("secret.txt"),
        vault_root.join("budget-q3-review.artifacts").join("leak.html"),
    )
    .unwrap();

    assert_eq!(
        vault::canonical_contained(&vault_root, "budget-q3-review.artifacts", "leak.html"),
        None
    );
}
