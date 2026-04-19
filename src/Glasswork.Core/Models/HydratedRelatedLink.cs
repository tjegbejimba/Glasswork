using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// A <see cref="RelatedLink"/> resolved against the wiki on disk: title, type, and created
/// date pulled from the target page's YAML frontmatter. When the target file is missing,
/// <see cref="IsMissing"/> is true and the rendered fields fall back to the slug.
/// </summary>
public partial class HydratedRelatedLink : ObservableObject
{
    [ObservableProperty] public partial string Slug { get; set; } = string.Empty;
    [ObservableProperty] public partial string? DisplayName { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Type { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime? Created { get; set; }
    [ObservableProperty] public partial bool IsMissing { get; set; }

    /// <summary>Single-character glyph for the page type (decision/note/contact/...).</summary>
    public string TypeGlyph => Type?.ToLowerInvariant() switch
    {
        "decision" => "📋",
        "contact" => "👤",
        "meeting" => "📅",
        "project" => "📁",
        "reference" or "ref" => "📚",
        "note" or "" or null => "📄",
        _ => "📄",
    };

    /// <summary>Capitalized type label, or "Page" when no type is known.</summary>
    public string TypeLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Type)) return "Page";
            return char.ToUpperInvariant(Type[0]) + Type[1..];
        }
    }

    /// <summary>"yyyy-MM-dd" string of <see cref="Created"/>, or empty.</summary>
    public string CreatedDisplay => Created.HasValue ? Created.Value.ToString("yyyy-MM-dd") : string.Empty;

    /// <summary>One-line summary used as the card subtitle: "Decision • 2026-04-17" or "Missing".</summary>
    public string Subtitle
    {
        get
        {
            if (IsMissing) return "Missing";
            var parts = new System.Collections.Generic.List<string> { TypeLabel };
            if (!string.IsNullOrEmpty(CreatedDisplay)) parts.Add(CreatedDisplay);
            return string.Join(" • ", parts);
        }
    }
}
