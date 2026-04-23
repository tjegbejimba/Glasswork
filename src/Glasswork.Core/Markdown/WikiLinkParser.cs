using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Glasswork.Core.Markdown;

/// <summary>
/// Canonical Obsidian wiki-link pattern. Matches <c>[[stem]]</c> and
/// <c>[[stem|display]]</c>. Stem is group 1 (raw, may have surrounding
/// whitespace — callers must trim). Display is group 2 (optional).
///
/// Single source of truth: <see cref="Services.BacklinkIndex"/> and
/// <see cref="Services.FrontmatterParser"/> reference <see cref="Pattern"/>
/// in their <c>[GeneratedRegex]</c> attributes so the literal can never
/// drift out of sync.
/// </summary>
public static partial class WikiLinkParser
{
    public const string Pattern = @"\[\[([^\]\|]+?)(?:\|([^\]]+))?\]\]";

    [GeneratedRegex(Pattern)]
    private static partial Regex Regex();

    /// <summary>
    /// Returns every wiki-link occurrence in <paramref name="text"/> with
    /// trimmed stem and display. Empty/whitespace stems are skipped.
    /// </summary>
    public static IReadOnlyList<WikiLinkMatch> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return System.Array.Empty<WikiLinkMatch>();
        var results = new List<WikiLinkMatch>();
        foreach (Match m in Regex().Matches(text))
        {
            var stem = m.Groups[1].Value.Trim();
            if (stem.Length == 0) continue;
            var display = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            if (display is { Length: 0 }) display = null;
            results.Add(new WikiLinkMatch(m.Index, m.Length, stem, display));
        }
        return results;
    }
}

public readonly record struct WikiLinkMatch(int Index, int Length, string Stem, string? Display);
