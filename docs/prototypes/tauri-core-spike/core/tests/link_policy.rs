//! TDD seam: the ArtifactLinkPolicy-equivalent gate for links embedded in
//! untrusted (agent-authored) Vault markdown.
//!
//! ADR 0006 routes rendered-markdown links through one policy: http/https are
//! allowed and launched as URLs; everything else is refused. The decision must
//! live in the Core rather than in the webview, because the webview is the
//! thing being defended -- a frontend-only check is bypassable by whatever
//! rendered the markup.

use glasswork_core_spike::artifact::is_allowed_external_url;

#[test]
fn allows_http_and_https() {
    assert!(is_allowed_external_url("https://example.com/report"));
    assert!(is_allowed_external_url("http://example.com/report"));
}

#[test]
fn is_case_insensitive_about_the_scheme() {
    assert!(is_allowed_external_url("HTTPS://example.com"));
    assert!(is_allowed_external_url("HtTp://example.com"));
}

#[test]
fn refuses_javascript_urls() {
    assert!(!is_allowed_external_url("javascript:alert(1)"));
    // Whitespace/control characters are a classic filter bypass.
    assert!(!is_allowed_external_url("  javascript:alert(1)"));
    assert!(!is_allowed_external_url("java\tscript:alert(1)"));
}

#[test]
fn refuses_file_and_data_and_other_schemes() {
    assert!(!is_allowed_external_url("file:///etc/passwd"));
    assert!(!is_allowed_external_url("data:text/html;base64,PHNjcmlwdD4="));
    assert!(!is_allowed_external_url("obsidian://open?vault=x&file=y"));
    assert!(!is_allowed_external_url("mailto:someone@example.com"));
}

#[test]
fn refuses_a_scheme_relative_or_bare_path() {
    assert!(!is_allowed_external_url("//example.com/evil"));
    assert!(!is_allowed_external_url("/etc/passwd"));
    assert!(!is_allowed_external_url(""));
}

// --- Beyond a scheme prefix ---------------------------------------------
// A prefix check alone accepts strings that are http(s)-shaped but not
// meaningfully a link, and forms whose displayed text misleads about the real
// destination. Since the result is handed to the OS to launch, the policy has
// to look at the authority too, not just the first seven characters.

#[test]
fn refuses_a_scheme_with_no_host() {
    assert!(!is_allowed_external_url("https://"));
    assert!(!is_allowed_external_url("http://"));
    assert!(!is_allowed_external_url("https:///path-only"));
}

#[test]
fn refuses_embedded_credentials_in_the_authority() {
    // Classic misdirection: the eye reads example.com, the browser goes to
    // evil.example.
    assert!(!is_allowed_external_url("https://example.com@evil.example/"));
    assert!(!is_allowed_external_url("https://user:pass@evil.example/"));
}

#[test]
fn refuses_backslashes_in_the_authority() {
    // Several resolvers normalize `\` to `/`, so `https://evil.example\@x`
    // can resolve somewhere other than it appears to.
    assert!(!is_allowed_external_url("https://example.com\\@evil.example/"));
    assert!(!is_allowed_external_url("http:/\\example.com/"));
}

#[test]
fn still_allows_ordinary_links_with_ports_paths_queries_and_fragments() {
    assert!(is_allowed_external_url("https://example.com"));
    assert!(is_allowed_external_url("https://example.com:8443/a/b?c=1#d"));
    assert!(is_allowed_external_url("http://localhost:3000/health"));
    assert!(is_allowed_external_url("https://sub.domain.example.com/path"));
}
