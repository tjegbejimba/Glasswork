using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Services;

/// <summary>
/// Filesystem-backed <see cref="IArtifactStore"/> reading
/// &lt;vault&gt;/wiki/todo/&lt;taskId&gt;.artifacts/*.md.
/// </summary>
public sealed partial class FileSystemArtifactStore : IArtifactStore
{
    private const int MaxTitleLength = 80;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"(?m)^#\s+(.+?)\s*$")]
    private static partial Regex FirstH1Regex();

    private readonly string _vaultRoot;

    public FileSystemArtifactStore(string vaultRoot)
    {
        _vaultRoot = vaultRoot ?? throw new ArgumentNullException(nameof(vaultRoot));
    }

    public IReadOnlyList<Artifact> Load(string taskId)
    {
        var folder = Path.Combine(_vaultRoot, "wiki", "todo", taskId + ".artifacts");
        if (!Directory.Exists(folder))
        {
            return Array.Empty<Artifact>();
        }

        var files = Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly);
        var artifacts = new List<Artifact>();
        foreach (var file in files)
        {
            var raw = File.ReadAllText(file);
            var (frontmatterTitle, bodyAfterFrontmatter) = ExtractFrontmatterTitle(raw);
            var title = frontmatterTitle
                ?? ExtractFirstH1(bodyAfterFrontmatter)
                ?? Path.GetFileNameWithoutExtension(file);
            title = Truncate(title, MaxTitleLength);
            var modified = File.GetLastWriteTimeUtc(file);
            artifacts.Add(new Artifact(file, title, modified, raw));
        }

        return artifacts
            .OrderByDescending(a => a.ModifiedUtc)
            .ToList();
    }

    private static (string? title, string body) ExtractFrontmatterTitle(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success) return (null, content);

        var yaml = match.Groups[1].Value;
        var body = match.Groups[2].Value;
        try
        {
            var fm = YamlDeserializer.Deserialize<ArtifactFrontmatter>(yaml);
            var t = fm?.Title?.Trim();
            return (string.IsNullOrEmpty(t) ? null : t, body);
        }
        catch
        {
            // Malformed frontmatter — fall back to body-based heuristics. The malformed
            // YAML block stays in `content`, so an H1 search would also scan it; advance
            // past the closing --- so the H1 search only sees prose.
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

    private sealed class ArtifactFrontmatter
    {
        public string? Title { get; set; }
        public string? Kind { get; set; }
        public string? Producer { get; set; }
        [YamlMember(Alias = "created_at")]
        public string? CreatedAt { get; set; }
    }
}
