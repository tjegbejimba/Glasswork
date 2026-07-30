// PROTOTYPE ONLY -- Wayfinder ticket #372. Confirms the bounded Rust Core's
// obsidian:// URI builder produces the same shape as production's
// Glasswork.Core.Models.ObsidianUriBuilder.ForVaultRelativePath: this is
// part of the shared vertical slice (#370) -- "open in Obsidian" must
// deep-link into the vault, not just open the raw file with the OS default
// handler for .md.
use glasswork_core_spike::obsidian_uri::for_vault_relative_path;

#[test]
fn builds_deep_link_for_a_task_file_in_the_vault_root() {
    let uri = for_vault_relative_path("/Users/tj/vaults/fixture-vault", "budget-q3-review.md");
    assert_eq!(
        uri,
        Some("obsidian://open?vault=fixture-vault&file=budget-q3-review".to_string())
    );
}

#[test]
fn drops_the_md_extension_but_preserves_other_extensions() {
    let uri = for_vault_relative_path(
        "/Users/tj/vaults/fixture-vault",
        "budget-q3-review.artifacts/report.html",
    );
    assert_eq!(
        uri,
        Some(
            "obsidian://open?vault=fixture-vault&file=budget-q3-review.artifacts/report.html"
                .to_string()
        )
    );
}

#[test]
fn rejects_paths_that_escape_the_vault_root() {
    let uri = for_vault_relative_path("/Users/tj/vaults/fixture-vault", "../../etc/passwd");
    assert_eq!(uri, None);
}

#[test]
fn refuses_an_artifact_path_that_escapes_the_vault_via_traversal() {
    // The IPC layer now passes a caller-supplied Vault-relative path (so the
    // Artifact row's "Open externally" can target the Artifact rather than its
    // parent task). That widens the input, so the escape check matters more:
    // a traversal must yield None and make the command fail loudly rather than
    // deep-linking outside the Vault.
    assert_eq!(
        for_vault_relative_path(
            "/vault/fixture-vault",
            "budget-q3-review.artifacts/../../../etc/passwd"
        ),
        None
    );
}
