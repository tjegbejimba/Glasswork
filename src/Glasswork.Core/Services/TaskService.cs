using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Orchestrates task operations: creation, status transitions, My Day toggling.
/// Handles business rules like setting completed_at timestamps.
/// </summary>
public class TaskService
{
    private readonly VaultService _vault;
    private readonly IndexService _index;
    private readonly Func<DateTimeOffset> _utcNow;

    public TaskService(VaultService vault, IndexService index, Func<DateTimeOffset>? utcNow = null)
    {
        _vault = vault;
        _index = index;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Create a new task with auto-generated ID, save to vault.
    /// </summary>
    public GlassworkTask CreateTask(
        string title,
        string priority = "medium",
        string? parent = null,
        int? adoLink = null,
        string? adoTitle = null,
        string? description = null,
        bool addToMyDay = false,
        IReadOnlyCollection<RelatedLink>? relatedLinks = null,
        string? size = null)
    {
        var id = VaultService.GenerateId(title);
        var suffix = 1;
        while (_vault.Exists(id))
            id = $"{VaultService.GenerateId(title)}-{suffix++}";

        var task = new GlassworkTask
        {
            Id = id,
            Title = title,
            Status = GlassworkTask.Statuses.Todo,
            Priority = priority,
            Created = DateTime.Today,
            Parent = parent,
            AdoLink = adoLink,
            AdoTitle = adoTitle,
            Description = description?.Trim() ?? string.Empty,
            MyDay = addToMyDay ? DateTime.Today : null,
            Size = size,
            RelatedLinks = relatedLinks is null
                ? []
                : relatedLinks
                    .Select(link => new RelatedLink
                    {
                        Slug = link.Slug,
                        DisplayName = link.DisplayName,
                    })
                    .ToList(),
        };

        _vault.Save(task, ifAbsent: true);
        return task;
    }

    /// <summary>
    /// Transition a task's status. Sets/clears completed_at as appropriate.
    /// </summary>
    public void SetStatus(GlassworkTask task, string newStatus)
    {
        ApplySetStatus(task, newStatus, () => DateTime.Now);
        _vault.Save(task);
    }

    /// <summary>
    /// Toggle My Day flag: adds today's date or clears it.
    /// </summary>
    public void ToggleMyDay(GlassworkTask task)
    {
        task.MyDay = task.IsMyDay ? null : DateTime.Today;
        _vault.Save(task);
    }

    /// <summary>
    /// Change a task's status without touching completed_at or updated_at.
    /// Used by drag-to-change-status in Board view where timestamp updates
    /// are not desired.
    /// </summary>
    public void SetStatusOnly(GlassworkTask task, string newStatus)
    {
        ApplySetStatusOnly(task, newStatus);
        _vault.Save(task);
    }

    public void MarkBlocked(GlassworkTask task, string reason)
    {
        ApplyMarkBlocked(task, reason, _utcNow);
        _vault.Save(task);
    }

    public void EditBlockedReason(GlassworkTask task, string reason)
    {
        ApplyEditBlockedReason(task, reason);
        _vault.Save(task);
    }

    public void RepairBlocked(GlassworkTask task, string reason, string blockedFromStatus)
    {
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can be repaired.");

        task.BlockedReason = ValidateBlockedReason(reason);
        task.BlockedFromStatus = NormalizeResumeStatus(blockedFromStatus);
        task.BlockedAt ??= _utcNow().ToUniversalTime();
        task.BlockedMetadataState = BlockedMetadataState.Valid;
        _vault.Save(task);
    }

    public void ResumeBlocked(GlassworkTask task, string? overrideStatus = null)
    {
        ApplyResumeBlocked(task, overrideStatus);
        _vault.Save(task);
    }

    public void Cancel(GlassworkTask task, string reason)
    {
        ApplyCancel(task, reason, _utcNow);
        _vault.Save(task);
    }

    public void RestoreCancelled(
        GlassworkTask task,
        string restoreStatus = GlassworkTask.Statuses.Todo)
    {
        ApplyRestoreCancelled(task, restoreStatus);
        _vault.Save(task);
    }

    /// <summary>
    /// Get incomplete tasks that were on My Day for a previous date (carryover candidates).
    /// </summary>
    public List<GlassworkTask> GetCarryoverTasks()
    {
        return _index.Carryover(DateTime.Today).ToList();
    }

    /// <summary>
    /// Move all carryover tasks to today's My Day.
    /// </summary>
    public void CarryAllToToday()
    {
        foreach (var task in GetCarryoverTasks())
        {
            task.MyDay = DateTime.Today;
            _vault.Save(task);
        }
    }

    /// <summary>
    /// Promote an inline subtask to a full task file with parent link.
    /// Removes the subtask from the parent.
    /// </summary>
    public GlassworkTask PromoteSubtask(GlassworkTask parent, int subtaskIndex)
    {
        if (subtaskIndex < 0 || subtaskIndex >= parent.Subtasks.Count)
            throw new ArgumentOutOfRangeException(nameof(subtaskIndex));

        var subtask = parent.Subtasks[subtaskIndex];
        var newTask = CreateTask(subtask.Text, parent: parent.Id, size: subtask.Size);

        if (subtask.IsCompleted)
            SetStatus(newTask, GlassworkTask.Statuses.Done);

        parent.Subtasks.RemoveAt(subtaskIndex);
        _vault.Save(parent);

        return newTask;
    }

    /// <summary>
    /// Remove a subtask from its parent and persist the parent. Unlike <see cref="PromoteSubtask"/>,
    /// this does not create a new top-level task — the subtask is gone for good.
    /// </summary>
    public void DeleteSubtask(GlassworkTask parent, int subtaskIndex)
    {
        if (subtaskIndex < 0 || subtaskIndex >= parent.Subtasks.Count)
            throw new ArgumentOutOfRangeException(nameof(subtaskIndex));

        parent.Subtasks.RemoveAt(subtaskIndex);
        _vault.Save(parent);
    }

    public static void EnsureCanMutate(GlassworkTask task)
    {
        if (task.IsCancelled)
            throw new InvalidOperationException("Cancelled tasks must be restored before they can be changed.");
        if (task.IsBlocked && task.NeedsBlockerDetails)
            throw new InvalidOperationException("Blocked task needs blocker details before it can be changed.");
    }

    public static void ApplySetStatus(GlassworkTask task, string newStatus, Func<DateTime> localNow)
    {
        if (newStatus == GlassworkTask.Statuses.Blocked)
            throw new InvalidOperationException("Use MarkBlocked to move a task to blocked.");
        if (newStatus == GlassworkTask.Statuses.Cancelled)
            throw new InvalidOperationException("Use Cancel to move a task to cancelled.");
        EnsureCanMutate(task);
        var wasDone = task.Status == GlassworkTask.Statuses.Done;
        task.Status = newStatus;
        if (newStatus != GlassworkTask.Statuses.Blocked)
            ClearBlockedState(task);

        if (newStatus == GlassworkTask.Statuses.Done)
        {
            if (!wasDone)
                task.CompletedAt = localNow();
        }
        else
        {
            task.CompletedAt = null;
        }
    }

    public static void ApplySetStatusOnly(GlassworkTask task, string newStatus)
    {
        if (newStatus == GlassworkTask.Statuses.Blocked)
            throw new InvalidOperationException("Use MarkBlocked to move a task to blocked.");
        if (newStatus == GlassworkTask.Statuses.Cancelled)
            throw new InvalidOperationException("Use Cancel to move a task to cancelled.");
        EnsureCanMutate(task);
        task.Status = newStatus;
        if (newStatus != GlassworkTask.Statuses.Blocked)
            ClearBlockedState(task);
    }

    public static void ApplyMarkBlocked(GlassworkTask task, string reason, Func<DateTimeOffset> utcNow)
    {
        var trimmedReason = ValidateBlockedReason(reason);
        ValidateBlockedSourceStatus(task.Status);

        task.BlockedReason = trimmedReason;
        task.BlockedAt = utcNow().ToUniversalTime();
        task.BlockedFromStatus = task.Status;
        task.BlockedMetadataState = BlockedMetadataState.Valid;
        task.Status = GlassworkTask.Statuses.Blocked;
        task.CompletedAt = null;
    }

    public static void ApplyEditBlockedReason(GlassworkTask task, string reason)
    {
        EnsureCanMutate(task);
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can edit blocker details.");

        task.BlockedReason = ValidateBlockedReason(reason);
        task.BlockedMetadataState = task.BlockedAt.HasValue
            && task.BlockedFromStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress
                ? BlockedMetadataState.Valid
                : BlockedMetadataState.NeedsDetails;
    }

    public static void ApplyResumeBlocked(GlassworkTask task, string? overrideStatus = null)
    {
        EnsureCanMutate(task);
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can be resumed.");

        var resumeStatus = string.IsNullOrWhiteSpace(overrideStatus)
            ? task.BlockedFromStatus
            : overrideStatus;
        task.Status = NormalizeResumeStatus(resumeStatus);
        task.CompletedAt = null;
        ClearBlockedState(task);
    }

    public static void ApplyRepairBlocked(
        GlassworkTask task,
        string reason,
        string blockedFromStatus,
        Func<DateTimeOffset> utcNow)
    {
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can be repaired.");

        task.BlockedReason = ValidateBlockedReason(reason);
        task.BlockedFromStatus = NormalizeResumeStatus(blockedFromStatus);
        task.BlockedAt ??= utcNow().ToUniversalTime();
        task.BlockedMetadataState = BlockedMetadataState.Valid;
    }

    public static void ApplyCancel(
        GlassworkTask task,
        string reason,
        Func<DateTimeOffset> utcNow)
    {
        if (task.Status is not (
            GlassworkTask.Statuses.Todo
            or GlassworkTask.Statuses.InProgress
            or GlassworkTask.Statuses.Blocked))
        {
            throw new InvalidOperationException(
                "Only todo, in-progress, or blocked tasks can be cancelled.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
            throw new ArgumentException("Cancellation reason is required.", nameof(reason));

        task.Status = GlassworkTask.Statuses.Cancelled;
        task.CancelledAt = utcNow().ToUniversalTime();
        task.CancellationReason = trimmedReason;
        task.MyDay = null;
        task.CompletedAt = null;
        ClearBlockedState(task);
    }

    public static void ApplyRestoreCancelled(
        GlassworkTask task,
        string restoreStatus = GlassworkTask.Statuses.Todo)
    {
        if (!task.IsCancelled)
            throw new InvalidOperationException("Only cancelled tasks can be restored.");
        if (restoreStatus is not (
            GlassworkTask.Statuses.Todo
            or GlassworkTask.Statuses.InProgress))
        {
            throw new InvalidOperationException(
                "Cancelled tasks can only be restored to todo or in-progress.");
        }

        task.Status = restoreStatus;
        task.CancelledAt = null;
        task.CancellationReason = null;
        task.CompletedAt = null;
        ClearBlockedState(task);
    }

    private static string ValidateBlockedReason(string reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Blocked reason is required.", nameof(reason));
        return trimmed;
    }

    private static void ValidateBlockedSourceStatus(string? status)
    {
        if (status is not (GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress))
            throw new InvalidOperationException("Only todo or in-progress tasks can be marked blocked.");
    }

    private static string NormalizeResumeStatus(string? status)
    {
        return status switch
        {
            GlassworkTask.Statuses.Todo => GlassworkTask.Statuses.Todo,
            GlassworkTask.Statuses.InProgress => GlassworkTask.Statuses.InProgress,
            _ => throw new InvalidOperationException("Blocked tasks can only resume to todo or in-progress."),
        };
    }

    private static void ClearBlockedState(GlassworkTask task)
    {
        task.BlockedReason = null;
        task.BlockedAt = null;
        task.BlockedFromStatus = null;
        task.BlockedMetadataState = BlockedMetadataState.None;
    }
}
