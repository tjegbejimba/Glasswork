using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Research;

namespace Glasswork.Core.VisualVerification;

public sealed partial class VisualVerificationScenario
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Name { get; init; } = string.Empty;
    public string? StartUri { get; init; }
    public int LaunchTimeoutSeconds { get; init; } = 20;
    public int InitialWaitMilliseconds { get; init; } = 800;
    public string Theme { get; init; } = "system";
    public List<VisualVerificationTask> Tasks { get; init; } = [];
    public List<VisualVerificationWikiPage> WikiPages { get; init; } = [];
    public List<VisualVerificationWayfinderIssue> WayfinderIssues { get; init; } = [];
    public List<VisualVerificationResearchChangeLog> ResearchChangeLogs { get; init; } = [];
    public List<VisualVerificationAction> Actions { get; init; } = [];
    public List<VisualVerificationCapture> Captures { get; init; } = [];

    public static VisualVerificationScenario FromFile(string path) =>
        FromJson(File.ReadAllText(path));

    public static VisualVerificationScenario FromJson(string json)
    {
        var scenario = JsonSerializer.Deserialize<VisualVerificationScenario>(json, JsonOptions)
            ?? throw new FormatException("Scenario JSON did not deserialize to an object.");

        scenario.Validate();
        return scenario;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new FormatException("Scenario requires a non-empty name.");
        if (Captures.Count == 0)
            throw new FormatException("Scenario requires at least one capture.");
        if (LaunchTimeoutSeconds <= 0)
            throw new FormatException("launchTimeoutSeconds must be greater than zero.");
        if (InitialWaitMilliseconds < 0)
            throw new FormatException("initialWaitMilliseconds must not be negative.");
        if (Theme is not ("system" or "light" or "dark"))
            throw new FormatException("theme must be system, light, or dark.");

        foreach (var task in Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new FormatException("Every scenario task requires a non-empty id.");
            if (!IsSafeTaskId(task.Id))
                throw new FormatException($"Scenario task id '{task.Id}' must be a safe filename slug.");
            if (string.IsNullOrWhiteSpace(task.Title))
                throw new FormatException($"Scenario task '{task.Id}' requires a non-empty title.");
            foreach (var subtask in task.Subtasks)
            {
                if (string.IsNullOrWhiteSpace(subtask.Text))
                    throw new FormatException($"Scenario task '{task.Id}' contains a subtask without text.");
            }
            foreach (var related in task.Related)
            {
                if (!IsSafeWikiPagePath(related + ".md"))
                    throw new FormatException(
                        $"Scenario task '{task.Id}' has unsafe Related Wiki path '{related}'.");
            }
        }

        foreach (var page in WikiPages)
        {
            if (string.IsNullOrWhiteSpace(page.Id) || !IsSafeTaskId(page.Id))
                throw new FormatException("Every scenario Wiki Page requires a safe id.");
            if (string.IsNullOrWhiteSpace(page.Title))
                throw new FormatException($"Scenario Wiki Page '{page.Id}' requires a non-empty title.");
            if (string.IsNullOrWhiteSpace(page.Type))
                throw new FormatException($"Scenario Wiki Page '{page.Id}' requires a non-empty type.");
            if (!IsSafeWikiPagePath(page.RelativePath))
                throw new FormatException(
                    $"Scenario Wiki Page '{page.Id}' requires a safe .md path relative to wiki/.");
        }

        foreach (var issue in WayfinderIssues)
        {
            if (!WayfinderIssueIdentity.TryParse(issue.Reference, out _))
            {
                throw new FormatException(
                    $"Scenario Wayfinder issue '{issue.Reference}' requires a canonical owner/repository#number identity.");
            }
            if (issue.State is not (
                "open" or "closed" or "unknown" or "inaccessible" or "not-found"))
            {
                throw new FormatException(
                    $"Scenario Wayfinder issue '{issue.Reference}' has unsupported state '{issue.State}'.");
            }
        }

        foreach (var log in ResearchChangeLogs)
        {
            if (string.IsNullOrWhiteSpace(log.TopicId) || !IsSafeTaskId(log.TopicId))
            {
                throw new FormatException(
                    "Every scenario Research Change Log requires a safe Topic ID.");
            }
        }

        foreach (var action in Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Type))
                throw new FormatException("Every scenario action requires a non-empty type.");
            if (action.Type.Equals("replace-task-text", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(action.TaskId) || !IsSafeTaskId(action.TaskId))
                    throw new FormatException("replace-task-text requires a safe taskId.");
                if (string.IsNullOrEmpty(action.OldValue))
                    throw new FormatException("replace-task-text requires a non-empty oldValue.");
                if (action.Value is null)
                    throw new FormatException("replace-task-text requires value.");
            }
            if (action.Type.Equals("replace-wiki-page-text", StringComparison.OrdinalIgnoreCase))
            {
                if (action.WikiPagePath is null
                    || !IsSafeWikiPagePath(action.WikiPagePath))
                {
                    throw new FormatException(
                        "replace-wiki-page-text requires a safe wikiPagePath relative to wiki/.");
                }
                if (string.IsNullOrEmpty(action.OldValue))
                    throw new FormatException("replace-wiki-page-text requires a non-empty oldValue.");
                if (action.Value is null)
                    throw new FormatException("replace-wiki-page-text requires value.");
            }
            if (action.Type.Equals("delete-wiki-page", StringComparison.OrdinalIgnoreCase)
                && (action.WikiPagePath is null
                    || !IsSafeWikiPagePath(action.WikiPagePath)))
            {
                throw new FormatException(
                    "delete-wiki-page requires a safe wikiPagePath relative to wiki/.");
            }
            if (action.Type.Equals("scroll-percent", StringComparison.OrdinalIgnoreCase)
                || action.Type.Equals(
                    "assert-vertical-scroll-at-least",
                    StringComparison.OrdinalIgnoreCase)
                || action.Type.Equals(
                    "assert-vertical-scroll-at-most",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(
                        action.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var percent)
                    || percent is < 0 or > 100)
                {
                    throw new FormatException(
                        $"{action.Type} requires value between 0 and 100.");
                }
            }
            if (action.Type.Equals("assert-selected", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(action.Name))
            {
                throw new FormatException("assert-selected requires name.");
            }
            if (action.Type.Equals("assert-name", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(action.Value))
            {
                throw new FormatException("assert-name requires value.");
            }
            if (action.Type.Equals(
                    "assert-clipboard-text",
                    StringComparison.OrdinalIgnoreCase)
                && action.Value is null)
            {
                throw new FormatException("assert-clipboard-text requires value.");
            }
            if (action.Type.Equals("press-key", StringComparison.OrdinalIgnoreCase)
                && action.Value is not ("Escape" or "Tab" or "Space"))
            {
                throw new FormatException("press-key requires value Escape, Tab, or Space.");
            }
        }

        foreach (var capture in Captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Name))
                throw new FormatException("Every scenario capture requires a non-empty name.");
        }
    }

    private static bool IsSafeTaskId(string taskId)
    {
        var trimmed = taskId.Trim();
        return trimmed == taskId
            && trimmed is not "." and not ".."
            && SafeTaskIdRegex().IsMatch(trimmed);
    }

    private static bool IsSafeWikiPagePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || !relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..")
            && SafeWikiPagePathRegex().IsMatch(normalized);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeTaskIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*\.md$")]
    private static partial Regex SafeWikiPagePathRegex();
}

public sealed class VisualVerificationTask
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = GlassworkTask.Statuses.Todo;
    public string Priority { get; init; } = GlassworkTask.Priorities.Medium;
    public string? Type { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public string? Due { get; init; }
    public string? MyDay { get; init; }
    public string? CompletedAt { get; init; }
    public string? CancelledAt { get; init; }
    public string? CancellationReason { get; init; }
    public string? Parent { get; init; }
    public int? AdoLink { get; init; }
    public List<string> Related { get; init; } = [];
    public List<VisualVerificationSubtask> Subtasks { get; init; } = [];
    public List<VisualVerificationArtifact> Artifacts { get; init; } = [];

    public GlassworkTask ToGlassworkTask(DateTime today)
    {
        var task = new GlassworkTask
        {
            Id = Id,
            Title = Title,
            Status = Status,
            Priority = Priority,
            Type = GlassworkTask.Types.Normalize(Type),
            Created = today.Date,
            Due = ParseScenarioDate(Due, today),
            MyDay = ParseScenarioDate(MyDay, today),
            CompletedAt = ParseScenarioDate(CompletedAt, today),
            CancelledAt = ParseScenarioDateTimeOffset(CancelledAt),
            CancellationReason = CancellationReason,
            Parent = Parent,
            AdoLink = AdoLink,
            Description = Description ?? string.Empty,
            Notes = Notes ?? string.Empty,
            RelatedLinks = Related.Select(slug => new RelatedLink
            {
                Slug = slug,
            }).ToList(),
        };

        foreach (var subtask in Subtasks)
        {
            task.Subtasks.Add(subtask.ToSubTask(today));
        }

        return task;
    }

    internal static DateTime? ParseScenarioDate(string? value, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "today" => today.Date,
            "yesterday" => today.Date.AddDays(-1),
            "tomorrow" => today.Date.AddDays(1),
            _ => DateTime.TryParse(value, out var parsed)
                ? parsed.Date
                : throw new FormatException($"Invalid scenario date '{value}'. Use yyyy-MM-dd, today, yesterday, or tomorrow.")
        };
    }

    private static DateTimeOffset? ParseScenarioDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
                ? parsed.ToUniversalTime()
                : throw new FormatException(
                    $"Invalid scenario timestamp '{value}'. Use an RFC 3339 timestamp.");
    }
}

public sealed class VisualVerificationSubtask
{
    public string Text { get; init; } = string.Empty;
    public bool IsCompleted { get; init; }
    public string? Status { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public string? Notes { get; init; }
    public string? Due { get; init; }
    public string? MyDay { get; init; }

    public SubTask ToSubTask(DateTime today)
    {
        var metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal);
        if (Due is not null)
            metadata["due"] = VisualVerificationTask.ParseScenarioDate(Due, today)!.Value.ToString("yyyy-MM-dd");
        if (MyDay is not null)
            metadata["my_day"] = VisualVerificationTask.ParseScenarioDate(MyDay, today)!.Value.ToString("yyyy-MM-dd");

        return new SubTask
        {
            Text = Text,
            IsCompleted = IsCompleted,
            Status = Status,
            Metadata = metadata,
            Notes = Notes ?? string.Empty,
        };
    }
}

public sealed class VisualVerificationArtifact
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Raw text content for text-like artifacts (md/html/txt/svg). Takes
    /// precedence over <see cref="Markdown"/> when set. Ignored when
    /// <see cref="Base64"/> is provided.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Back-compat alias for <see cref="Content"/>; the original scenarios only
    /// seeded markdown bodies.
    /// </summary>
    public string Markdown { get; init; } = string.Empty;

    /// <summary>
    /// Base64-encoded bytes for binary artifacts (e.g. a PNG). When set, the
    /// file is written verbatim from the decoded bytes and text fields are
    /// ignored.
    /// </summary>
    public string? Base64 { get; init; }
}

public sealed class VisualVerificationWikiPage
{
    public string RelativePath { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Confidence { get; init; }
    public string? Updated { get; init; }
    public string? Expires { get; init; }
    public List<string> Sources { get; init; } = [];
    public bool OptedIn { get; init; } = true;
    public List<string> ResearchInclude { get; init; } = [];
    public List<string> ResearchExclude { get; init; } = [];
    public List<string> ResearchRelatedWork { get; init; } = [];
    public List<string> ResearchRelatedWayfinder { get; init; } = [];
    public string Markdown { get; init; } = string.Empty;
}

public sealed class VisualVerificationWayfinderIssue
{
    public string Reference { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = "unknown";
    public bool HasReciprocalReference { get; init; }
}

public sealed class VisualVerificationResearchChangeLog
{
    public string TopicId { get; init; } = string.Empty;
    public string Markdown { get; init; } = string.Empty;
}

public sealed class VisualVerificationAction
{
    public string Type { get; init; } = string.Empty;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? TaskId { get; init; }
    public string? WikiPagePath { get; init; }
    public string? OldValue { get; init; }
    public string? Value { get; init; }
    public int TimeoutMilliseconds { get; init; } = 5000;
}

public sealed class VisualVerificationCapture
{
    public string Name { get; init; } = string.Empty;
    public int WaitMilliseconds { get; init; }
}
