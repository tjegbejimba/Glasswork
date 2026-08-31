using System;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Formats the one-liner invocations that the TaskDetailPage buttons copy to the clipboard.
/// The user pastes these into a Copilot CLI session, where the matching glasswork-* skill activates.
/// </summary>
public static class TaskInvocationFormatter
{
    public static string FormatStartWork(string taskId) =>
        FormatStartWork(taskId, GlassworkTask.Types.Task);

    public static string FormatResume(string taskId) =>
        FormatResume(taskId, GlassworkTask.Types.Task);

    public static string FormatWrapUp(string taskId) =>
        FormatWrapUp(taskId, GlassworkTask.Types.Task);

    public static string FormatStartWork(string taskId, string? taskType) =>
        FormatLifecycle("Start work on", taskId, taskType);

    public static string FormatResume(string taskId, string? taskType) =>
        FormatLifecycle("Resume", taskId, taskType);

    public static string FormatWrapUp(string taskId, string? taskType) =>
        FormatLifecycle("Wrap up", taskId, taskType);

    public static string FormatRefreshChildActivitySummary(string taskId) =>
        $"Refresh Child activity summary for Glasswork task: {Require(taskId)}";

    public static string FormatTriageReport(string description) =>
        $"Run the triage-issue skill on this report: {RequireDescription(description)}";

    private static string FormatLifecycle(string verb, string taskId, string? taskType)
    {
        var taskLabel = GlassworkTask.Types.IsParent(taskType)
            ? "Glasswork Parent Task"
            : "Glasswork task";
        return $"{verb} {taskLabel}: {Require(taskId)}";
    }

    private static string Require(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id must not be null or whitespace.", nameof(taskId));
        return taskId;
    }

    private static string RequireDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description must not be null or whitespace.", nameof(description));
        return description;
    }
}
