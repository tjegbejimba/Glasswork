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
    public GlassworkTask CreateTask(string title, string priority = "medium", string? parent = null,
        int? adoLink = null, string? adoTitle = null)
    {
        var task = new GlassworkTask
        {
            Id = VaultService.GenerateId(title),
            Title = title,
            Status = GlassworkTask.Statuses.Todo,
            Priority = priority,
            Created = DateTime.Today,
            Parent = parent,
            AdoLink = adoLink,
            AdoTitle = adoTitle,
        };

        _vault.Save(task);
        return task;
    }

    /// <summary>
    /// Transition a task's status. Sets/clears completed_at as appropriate.
    /// </summary>
    public void SetStatus(GlassworkTask task, string newStatus)
    {
        if (newStatus == GlassworkTask.Statuses.Blocked)
            throw new InvalidOperationException("Use MarkBlocked to move a task to blocked.");
        EnsureBlockedTaskCanMutate(task);
        task.Status = newStatus;
        if (newStatus != GlassworkTask.Statuses.Blocked)
            ClearBlockedState(task);

        if (newStatus == GlassworkTask.Statuses.Done)
        {
            task.CompletedAt = DateTime.Now;
        }
        else
        {
            task.CompletedAt = null;
        }

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
        if (newStatus == GlassworkTask.Statuses.Blocked)
            throw new InvalidOperationException("Use MarkBlocked to move a task to blocked.");
        EnsureBlockedTaskCanMutate(task);
        task.Status = newStatus;
        if (newStatus != GlassworkTask.Statuses.Blocked)
            ClearBlockedState(task);
        _vault.Save(task);
    }

    public void MarkBlocked(GlassworkTask task, string reason)
    {
        var trimmedReason = ValidateBlockedReason(reason);
        ValidateBlockedSourceStatus(task.Status);

        task.BlockedReason = trimmedReason;
        task.BlockedAt = _utcNow().ToUniversalTime();
        task.BlockedFromStatus = task.Status;
        task.BlockedMetadataState = BlockedMetadataState.Valid;
        task.Status = GlassworkTask.Statuses.Blocked;
        task.CompletedAt = null;
        _vault.Save(task);
    }

    public void EditBlockedReason(GlassworkTask task, string reason)
    {
        EnsureBlockedTaskCanMutate(task);
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can edit blocker details.");

        task.BlockedReason = ValidateBlockedReason(reason);
        task.BlockedMetadataState = task.BlockedAt.HasValue
            && task.BlockedFromStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress
                ? BlockedMetadataState.Valid
                : BlockedMetadataState.NeedsDetails;
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
        EnsureBlockedTaskCanMutate(task);
        if (!task.IsBlocked)
            throw new InvalidOperationException("Only blocked tasks can be resumed.");

        var resumeStatus = string.IsNullOrWhiteSpace(overrideStatus)
            ? task.BlockedFromStatus
            : overrideStatus;
        task.Status = NormalizeResumeStatus(resumeStatus);
        task.CompletedAt = null;
        ClearBlockedState(task);
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
        var newTask = CreateTask(subtask.Text, parent: parent.Id);

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

    /// <summary>
    /// Get tasks in My Day for today, applying the four-condition promotion rule from ADR 0008:
    /// 1. Direct pin: task.MyDay == today
    /// 2. Task due: task.Due &lt;= today &amp;&amp; Status != Done &amp;&amp; Type != pbi (PBIs don't self-promote on their own due — ADR 0016)
    /// 3. Flagged subtask: any subtask has IsMyDay == true
    /// 4. Due subtask: any subtask has Due &lt;= today &amp;&amp; Status != Done
    /// </summary>
    public List<GlassworkTask> GetMyDay(bool includeDone, bool includeSubtasks)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var tasks = new List<GlassworkTask>();

        foreach (var task in _index.All)
        {
            var promoted = MyDayPromotionPolicy.IsTaskInMyDayToday(task, today, new HashSet<string>(StringComparer.Ordinal))
                || (includeDone
                    && task.Status == GlassworkTask.Statuses.Done
                    && task.MyDay.HasValue
                    && DateOnly.FromDateTime(task.MyDay.Value.Date) == today);

            if (promoted)
            {
                if (includeDone || task.Status != GlassworkTask.Statuses.Done)
                {
                    var clone = task.Clone();
                    
                    // Apply includeSubtasks filter
                    if (!includeSubtasks)
                    {
                        clone.Subtasks.Clear();
                    }
                    
                    tasks.Add(clone);
                }
            }
        }

        return tasks;
    }

    private static void EnsureBlockedTaskCanMutate(GlassworkTask task)
    {
        if (task.IsBlocked && task.NeedsBlockerDetails)
            throw new InvalidOperationException("Blocked task needs blocker details before it can be changed.");
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
