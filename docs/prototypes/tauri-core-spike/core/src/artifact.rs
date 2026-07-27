//! Bounded port of `Glasswork.Core.Models.ArtifactKind` / `ArtifactLinkPolicy`,
//! scoped to exactly the two Artifact kinds #370's fixture requires: one
//! Markdown artifact through the shared renderer, one untrusted HTML artifact
//! through a sandboxed preview. This is the safe-untrusted-HTML boundary the
//! ticket calls out explicitly.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArtifactKind {
    Markdown,
    Html,
    Other,
}

pub fn classify_kind(filename: &str) -> ArtifactKind {
    let lower = filename.to_ascii_lowercase();
    if lower.ends_with(".md") || lower.ends_with(".markdown") {
        ArtifactKind::Markdown
    } else if lower.ends_with(".html") || lower.ends_with(".htm") {
        ArtifactKind::Html
    } else {
        ArtifactKind::Other
    }
}

/// Content-Security-Policy applied to the sandboxed HTML artifact preview.
/// Paired at the call site with an `<iframe sandbox="allow-same-origin">`
/// (script explicitly NOT allowed) so the untrusted document can render its
/// own styling but cannot execute script, reach the network, or navigate the
/// parent window/document -- the genuine-sandbox hard gate in the scorecard.
pub fn sandbox_csp() -> &'static str {
    "default-src 'none'; style-src 'unsafe-inline'; img-src data: blob:; \
     script-src 'none'; connect-src 'none'; frame-ancestors 'none'; form-action 'none';"
}
