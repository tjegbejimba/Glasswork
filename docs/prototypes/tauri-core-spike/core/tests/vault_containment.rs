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
