using System.Globalization;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class TaskPickerRow
{
    public TaskPickerRow(
        string taskId,
        string title,
        string statusLabel,
        string sourceKindBadge,
        string? nearestParentTitle,
        string? fullAncestry,
        string accessibleName)
    {
        TaskId = taskId;
        Title = title;
        StatusLabel = statusLabel;
        SourceKindBadge = sourceKindBadge;
        NearestParentTitle = nearestParentTitle;
        FullAncestry = fullAncestry;
        AccessibleName = accessibleName;
    }

    public string TaskId { get; set; }
    public string Title { get; set; }
    public string StatusLabel { get; set; }
    public string SourceKindBadge { get; set; }
    public string? NearestParentTitle { get; set; }
    public string? FullAncestry { get; set; }
    public string AccessibleName { get; set; }
    public string? NearestParentLabel =>
        NearestParentTitle is null ? null : $"Parent · {NearestParentTitle}";
}

public static class TaskPickerPresentationPolicy
{
    public static IReadOnlyList<TaskPickerRow> Project(
        IEnumerable<GlassworkTask> allTasks,
        IEnumerable<GlassworkTask> candidates)
    {
        ArgumentNullException.ThrowIfNull(allTasks);
        ArgumentNullException.ThrowIfNull(candidates);

        var hierarchyTasks = allTasks.ToArray();
        var hierarchy = new TaskHierarchyPolicy(hierarchyTasks);
        return candidates
            .Select(task => Project(task, hierarchy))
            .ToArray();
    }

    public static IReadOnlyList<TaskPickerRow> Filter(
        IEnumerable<TaskPickerRow> rows,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var normalizedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return rows.ToArray();

        return rows
            .Where(row =>
                row.TaskId.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || row.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static TaskPickerRow Project(
        GlassworkTask task,
        TaskHierarchyPolicy hierarchy)
    {
        var parent = hierarchy.ResolveParent(task);
        var nearestParentTitle = ParentTitle(parent);
        var localAncestors = hierarchy.GetAncestors(task.Id);
        var ancestry = localAncestors
            .Reverse()
            .Select(TaskPresentationLabels.DisplayTitle)
            .ToList();

        if (localAncestors.Count > 0)
        {
            var outermostParent = hierarchy.ResolveParent(localAncestors[^1]);
            var externalAncestor = ParentTitle(outermostParent);
            if (outermostParent.Kind is TaskParentResolutionKind.UnresolvedExternal
                or TaskParentResolutionKind.AmbiguousExternal
                or TaskParentResolutionKind.Invalid)
            {
                ancestry.Insert(0, externalAncestor!);
            }
        }
        else if (nearestParentTitle is not null)
        {
            ancestry.Add(nearestParentTitle);
        }

        var fullAncestry = ancestry.Count == 0
            ? null
            : string.Join(" > ", ancestry);
        var sourceKindBadge = TaskPresentationLabels.SourceKindBadge(task);
        var statusLabel = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            task.Status.Replace('-', ' '));
        var parentSegment = nearestParentTitle is null
            ? string.Empty
            : $", Parent {nearestParentTitle}";
        var ancestrySegment = fullAncestry is null
            ? string.Empty
            : $", Full ancestry {fullAncestry}";
        var accessibleName =
            $"{task.Title}, {sourceKindBadge}, {statusLabel}{parentSegment}{ancestrySegment}, Task {task.Id}";

        return new(
            task.Id,
            task.Title,
            statusLabel,
            sourceKindBadge,
            nearestParentTitle,
            fullAncestry,
            accessibleName);
    }

    private static string? ParentTitle(TaskParentResolution parent) =>
        parent.Kind switch
        {
            TaskParentResolutionKind.None => null,
            TaskParentResolutionKind.Invalid =>
                $"Invalid parent · {parent.RawReference}",
            _ => parent.DisplayTitle ?? "Unresolved parent",
        };
}

internal static class TaskPresentationLabels
{
    public static string SourceKindBadge(GlassworkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceKind))
            return task.SourceKind.Trim();
        if (GlassworkTask.Types.IsParent(task.Type))
            return "Parent Task";
        return string.Equals(
            GlassworkTask.Types.Normalize(task.Type),
            GlassworkTask.Types.Bug,
            StringComparison.Ordinal)
            ? "Bug"
            : "Task";
    }

    public static string DisplayTitle(GlassworkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Title))
            return task.Title.Trim();
        if (!string.IsNullOrWhiteSpace(task.AdoTitle))
            return task.AdoTitle.Trim();
        return task.AdoLink is int adoId
            ? $"Unresolved parent · ADO #{adoId}"
            : "Unresolved parent";
    }
}
