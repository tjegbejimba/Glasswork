using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Services;

/// <summary>
/// Resolves the display title of a wiki/markdown page using the canonical
/// Glasswork rule: frontmatter <c>title:</c> wins over the first H1 wins
/// over the filename stem. Pure helper — no I/O.
///
/// Shared by <see cref="FileSystemArtifactStore"/> and the backlink index
/// so artifact titles, backlink titles, and any future readers stay aligned.
/// </summary>
public static partial class WikiPageTitleResolver
{
    public const int DefaultMaxLength = 80;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"(?m)^#\s+(.+?)\s*$")]
    private static partial Regex FirstH1Regex();

    /// <summary>
    /// Resolves the display title from the markdown <paramref name="content"/>,
    /// falling back to the filename stem of <paramref name="filePath"/>.
    /// Result is truncated (with an ellipsis) at <paramref name="maxLength"/>.
    /// </summary>
    public static string Resolve(string content, string filePath, int maxLength = DefaultMaxLength)
    {
        var (frontmatterTitle, bodyAfterFrontmatter) = ExtractFrontmatterTitle(content ?? string.Empty);
        var title = frontmatterTitle
            ?? ExtractFirstH1(bodyAfterFrontmatter)
            ?? Path.GetFileNameWithoutExtension(filePath ?? string.Empty);
        return Truncate(title, maxLength);
    }

    private static (string? title, string body) ExtractFrontmatterTitle(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success) return (null, content);

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value;
        try
        {
            var fm = YamlDeserializer.Deserialize<TitleFrontmatter>(yaml);
            var t = fm?.Title?.Trim();
            return (string.IsNullOrEmpty(t) ? null : t, body);
        }
        catch
        {
            // Malformed frontmatter — fall back to body-based heuristics.
            return (null, body);
        }
    }

    private static string? ExtractFirstH1(string body)
    {
        var match = FirstH1Regex().Match(body);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        return value[..(max - 1)].TrimEnd() + "…";
    }

    private sealed class TitleFrontmatter
    {
        public string? Title { get; set; }
    }
}
