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

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^### \[([ xX])\] (.+?)\s*$")]
    private static partial Regex SubtaskHeadingRegex();

    [GeneratedRegex(@"(?ms)^## Subtasks\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex SubtasksSectionRegex();

    [GeneratedRegex(@"^- ([a-z_][a-z0-9_]*): (.*)$")]
    private static partial Regex MetadataLineRegex();

    /// <summary>
    /// Recognized metadata keys, in the canonical serialization order.
    /// "status" is handled as a first-class SubTask field; the rest live in Metadata.
    /// </summary>
    private static readonly string[] MetadataOrder = ["status", "ado", "completed", "blocker", "my_day"];

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

        var task = new GlassworkTask
        {
            Id = frontmatter.Id ?? string.Empty,
            Title = frontmatter.Title ?? string.Empty,
            Status = frontmatter.Status ?? GlassworkTask.Statuses.Todo,
            Priority = frontmatter.Priority ?? GlassworkTask.Priorities.Medium,
            Created = ParseDate(frontmatter.Created) ?? DateTime.Today,
            CompletedAt = ParseDate(frontmatter.CompletedAt),
            Due = ParseDate(frontmatter.Due),
            MyDay = ParseDate(frontmatter.MyDay),
            AdoLink = frontmatter.AdoLink,
            AdoTitle = frontmatter.AdoTitle,
            Parent = frontmatter.Parent,
            ContextLinks = frontmatter.ContextLinks ?? [],
            Tags = frontmatter.Tags ?? [],
        };

        // Parse subtasks from checkbox lines, separate from body prose
        var (subtasks, cleanBody) = ParseSubtasks(body);
        task.Subtasks = subtasks;
        task.Body = cleanBody;
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
            Created = task.Created.ToString("yyyy-MM-dd"),
            CompletedAt = task.CompletedAt?.ToString("yyyy-MM-dd"),
            Due = task.Due?.ToString("yyyy-MM-dd"),
            MyDay = task.MyDay?.ToString("yyyy-MM-dd"),
            AdoLink = task.AdoLink,
            AdoTitle = task.AdoTitle,
            Parent = task.Parent,
            ContextLinks = task.ContextLinks.Count > 0 ? task.ContextLinks : null,
            Tags = task.Tags.Count > 0 ? task.Tags : null,
        };

        var yaml = YamlSerializer.Serialize(frontmatter).TrimEnd();
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(yaml);
        sb.AppendLine("---");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(task.Body))
        {
            sb.AppendLine(task.Body);
            sb.AppendLine();
        }

        if (task.Subtasks.Count > 0)
        {
            sb.AppendLine("## Subtasks");
            sb.AppendLine();
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

        return sb.ToString().TrimEnd() + "\n";
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

    /// <summary>
    /// Internal DTO matching the YAML frontmatter structure.
    /// </summary>
    private class TaskFrontmatter
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Status { get; set; }
        public string? Priority { get; set; }
        public string? Created { get; set; }
        [YamlMember(Alias = "completed_at")]
        public string? CompletedAt { get; set; }
        public string? Due { get; set; }
        [YamlMember(Alias = "my_day")]
        public string? MyDay { get; set; }
        [YamlMember(Alias = "ado_link")]
        public int? AdoLink { get; set; }
        [YamlMember(Alias = "ado_title")]
        public string? AdoTitle { get; set; }
        public string? Parent { get; set; }
        [YamlMember(Alias = "context_links")]
        public List<string>? ContextLinks { get; set; }
        public List<string>? Tags { get; set; }
    }
}
