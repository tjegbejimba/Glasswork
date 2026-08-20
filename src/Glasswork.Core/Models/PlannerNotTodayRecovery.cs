using System.Text.Json;

namespace Glasswork.Core.Models;

public sealed class PlannerNotTodayRecovery
{
    internal PlannerNotTodayRecovery(
        string id,
        string title,
        DateTimeOffset undoUntil,
        IReadOnlyList<PlannerNotTodayTargetState> targets)
    {
        Id = id;
        Title = title;
        UndoUntil = undoUntil;
        Targets = targets;
    }

    public string Id { get; }
    public string Title { get; }
    public DateTimeOffset UndoUntil { get; }
    public int AffectedTaskCount => Targets.Count;
    public string RestoreControlName => $"Restore {Title}";
    internal IReadOnlyList<PlannerNotTodayTargetState> Targets { get; }
}

internal sealed record PlannerNotTodayTargetState(
    string TaskId,
    DateTime? PriorMyDay,
    JsonElement PriorDismissal,
    string RestoreFromRevision);
