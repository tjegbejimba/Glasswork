using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glasswork.Core.Services;

/// <summary>
/// Parses WorkLog markdown files with YAML frontmatter from wiki/journal/YYYY-WNN.md.
/// </summary>
public partial class WorkLogParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n?(.*)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"(?ms)^## Summary\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex SummarySectionRegex();

    [GeneratedRegex(@"(?ms)^## Key Insights & Breakthrough Moments\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex KeyInsightsSectionRegex();

    [GeneratedRegex(@"(?ms)^## Strategic Thinking Highlights\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex StrategicThinkingSectionRegex();

    [GeneratedRegex(@"(?ms)^## Tasks Completed\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex TasksCompletedSectionRegex();

    [GeneratedRegex(@"(?ms)^## In Progress at Week End\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex InProgressSectionRegex();

    [GeneratedRegex(@"(?ms)^## Frustrations or Roadblocks\s*$(.*?)(?=^## |\z)", RegexOptions.Multiline)]
    private static partial Regex FrustrationsSectionRegex();

    /// <summary>
    /// Parse a work log markdown file's content into a WorkLog object.
    /// </summary>
    public WorkLog Parse(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
            throw new FormatException("Invalid work log: missing YAML frontmatter delimiters (---).");

        var yamlContent = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();

        var frontmatter = YamlDeserializer.Deserialize<WorkLogFrontmatter>(yamlContent)
            ?? throw new FormatException("Failed to deserialize work log YAML frontmatter.");

        // Validate required fields
        if (string.IsNullOrEmpty(frontmatter.Type))
            throw new FormatException("Work log missing required field: type");
        if (string.IsNullOrEmpty(frontmatter.Period))
            throw new FormatException("Work log missing required field: period");
        if (string.IsNullOrEmpty(frontmatter.Week))
            throw new FormatException("Work log missing required field: week");
        if (string.IsNullOrEmpty(frontmatter.DateFrom))
            throw new FormatException("Work log missing required field: date_from");
        if (string.IsNullOrEmpty(frontmatter.DateTo))
            throw new FormatException("Work log missing required field: date_to");
        if (string.IsNullOrEmpty(frontmatter.GeneratedAt))
            throw new FormatException("Work log missing required field: generated_at");
        if (string.IsNullOrEmpty(frontmatter.GeneratedBy))
            throw new FormatException("Work log missing required field: generated_by");

        var workLog = new WorkLog
        {
            Type = frontmatter.Type,
            Period = frontmatter.Period,
            Week = frontmatter.Week,
            DateFrom = ParseDate(frontmatter.DateFrom),
            DateTo = ParseDate(frontmatter.DateTo),
            GeneratedAt = ParseDateTimeUtc(frontmatter.GeneratedAt),
            GeneratedBy = frontmatter.GeneratedBy,
            TasksReferenced = frontmatter.TasksReferenced ?? [],
            Summary = ExtractSection(body, SummarySectionRegex()),
            KeyInsights = ExtractSection(body, KeyInsightsSectionRegex()),
            StrategicThinking = ExtractSection(body, StrategicThinkingSectionRegex()),
            TasksCompleted = ExtractSection(body, TasksCompletedSectionRegex()),
            InProgress = ExtractSection(body, InProgressSectionRegex()),
            Frustrations = ExtractSection(body, FrustrationsSectionRegex())
        };

        return workLog;
    }

    private static DateTime ParseDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var date))
            return date.Date;
        throw new FormatException($"Invalid date format: {dateStr}");
    }

    private static DateTime ParseDateTimeUtc(string dateTimeStr)
    {
        if (!Regex.IsMatch(dateTimeStr.Trim(), @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase))
            throw new FormatException($"Timestamp must include an explicit UTC offset: {dateTimeStr}");

        if (DateTimeOffset.TryParse(dateTimeStr, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.None,
                                     out var offset))
        {
            if (offset.Offset == TimeSpan.Zero)
                return offset.UtcDateTime;
            throw new FormatException($"Timestamp must be UTC (Z suffix or +00:00 offset): {dateTimeStr}");
        }
        throw new FormatException($"Invalid datetime format: {dateTimeStr}");
    }

    private static string ExtractSection(string body, Regex sectionRegex)
    {
        var match = sectionRegex.Match(body);
        if (!match.Success)
            return string.Empty;

        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// DTO for work log frontmatter deserialization.
    /// </summary>
    private class WorkLogFrontmatter
    {
        public string? Type { get; set; }
        public string? Period { get; set; }
        public string? Week { get; set; }
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public string? GeneratedAt { get; set; }
        public string? GeneratedBy { get; set; }
        public List<string>? TasksReferenced { get; set; }
    }
}
