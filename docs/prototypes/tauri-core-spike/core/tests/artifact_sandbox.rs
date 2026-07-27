//! TDD seam: `artifact::classify_kind` / `artifact::sandbox_policy` — mirrors
//! `ArtifactKind`/`ArtifactLinkPolicy` from `Glasswork.Core.Models`, bounded to
//! just Markdown and Html (the two kinds #370's fixture requires).

use glasswork_core_spike::artifact::{classify_kind, sandbox_csp, ArtifactKind};

#[test]
fn classifies_markdown_extension() {
    assert_eq!(classify_kind("plan.md"), ArtifactKind::Markdown);
}

#[test]
fn classifies_html_extension() {
    assert_eq!(classify_kind("report.html"), ArtifactKind::Html);
    assert_eq!(classify_kind("report.htm"), ArtifactKind::Html);
}

#[test]
fn unknown_extension_is_other() {
    assert_eq!(classify_kind("data.bin"), ArtifactKind::Other);
}

#[test]
fn html_sandbox_csp_blocks_script_and_network_and_framing_out() {
    let csp = sandbox_csp();
    // No script execution.
    assert!(csp.contains("script-src 'none'"));
    // No outbound network from the sandboxed document.
    assert!(csp.contains("connect-src 'none'"));
    assert!(csp.contains("default-src 'none'"));
    // No navigating the parent/opener.
    assert!(csp.contains("frame-ancestors 'none'"));
}
