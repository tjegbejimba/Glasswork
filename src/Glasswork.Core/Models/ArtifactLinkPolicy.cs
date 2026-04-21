using System;

namespace Glasswork.Core.Models;

/// <summary>
/// Decides whether a hyperlink found in an artifact body may be opened.
/// Artifact content is treated as untrusted (agents wrote it, possibly autonomously),
/// so anything outside an explicit allow-list is blocked.
/// </summary>
public static class ArtifactLinkPolicy
{
    public enum Decision
    {
        Allow,
        Block,
    }

    public static Decision Decide(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Decision.Block;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return Decision.Block;

        var scheme = uri.Scheme.ToLowerInvariant();
        return scheme switch
        {
            "http" or "https" or "obsidian" => Decision.Allow,
            _ => Decision.Block,
        };
    }
}
