using Glasswork.Core.Services;

namespace Glasswork.Core.Models;

public sealed record TaskDetailAncestor(
    string TaskId,
    string DisplayTitle,
    int? AdoId);

public sealed record TaskDetailHierarchyProjection(
    string? SourceBadgeText,
    IReadOnlyList<TaskDetailAncestor> Ancestors,
    TaskParentResolution Parent,
    TaskLink? PrimaryAdo,
    int? PrimaryAdoId,
    string? PrimaryAdoDisplayText,
    IReadOnlyList<TaskLink> VisibleLinks,
    bool ShowChildren,
    bool ShowSubtasks)
{
    public static TaskDetailHierarchyProjection Project(
        GlassworkTask task,
        IEnumerable<GlassworkTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(tasks);

        var hierarchy = new TaskHierarchyPolicy(tasks);
        var ancestors = hierarchy.GetAncestors(task.Id)
            .Reverse()
            .Select(ancestor => new TaskDetailAncestor(
                ancestor.Id,
                DisplayTitle(ancestor),
                ancestor.AdoLink))
            .ToArray();

        var primaryAdoIndex = task.Links.FindIndex(
            link => link.Type == TaskLink.Types.Ado);
        var primaryAdo = primaryAdoIndex >= 0
            ? task.Links[primaryAdoIndex]
            : null;
        var primaryAdoId = AdoParentIdExtractor.TryExtractId(primaryAdo?.Value);
        var visibleLinks = task.Links
            .Where((_, index) => index != primaryAdoIndex)
            .ToArray();
        var isParent = GlassworkTask.Types.IsParent(task.Type);

        return new(
            SourceBadgeText: !string.IsNullOrWhiteSpace(task.SourceKind)
                ? task.SourceKind.Trim()
                : isParent ? "Parent Task" : null,
            Ancestors: ancestors,
            Parent: hierarchy.ResolveParent(task),
            PrimaryAdo: primaryAdo,
            PrimaryAdoId: primaryAdoId,
            PrimaryAdoDisplayText: PrimaryAdoText(primaryAdo, primaryAdoId),
            VisibleLinks: visibleLinks,
            ShowChildren: isParent,
            ShowSubtasks: !isParent);
    }

    private static string DisplayTitle(GlassworkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Title))
            return task.Title.Trim();
        if (!string.IsNullOrWhiteSpace(task.AdoTitle))
            return task.AdoTitle.Trim();
        return task.AdoLink is int adoId
            ? $"Unresolved parent · ADO #{adoId}"
            : "Unresolved parent";
    }

    private static string? PrimaryAdoText(TaskLink? link, int? adoId)
    {
        if (link is null)
            return null;
        if (!string.IsNullOrWhiteSpace(link.Label))
            return link.Label.Trim();
        return adoId is int id
            ? $"ADO #{id}"
            : string.IsNullOrWhiteSpace(link.Value) ? "Unresolved ADO Link" : link.Value.Trim();
    }
}
