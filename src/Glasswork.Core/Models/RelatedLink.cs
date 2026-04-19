using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// A wiki-link reference parsed from a task's <c>## Related</c> section.
/// Format: <c>[[slug]]</c> or <c>[[slug|display name]]</c>.
/// The <see cref="Slug"/> is the path relative to the Obsidian vault root, without the
/// <c>.md</c> extension (e.g. <c>decisions/glasswork-v2-prd</c>).
/// </summary>
public partial class RelatedLink : ObservableObject
{
    [ObservableProperty] public partial string Slug { get; set; } = string.Empty;
    [ObservableProperty] public partial string? DisplayName { get; set; }

    /// <summary>
    /// The text to show when no hydrated title is available.
    /// Falls back to the last segment of the slug (e.g. <c>glasswork-v2-prd</c>).
    /// </summary>
    public string FallbackDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName!;
            var idx = Slug.LastIndexOf('/');
            return idx >= 0 ? Slug[(idx + 1)..] : Slug;
        }
    }
}
