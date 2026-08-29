namespace Glasswork.Core.Models;

public sealed class BacklogHierarchyRow
{
    public GlassworkTask? Task { get; }
    public string Key { get; }
    public string Title { get; }
    public int Depth { get; }
    public bool IsParent { get; }
    public bool IsLeaf => !IsParent;
    public bool HasTask => Task is not null;
    public bool IsExpanded { get; }
    public bool IsCollapsed => IsParent && !IsExpanded;
    public int ChildCount { get; }
    public int VisibleChildCount { get; }
    public bool HasVisibleChildren => VisibleChildCount > 0;
    public string ChildCountText => VisibleChildCount == ChildCount
        ? ChildCount == 1 ? "1 child" : $"{ChildCount} children"
        : $"{VisibleChildCount} of {ChildCount} children";
    public string SourceKindBadge { get; }
    public string Status { get; }
    public bool IsDegraded => !string.IsNullOrWhiteSpace(DegradedReason);
    public string? DegradedReason { get; }

    public BacklogHierarchyRow(
        GlassworkTask? task,
        string key,
        string title,
        int depth,
        bool isParent,
        bool isExpanded,
        int childCount,
        int visibleChildCount,
        string sourceKindBadge,
        string status,
        string? degradedReason = null)
    {
        Task = task;
        Key = key;
        Title = title;
        Depth = depth;
        IsParent = isParent;
        IsExpanded = isExpanded;
        ChildCount = childCount;
        VisibleChildCount = visibleChildCount;
        SourceKindBadge = sourceKindBadge;
        Status = status;
        DegradedReason = degradedReason;
    }
}
