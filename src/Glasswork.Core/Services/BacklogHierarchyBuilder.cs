using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public static class BacklogHierarchyBuilder
{
    public static IReadOnlyList<BacklogHierarchyRow> Build(
        IEnumerable<GlassworkTask> sourceTasks,
        IEnumerable<GlassworkTask> visibleTasks,
        IEnumerable<string> collapsedParentKeys)
    {
        ArgumentNullException.ThrowIfNull(sourceTasks);
        ArgumentNullException.ThrowIfNull(visibleTasks);
        ArgumentNullException.ThrowIfNull(collapsedParentKeys);
        var collapsedKeys = collapsedParentKeys.ToHashSet(StringComparer.Ordinal);

        var all = sourceTasks
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var hierarchy = new TaskHierarchyPolicy(all.Values);
        var required = visibleTasks
            .Where(task => all.ContainsKey(task.Id))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var taskId in required.ToArray())
        {
            foreach (var ancestor in hierarchy.GetAncestors(taskId))
            {
                if (!GlassworkTask.Types.IsParent(ancestor.Type))
                    break;
                required.Add(ancestor.Id);
            }
        }

        var children = required.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var roots = new HashSet<string>(required, StringComparer.Ordinal);
        var degradedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var syntheticChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var syntheticTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var diagnostic in hierarchy.Validate(required)
                     .Where(diagnostic => diagnostic.Code == TaskHierarchyDiagnosticCodes.ParentCycle))
        {
            foreach (var taskId in diagnostic.TaskIds.Where(required.Contains))
                degradedReasons[taskId] = "Parent relationship contains a cycle.";
        }

        foreach (var taskId in required)
        {
            var task = all[taskId];
            var parent = hierarchy.ResolveParent(task);
            if (parent.Kind == TaskParentResolutionKind.Local
                && parent.CanonicalTaskId is { } parentId
                && all.TryGetValue(parentId, out var parentTask))
            {
                if (!GlassworkTask.Types.IsParent(parentTask.Type))
                {
                    degradedReasons[taskId] =
                        $"Parent '{parentTask.Title}' is not a Parent Task.";
                }
                else if (required.Contains(parentId))
                {
                    children[parentId].Add(taskId);
                    roots.Remove(taskId);
                }
                continue;
            }

            if (parent.Kind is TaskParentResolutionKind.UnresolvedExternal
                or TaskParentResolutionKind.AmbiguousExternal
                or TaskParentResolutionKind.Invalid)
            {
                var reference = parent.RawReference ?? task.Parent?.Trim() ?? string.Empty;
                var key = $"unresolved:{reference.ToLowerInvariant()}";
                if (!syntheticChildren.TryGetValue(key, out var groupedChildren))
                {
                    groupedChildren = [];
                    syntheticChildren[key] = groupedChildren;
                    syntheticTitles[key] = parent.Kind == TaskParentResolutionKind.Invalid
                        ? $"Invalid parent · {reference}"
                        : parent.DisplayTitle ?? "Unresolved parent";
                }
                groupedChildren.Add(taskId);
                roots.Remove(taskId);
            }
        }

        var rows = new List<BacklogHierarchyRow>();
        var rendered = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rootId in OrderTaskIds(roots, all))
            AppendTask(rootId, 0);

        foreach (var key in syntheticChildren.Keys
                     .OrderBy(key => syntheticTitles[key], StringComparer.Ordinal)
                     .ThenBy(key => key, StringComparer.Ordinal))
        {
            var groupedChildren = syntheticChildren[key];
            var collapsed = collapsedKeys.Contains(key);
            rows.Add(new(
                task: null,
                key,
                syntheticTitles[key],
                depth: 0,
                isParent: true,
                isExpanded: !collapsed,
                childCount: groupedChildren.Count,
                visibleChildCount: groupedChildren.Count,
                sourceKindBadge: "Unresolved Parent",
                status: "Needs repair",
                degradedReason: "This Parent relationship cannot be resolved."));
            if (!collapsed)
            {
                foreach (var childId in OrderTaskIds(groupedChildren, all))
                    AppendTask(childId, 1);
            }
            else
            {
                foreach (var childId in groupedChildren)
                    MarkCovered(childId);
            }
        }

        // Malformed cycles have no root. Keep every affected Task visible once.
        foreach (var taskId in OrderTaskIds(required.Where(id => !covered.Contains(id)), all))
        {
            degradedReasons.TryAdd(taskId, "Parent relationship contains a cycle.");
            AppendTask(taskId, 0);
        }

        return rows;

        void AppendTask(string taskId, int depth)
        {
            covered.Add(taskId);
            if (!rendered.Add(taskId))
                return;

            var task = all[taskId];
            var isParent = GlassworkTask.Types.IsParent(task.Type);
            var directChildren = children[taskId];
            var collapsed = isParent && collapsedKeys.Contains(task.Id);
            rows.Add(new(
                task,
                task.Id,
                task.Title,
                depth,
                isParent,
                isExpanded: isParent && !collapsed,
                childCount: hierarchy.GetChildren(task.Id).Count,
                visibleChildCount: directChildren.Count,
                sourceKindBadge: SourceBadge(task),
                status: task.Status,
                degradedReason: degradedReasons.GetValueOrDefault(taskId)));

            if (collapsed)
            {
                foreach (var childId in directChildren)
                    MarkCovered(childId);
                return;
            }

            foreach (var childId in OrderTaskIds(directChildren, all))
                AppendTask(childId, depth + 1);
        }

        void MarkCovered(string taskId)
        {
            if (!covered.Add(taskId))
                return;
            foreach (var childId in children[taskId])
                MarkCovered(childId);
        }
    }

    private static IEnumerable<string> OrderTaskIds(
        IEnumerable<string> taskIds,
        IReadOnlyDictionary<string, GlassworkTask> tasks) =>
        taskIds
            .OrderBy(id => tasks[id].Title, StringComparer.Ordinal)
            .ThenBy(id => id, StringComparer.Ordinal);

    private static string SourceBadge(GlassworkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceKind))
            return task.SourceKind.Trim();
        if (GlassworkTask.Types.IsParent(task.Type))
            return "Parent Task";
        return string.Equals(task.Type, GlassworkTask.Types.Bug, StringComparison.Ordinal)
            ? "Bug"
            : "Task";
    }
}
