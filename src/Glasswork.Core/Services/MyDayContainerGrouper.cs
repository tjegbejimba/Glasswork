using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Pure, presentation-only grouping for the My Day surface (issue #337 / ADR 0017).
///
/// Given the already-promoted set of today's tasks, nests each promoted child Task
/// whose <c>parent</c> resolves by Glasswork id or ADO identity to an in-app
/// <c>type: parent</c> task under that Parent Task as a container card
/// (<see cref="GlassworkTask.TodaysChildren"/>). The Parent Task is pulled in to host its
/// children even when it would not independently promote. This never changes the promotion policy
/// (<see cref="MyDayPromotionPolicy"/> / Task Query);
/// it only reshapes the rows for display.
///
/// Layout: standalone actionable leaves retain their existing relative order, followed
/// by nearest-Parent context rows in first-promoted-child order. Higher ancestors are
/// compressed into a breadcrumb rather than materialized as nested rows.
/// </summary>
public static class MyDayContainerGrouper
{
    public static IReadOnlyList<GlassworkTask> Group(
        IReadOnlyList<GlassworkTask> promoted,
        IReadOnlyDictionary<string, GlassworkTask> allTasks,
        DateOnly today)
    {
        if (promoted is null) throw new ArgumentNullException(nameof(promoted));
        if (allTasks is null) throw new ArgumentNullException(nameof(allTasks));

        var hierarchy = new TaskHierarchyPolicy(allTasks.Values);
        var parentResolver = new TaskParentResolver(allTasks);

        // First pass: bucket promoted actionable leaves under their resolved nearest
        // Parent, preserving Task Query order within each bucket. Parent Tasks remain
        // top-level coordination/context rows; they are never presented as leaves.
        var childrenByParent = new Dictionary<string, List<GlassworkTask>>(StringComparer.Ordinal);
        foreach (var t in promoted)
        {
            if (GlassworkTask.Types.IsParent(t.Type)) continue;

            var parentId = parentResolver.ResolveTaskId(t.Parent);
            if (parentId is null || !allTasks.TryGetValue(parentId, out var parent)) continue;
            if (!GlassworkTask.Types.IsParent(parent.Type)) continue;
            if (parent.IsTerminal) continue;

            if (!childrenByParent.TryGetValue(parentId, out var bucket))
            {
                bucket = [];
                childrenByParent[parentId] = bucket;
            }
            bucket.Add(t);
        }

        var nestedChildIds = new HashSet<string>(
            childrenByParent.Values.SelectMany(b => b.Select(c => c.Id)), StringComparer.Ordinal);
        var hostParentIds = new HashSet<string>(childrenByParent.Keys, StringComparer.Ordinal);

        // Parent rows: reuse the promoted instance when it independently promoted,
        // otherwise pull in a host clone (and compute its own in-file today's subtasks).
        var containers = new Dictionary<string, GlassworkTask>(StringComparer.Ordinal);
        foreach (var (parentId, children) in childrenByParent)
        {
            var container = promoted.FirstOrDefault(t => string.Equals(t.Id, parentId, StringComparison.Ordinal))
                ?? PullInHost(allTasks[parentId], today);

            container.TodaysChildren = children;
            AnnotateParentContext(container, hierarchy);
            containers[parentId] = container;
        }

        // Preserve the established My Day shape: standalone leaves first in their
        // existing relative order, followed by Parent groups. Group and child order
        // both follow the promoted leaf order; Parent target/priority never participate.
        var standalone = promoted
            .Where(task => !nestedChildIds.Contains(task.Id) && !hostParentIds.Contains(task.Id))
            .Where(task => !GlassworkTask.Types.IsParent(task.Type)
                           || IsPinnedFor(task, today)
                           || task.TodaysSubtasks?.Count is > 0)
            .ToList();
        foreach (var parent in standalone.Where(
                     task => GlassworkTask.Types.IsParent(task.Type)))
        {
            AnnotateParentContext(parent, hierarchy);
        }

        var promotedPositions = promoted
            .Select((task, index) => (task.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var orderedContainers = containers.Values
            .OrderBy(container => container.TodaysChildren!
                .Min(child => promotedPositions[child.Id]))
            .ToList();

        var rows = new List<GlassworkTask>(standalone.Count + orderedContainers.Count);
        rows.AddRange(standalone);
        rows.AddRange(orderedContainers);
        return rows;
    }

    private static bool IsPinnedFor(GlassworkTask task, DateOnly today) =>
        task.MyDay.HasValue
        && DateOnly.FromDateTime(task.MyDay.Value.Date) == today;

    private static void AnnotateParentContext(
        GlassworkTask parent,
        TaskHierarchyPolicy hierarchy)
    {
        parent.MyDaySourceKindBadge = TaskPresentationLabels.SourceKindBadge(parent);
        var higherAncestors = hierarchy.GetAncestors(parent.Id);
        parent.MyDayAncestorBreadcrumb = higherAncestors.Count == 0
            ? null
            : string.Join(
                " › ",
                higherAncestors
                    .Reverse()
                    .Select(TaskPresentationLabels.DisplayTitle));
    }

    private static GlassworkTask PullInHost(GlassworkTask parent, DateOnly today)
    {
        var host = parent.Clone();
        host.TodaysSubtasks = MyDayPromotionPolicy.TodaysSubtasks(parent, today);
        return host;
    }

}
