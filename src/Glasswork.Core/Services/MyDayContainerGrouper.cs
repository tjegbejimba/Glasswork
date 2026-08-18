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
/// <c>type: pbi</c> task under that PBI as a container card
/// (<see cref="GlassworkTask.TodaysChildren"/>). The PBI is pulled in to host its
/// children even when it would not independently promote — a
/// <i>container-only host</i>. This never changes the promotion policy
/// (<see cref="MyDayPromotionPolicy"/> / Task Query);
/// it only reshapes the rows for display.
///
/// Layout: standalone (non-container) rows first, in their original promoted order;
/// then PBI container rows, ordered by earliest child due then PBI title. Grouping is
/// one level only — a child that is itself a container PBI stays at the top level as its
/// own container rather than being nested.
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

        var parentResolver = new TaskParentResolver(allTasks);

        // First pass: bucket promoted children under their resolved PBI parent,
        // preserving promoted order within each bucket.
        var childrenByPbi = new Dictionary<string, List<GlassworkTask>>(StringComparer.Ordinal);
        foreach (var t in promoted)
        {
            var parentId = parentResolver.ResolveTaskId(t.Parent);
            if (parentId is null || !allTasks.TryGetValue(parentId, out var parent)) continue;
            if (parent.Type != GlassworkTask.Types.Pbi) continue;

            if (!childrenByPbi.TryGetValue(parentId, out var bucket))
            {
                bucket = [];
                childrenByPbi[parentId] = bucket;
            }
            bucket.Add(t);
        }

        // One-level guard: a child that is itself a container (hosts its own promoted
        // children) is not nested; it remains a top-level container. Drop such children
        // from their parent buckets, and drop any parent left with no children.
        var containerPbiIds = new HashSet<string>(childrenByPbi.Keys, StringComparer.Ordinal);
        foreach (var bucket in childrenByPbi.Values)
        {
            bucket.RemoveAll(c => containerPbiIds.Contains(c.Id));
        }
        foreach (var emptyKey in childrenByPbi.Where(kv => kv.Value.Count == 0)
                                              .Select(kv => kv.Key).ToList())
        {
            childrenByPbi.Remove(emptyKey);
        }

        var nestedChildIds = new HashSet<string>(
            childrenByPbi.Values.SelectMany(b => b.Select(c => c.Id)), StringComparer.Ordinal);
        var hostPbiIds = new HashSet<string>(childrenByPbi.Keys, StringComparer.Ordinal);

        // Standalone rows: promoted tasks that are neither nested children nor container
        // hosts. PBIs without actionable children are containers with nothing to show,
        // so they are omitted even when directly pinned. Original promoted order is preserved.
        var standalone = promoted
            .Where(t => !nestedChildIds.Contains(t.Id) && !hostPbiIds.Contains(t.Id))
            .Where(t => t.Type != GlassworkTask.Types.Pbi || t.TodaysSubtasks?.Count > 0)
            .ToList();

        // Container rows: reuse the promoted PBI instance when it independently promoted,
        // otherwise pull in a host clone (and compute its own in-file today's subtasks).
        var containers = new List<GlassworkTask>(childrenByPbi.Count);
        foreach (var (pbiId, children) in childrenByPbi)
        {
            var container = promoted.FirstOrDefault(t => string.Equals(t.Id, pbiId, StringComparison.Ordinal))
                ?? PullInHost(allTasks[pbiId], today);

            container.TodaysChildren = children
                .OrderBy(c => DueKey(c, today))
                .ToList();
            containers.Add(container);
        }

        containers = containers
            .OrderBy(EarliestChildDue)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var rows = new List<GlassworkTask>(standalone.Count + containers.Count);
        rows.AddRange(standalone);
        rows.AddRange(containers);
        return rows;
    }

    private static GlassworkTask PullInHost(GlassworkTask pbi, DateOnly today)
    {
        var host = pbi.Clone();
        host.TodaysSubtasks = MyDayPromotionPolicy.TodaysSubtasks(pbi, today);
        return host;
    }

    private static DateOnly DueKey(GlassworkTask child, DateOnly today) =>
        child.Due.HasValue ? DateOnly.FromDateTime(child.Due.Value.Date) : DateOnly.MaxValue;

    private static DateOnly EarliestChildDue(GlassworkTask container)
    {
        var earliest = DateOnly.MaxValue;
        if (container.TodaysChildren is null) return earliest;
        foreach (var c in container.TodaysChildren)
        {
            var key = c.Due.HasValue ? DateOnly.FromDateTime(c.Due.Value.Date) : DateOnly.MaxValue;
            if (key < earliest) earliest = key;
        }
        return earliest;
    }
}
