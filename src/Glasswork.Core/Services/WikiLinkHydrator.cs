using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Services;

/// <summary>
/// Resolves <see cref="RelatedLink"/> references against the wiki on disk and produces
/// <see cref="HydratedRelatedLink"/> view-models with title/type/created pulled from the
/// target page's YAML frontmatter. Missing files render as placeholder cards (per D10).
/// </summary>
public partial class WikiLinkHydrator
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    /// <summary>
    /// Hydrate each link by reading the target file's frontmatter from <paramref name="wikiRoot"/>.
    /// The wiki root is the Obsidian vault root (e.g. <c>~/Wiki/wiki/</c>); slugs are interpreted
    /// as paths relative to that root with <c>.md</c> appended.
    /// </summary>
    public List<HydratedRelatedLink> Hydrate(IEnumerable<RelatedLink> links, string wikiRoot)
    {
        var result = new List<HydratedRelatedLink>();
        foreach (var link in links)
        {
            result.Add(HydrateOne(link, wikiRoot));
        }
        return result;
    }

    private static HydratedRelatedLink HydrateOne(RelatedLink link, string wikiRoot)
    {
        var slugPath = link.Slug.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(wikiRoot, slugPath + ".md");

        var hydrated = new HydratedRelatedLink
        {
            Slug = link.Slug,
            DisplayName = link.DisplayName,
            Title = link.FallbackDisplay,
        };

        if (!File.Exists(fullPath))
        {
            hydrated.IsMissing = true;
            return hydrated;
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            var match = FrontmatterRegex().Match(content);
            if (!match.Success) return hydrated; // file exists but no frontmatter — keep slug fallback

            var fm = YamlDeserializer.Deserialize<PageFrontmatter>(match.Groups[1].Value);
            if (fm is null) return hydrated;

            if (!string.IsNullOrWhiteSpace(fm.Title)) hydrated.Title = fm.Title!;
            if (!string.IsNullOrWhiteSpace(fm.Type)) hydrated.Type = fm.Type!;
            hydrated.Created = ParseDate(fm.Created);
        }
        catch
        {
            // Best-effort: a malformed page should render as a card with whatever we have.
        }

        return hydrated;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(value, out var fallback)) return fallback;
        return null;
    }

    private class PageFrontmatter
    {
        public string? Title { get; set; }
        public string? Type { get; set; }
        public string? Created { get; set; }
    }
}
