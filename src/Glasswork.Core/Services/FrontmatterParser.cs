using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Services;

/// <summary>
/// Parses and serializes GlassworkTask objects to/from markdown files with YAML frontmatter.
/// </summary>
public partial class FrontmatterParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    private static readonly HashSet<string> KnownFrontmatterKeys = new(StringComparer.Ordinal)
    {
        "id", "title", "status", "priority", "type", "source_kind", "size", "created", "completed_at",
        "cancelled_at", "cancellation_reason",
        "blocked_reason", "blocked_at", "blocked_from_status", "due", "start",
        "my_day", "defer_until", "ado_link", "ado_title", "parent", "blocked_by",
        "context_links", "tags", "links",
    };

    public string SerializeScalar(string key, string value)
    {
        var yaml = YamlSerializer.Serialize(new Dictionary<string, string>
        {
            [key] = value,
        }).TrimEnd();
        var prefix = $"{key}: ";
        if (!yaml.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unable to serialize YAML scalar '{key}'.");
        return yaml[prefix.Length..];
    }

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^### \[([ xX])\] (.+?)\s*$")]
    private static partial Regex SubtaskHeadingRegex();

    [GeneratedRegex(@"(?ms)^## Subtasks\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex SubtasksSectionRegex();

    [GeneratedRegex(@"(?ms)^## Related\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex RelatedSectionRegex();

    [GeneratedRegex(@"(?ms)^## Notes\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex NotesSectionRegex();

    [GeneratedRegex(Markdown.WikiLinkParser.Pattern)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"^- ([a-z_][a-z0-9_]*): (.*)$")]
    private static partial Regex MetadataLineRegex();

    /// <summary>
    /// Recognized metadata keys, in the canonical serialization order.
    /// "status" is handled as a first-class SubTask field; the rest live in Metadata.
    /// </summary>
    private static readonly string[] MetadataOrder = ["status", "size", "ado", "completed", "blocker", "due", "my_day"];

    /// <summary>
    /// Parse a markdown file's content into a GlassworkTask.
    /// </summary>
    public GlassworkTask Parse(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
            throw new FormatException("Invalid task file: missing YAML frontmatter delimiters (---).");

        var yamlContent = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();

        var frontmatter = YamlDeserializer.Deserialize<TaskFrontmatter>(yamlContent)
            ?? throw new FormatException("Failed to deserialize YAML frontmatter.");
        var rawFrontmatter = YamlDeserializer.Deserialize<Dictionary<string, object?>>(yamlContent)
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);

        var task = new GlassworkTask
        {
            Id = frontmatter.Id ?? string.Empty,
            Title = frontmatter.Title ?? string.Empty,
            Status = frontmatter.Status ?? GlassworkTask.Statuses.Todo,
            Priority = frontmatter.Priority ?? GlassworkTask.Priorities.Medium,
            Type = GlassworkTask.Types.Normalize(frontmatter.Type),
            SourceKind = frontmatter.SourceKind,
            Size = frontmatter.Size,
            Created = ParseDate(frontmatter.Created) ?? DateTime.Today,
            CompletedAt = ParseDate(frontmatter.CompletedAt),
            Due = ParseDate(frontmatter.Due),
            Start = ParseDate(frontmatter.Start),
            MyDay = ParseDate(frontmatter.MyDay),
            DeferUntil = ParseDate(frontmatter.DeferUntil),
            AdoLink = frontmatter.AdoLink,
            AdoTitle = frontmatter.AdoTitle,
            Parent = frontmatter.Parent,
            BlockedBy = CanonicalizeDependencyIds(frontmatter.BlockedBy),
            ContextLinks = frontmatter.ContextLinks ?? [],
            Tags = frontmatter.Tags ?? [],
        };
        if (task.Status == GlassworkTask.Statuses.Cancelled)
        {
            task.CancelledAt = ParseUtcTimestamp(frontmatter.CancelledAt);
            task.CancellationReason = frontmatter.CancellationReason;
        }
        task.FrontmatterExtensions = rawFrontmatter
            .Where(pair => !KnownFrontmatterKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (task.Status == GlassworkTask.Statuses.Blocked)
        {
            task.BlockedReason = frontmatter.BlockedReason;
            task.BlockedAt = ParseUtcTimestamp(frontmatter.BlockedAt);
            task.BlockedFromStatus = frontmatter.BlockedFromStatus;
            task.BlockedMetadataState =
                !string.IsNullOrWhiteSpace(task.BlockedReason)
                && task.BlockedAt.HasValue
                && task.BlockedFromStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress
                    ? BlockedMetadataState.Valid
                    : BlockedMetadataState.NeedsDetails;
        }

        // Hydrate Links from DTO
        if (frontmatter.Links is not null)
        {
            foreach (var dto in frontmatter.Links)
            {
                task.Links.Add(new TaskLink
                {
                    Type = TaskLink.Types.Normalize(dto.Type),
                    Value = dto.Value ?? string.Empty,
                    Label = dto.Label
                });
            }
        }

        // Parse subtasks from checkbox lines, separate from description prose
        var (subtasks, cleanDescription) = ParseSubtasks(body);
        task.Subtasks = subtasks;
        task.Description = cleanDescription;
        task.RelatedLinks = ParseRelatedLinks(body);
        task.Notes = ParseNotes(body);
        task.IsV1Format = MigrationService.IsV1Format(content);

        return task;
    }

    /// <summary>
    /// Serialize a GlassworkTask to markdown file content.
    /// </summary>
    public string Serialize(GlassworkTask task)
    {
        var frontmatter = new TaskFrontmatter
        {
            Id = task.Id,
            Title = task.Title,
            Status = task.Status,
            Priority = task.Priority,
            Type = GlassworkTask.Types.Normalize(task.Type) == GlassworkTask.Types.Task
                ? null
                : GlassworkTask.Types.Normalize(task.Type),
            SourceKind = task.SourceKind,
            Size = task.Size,
            Created = task.Created.ToString("yyyy-MM-dd"),
            CompletedAt = task.CompletedAt?.ToString("yyyy-MM-dd"),
            CancelledAt = task.Status == GlassworkTask.Statuses.Cancelled
                ? task.CancelledAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                : null,
            CancellationReason = task.Status == GlassworkTask.Statuses.Cancelled
                ? task.CancellationReason
                : null,
            BlockedReason = task.Status == GlassworkTask.Statuses.Blocked ? task.BlockedReason : null,
            BlockedAt = task.Status == GlassworkTask.Statuses.Blocked ? task.BlockedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : null,
            BlockedFromStatus = task.Status == GlassworkTask.Statuses.Blocked ? task.BlockedFromStatus : null,
            Due = task.Due?.ToString("yyyy-MM-dd"),
            Start = task.Start?.ToString("yyyy-MM-dd"),
            MyDay = task.MyDay?.ToString("yyyy-MM-dd"),
            DeferUntil = task.DeferUntil?.ToString("yyyy-MM-dd"),
            Parent = task.Parent,
            BlockedBy = task.BlockedBy.Count > 0 ? CanonicalizeDependencyIds(task.BlockedBy) : null,
            ContextLinks = task.ContextLinks.Count > 0 ? task.ContextLinks : null,
            Tags = task.Tags.Count > 0 ? task.Tags : null,
            Links = task.Links.Count > 0 ? task.Links.Select(l => new TaskLinkDto
            {
                Type = l.Type,
                Value = l.Value,
                Label = l.Label
            }).ToList() : null,
            // Legacy keys are omitted: AdoLink and AdoTitle are derived properties now
        };

        var yamlValues = YamlDeserializer.Deserialize<Dictionary<string, object?>>(
                YamlSerializer.Serialize(frontmatter))
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var extension in task.FrontmatterExtensions)
        {
            if (!KnownFrontmatterKeys.Contains(extension.Key))
                yamlValues[extension.Key] = extension.Value;
        }

        var yaml = YamlSerializer.Serialize(yamlValues).TrimEnd();
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(yaml);
        sb.AppendLine("---");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine(task.Description);
            sb.AppendLine();
        }

        // Always emit the canonical V2 sections (Subtasks, Notes, Related) — even when empty —
        // so newly-created tasks are V2-shaped on disk from birth and never trip the
        // "Upgrade to V2 format" affordance. Pre-existing V1 files are upgraded once at
        // app startup via VaultService.MigrateAllToV2.
        sb.AppendLine("## Subtasks");
        sb.AppendLine();
        if (task.Subtasks.Count > 0)
        {
            for (int i = 0; i < task.Subtasks.Count; i++)
            {
                var sub = task.Subtasks[i];
                var check = sub.IsCompleted ? "x" : " ";
                sb.AppendLine($"### [{check}] {sub.Text}");

                // Emit metadata in stable order: status first, then known keys, then any
                // unknown keys alphabetically (preserved for round-trip safety).
                var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(sub.Status))
                {
                    sb.AppendLine($"- status: {sub.Status}");
                    emittedKeys.Add("status");
                }
                foreach (var key in MetadataOrder)
                {
                    if (key == "status") continue;
                    if (sub.Metadata.TryGetValue(key, out var val))
                    {
                        sb.AppendLine($"- {key}: {val}");
                        emittedKeys.Add(key);
                    }
                }
                foreach (var kvp in sub.Metadata.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (emittedKeys.Contains(kvp.Key)) continue;
                    sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
                }

                // Notes (prose) block
                if (!string.IsNullOrWhiteSpace(sub.Notes))
                {
                    sb.AppendLine();
                    sb.AppendLine(sub.Notes.TrimEnd());
                }

                sb.AppendLine();
            }
        }

        // Notes section: emitted as part of V2 canonical structure. The heading is
        // always present so files are V2-shaped on disk; when Notes content is
        // non-empty (set from the UI or by an external tool), it follows the heading.
        sb.AppendLine("## Notes");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            sb.AppendLine(task.Notes.TrimEnd());
            sb.AppendLine();
        }

        sb.AppendLine("## Related");
        if (task.RelatedLinks.Count > 0)
        {
            sb.AppendLine();
            foreach (var link in task.RelatedLinks)
            {
                var inner = string.IsNullOrWhiteSpace(link.DisplayName)
                    ? link.Slug
                    : $"{link.Slug}|{link.DisplayName}";
                sb.AppendLine($"- [[{inner}]]");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static List<RelatedLink> ParseRelatedLinks(string body)
    {
        var links = new List<RelatedLink>();
        var match = RelatedSectionRegex().Match(body);
        if (!match.Success) return links;

        var section = match.Groups[1].Value;
        // Find every wiki-link occurrence; preserves order and tolerates bullets,
        // bare lines, or multiple links per line. Per D10, this section is left
        // intact in the body (Obsidian's graph view depends on it being on disk).
        foreach (Match m in WikiLinkRegex().Matches(section))
        {
            var slug = m.Groups[1].Value.Trim();
            if (slug.Length == 0) continue;
            string? display = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            if (string.IsNullOrWhiteSpace(display)) display = null;
            links.Add(new RelatedLink { Slug = slug, DisplayName = display });
        }
        return links;
    }

    /// <summary>
    /// Extracts the body of the `## Notes` section as freeform prose. Returns
    /// empty string if the section is missing or empty. Operates on the full
    /// post-frontmatter body so it sees `## Notes` even when `## Subtasks`
    /// strips it out of the cleaned Description.
    /// </summary>
    private static string ParseNotes(string body)
    {
        var match = NotesSectionRegex().Match(body);
        if (!match.Success) return string.Empty;
        return match.Groups[1].Value.Replace("\r\n", "\n").Trim();
    }

    private static List<string> CanonicalizeDependencyIds(IEnumerable<string>? dependencyIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonical = new List<string>();
        foreach (var dependencyId in dependencyIds ?? [])
        {
            var normalized = dependencyId?.Trim();
            if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
                canonical.Add(normalized);
        }

        return canonical;
    }

    private static (List<SubTask> subtasks, string cleanBody) ParseSubtasks(string body)
    {
        var subtasks = new List<SubTask>();
        var sectionMatch = SubtasksSectionRegex().Match(body);

        if (!sectionMatch.Success)
            return (subtasks, body.Trim());

        var sectionContent = sectionMatch.Groups[1].Value;
        var lines = sectionContent.Split('\n');

        SubTask? current = null;
        var notesBuffer = new StringBuilder();
        var inMetadataBlock = false;

        void FinalizeCurrent()
        {
            if (current is null) return;
            current.Notes = notesBuffer.ToString().Trim();
            subtasks.Add(current);
            notesBuffer.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var headingMatch = SubtaskHeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                FinalizeCurrent();
                current = new SubTask
                {
                    IsCompleted = headingMatch.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase),
                    Text = headingMatch.Groups[2].Value.Trim(),
                };
                inMetadataBlock = true;
                continue;
            }

            if (current is null) continue;

            if (inMetadataBlock)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inMetadataBlock = false;
                    continue;
                }

                var metaMatch = MetadataLineRegex().Match(line);
                if (metaMatch.Success)
                {
                    var key = metaMatch.Groups[1].Value;
                    var value = metaMatch.Groups[2].Value.Trim();
                    if (key == "status")
                        current.Status = value;
                    else if (key == "size")
                        current.Size = value;
                    else
                        current.Metadata[key] = value;
                    continue;
                }

                // Non-blank, non-metadata line ends the metadata block; treat as notes.
                inMetadataBlock = false;
                notesBuffer.AppendLine(line);
                continue;
            }

            notesBuffer.AppendLine(line);
        }

        FinalizeCurrent();

        // Body is everything before the ## Subtasks heading.
        var cleanBody = body[..sectionMatch.Index].Trim();
        return (subtasks, cleanBody);
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateTime.TryParse(value, out var fallback))
            return fallback;
        return null;
    }

    private static DateTimeOffset? ParseUtcTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    /// <summary>
    /// Internal DTO matching the YAML frontmatter structure.
    /// </summary>
    private class TaskFrontmatter
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Priority { get; set; }
        public string? Type { get; set; }
        [YamlMember(Alias = "source_kind")]
        public string? SourceKind { get; set; }
        public string? Size { get; set; }
        public string? Created { get; set; }
        [YamlMember(Alias = "completed_at")]
        public string? CompletedAt { get; set; }
        [YamlMember(Alias = "cancelled_at")]
        public string? CancelledAt { get; set; }
        [YamlMember(Alias = "cancellation_reason")]
        public string? CancellationReason { get; set; }
        [YamlMember(Alias = "blocked_reason")]
        public string? BlockedReason { get; set; }
        [YamlMember(Alias = "blocked_at")]
        public string? BlockedAt { get; set; }
        [YamlMember(Alias = "blocked_from_status")]
        public string? BlockedFromStatus { get; set; }
        public string? Due { get; set; }
        public string? Start { get; set; }
        [YamlMember(Alias = "my_day")]
        public string? MyDay { get; set; }
        [YamlMember(Alias = "defer_until")]
        public string? DeferUntil { get; set; }
        [YamlMember(Alias = "ado_link")]
        public int? AdoLink { get; set; }
        [YamlMember(Alias = "ado_title")]
        public string? AdoTitle { get; set; }
        public string? Parent { get; set; }
        [YamlMember(Alias = "blocked_by")]
        public List<string>? BlockedBy { get; set; }
        [YamlMember(Alias = "context_links")]
        public List<string>? ContextLinks { get; set; }
        public List<string>? Tags { get; set; }
        public List<TaskLinkDto>? Links { get; set; }
    }

    /// <summary>
    /// DTO for deserializing a single link from YAML frontmatter.
    /// </summary>
    private class TaskLinkDto
    {
        public string? Type { get; set; }
        public string? Value { get; set; }
        public string? Label { get; set; }
    }
}
