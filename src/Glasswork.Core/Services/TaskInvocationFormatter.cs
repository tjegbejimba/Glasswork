using System;

namespace Glasswork.Core.Services;

/// <summary>
/// Formats the one-liner invocations that the TaskDetailPage buttons copy to the clipboard.
/// The user pastes these into a Copilot CLI session, where the matching glasswork-* skill activates.
/// </summary>
public static class TaskInvocationFormatter
{
    public static string FormatStartWork(string taskId) =>
        $"Start work on Glasswork task: {Require(taskId)}";

    public static string FormatResume(string taskId) =>
        $"Resume Glasswork task: {Require(taskId)}";

    public static string FormatWrapUp(string taskId) =>
        $"Wrap up Glasswork task: {Require(taskId)}";

    private static string Require(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id must not be null or whitespace.", nameof(taskId));
        return taskId;
    }
}
