using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Glasswork.Core.Services;

/// <summary>
/// Lazy migration of V1 task files to V2 schema.
/// V1 = flat markdown body with no `## Subtasks` header.
/// V2 = canonical sections in order: <c>## Subtasks</c>, <c>## Notes</c>, <c>## Related</c>.
///
/// Migration is idempotent (running twice yields the same content) and lossless
/// (frontmatter and existing body content are preserved verbatim; only missing
/// canonical sections are appended).
/// </summary>
public partial class MigrationService
{
    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"(?m)^##\s+Subtasks\s*$")]
    private static partial Regex SubtasksHeaderRegex();

    [GeneratedRegex(@"(?m)^##\s+Notes\s*$")]
    private static partial Regex NotesHeaderRegex();

    [GeneratedRegex(@"(?m)^##\s+Related\s*$")]
    private static partial Regex RelatedHeaderRegex();

    /// <summary>
    /// Returns true when the file content is in V1 format (no `## Subtasks` header in body).
    /// </summary>
    public static bool IsV1Format(string content)
    {
        var match = FrontmatterRegex().Match(content);
        var body = match.Success ? match.Groups[2].Value : content;
        return !SubtasksHeaderRegex().IsMatch(body);
    }

    /// <summary>
    /// Migrate a V1 file's content to V2 by appending any missing canonical sections
    /// (Subtasks, Notes, Related) in order. Idempotent and lossless.
    /// </summary>
    public string MigrateToV2(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
            throw new FormatException("Cannot migrate: missing YAML frontmatter delimiters (---).");

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value;

        var hasSubtasks = SubtasksHeaderRegex().IsMatch(body);
        var hasNotes = NotesHeaderRegex().IsMatch(body);
        var hasRelated = RelatedHeaderRegex().IsMatch(body);

        if (hasSubtasks && hasNotes && hasRelated)
            return content;

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(frontmatter);
        sb.Append("\n---\n");

        var trimmedBody = body.TrimEnd();
        if (trimmedBody.Length > 0)
        {
            sb.Append('\n');
            sb.Append(trimmedBody);
            sb.Append('\n');
        }

        if (!hasSubtasks)
        {
            sb.Append("\n## Subtasks\n");
        }
        if (!hasNotes)
        {
            sb.Append("\n## Notes\n");
        }
        if (!hasRelated)
        {
            sb.Append("\n## Related\n");
        }

        return sb.ToString();
    }
}
