using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

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
    public List<VisualVerificationTask> Tasks { get; init; } = [];
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
        }

        foreach (var action in Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Type))
                throw new FormatException("Every scenario action requires a non-empty type.");
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

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeTaskIdRegex();
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
    public string? Parent { get; init; }
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
            Parent = Parent,
            Description = Description ?? string.Empty,
            Notes = Notes ?? string.Empty,
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

public sealed class VisualVerificationAction
{
    public string Type { get; init; } = string.Empty;
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
    public int TimeoutMilliseconds { get; init; } = 5000;
}

public sealed class VisualVerificationCapture
{
    public string Name { get; init; } = string.Empty;
    public int WaitMilliseconds { get; init; }
}
