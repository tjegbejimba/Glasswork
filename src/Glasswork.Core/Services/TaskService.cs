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

    public TaskService(VaultService vault, IndexService index)
    {
        _vault = vault;
        _index = index;
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
        task.Status = newStatus;

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
        task.Status = newStatus;
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
    /// 2. Task due: task.Due <= today && Status != Done
    /// 3. Flagged subtask: any subtask has IsMyDay == true
    /// 4. Due subtask: any subtask has Due <= today && Status != Done
    /// </summary>
    public List<GlassworkTask> GetMyDay(bool includeDone, bool includeSubtasks)
    {
        var today = DateTime.Today;
        var tasks = new List<GlassworkTask>();

        foreach (var task in _index.All)
        {
            bool promoted = false;

            // Condition 1: Direct pin
            if (task.MyDay.HasValue && task.MyDay.Value.Date == today)
            {
                promoted = true;
            }

            // Condition 2: Task due (not done)
            if (!promoted && task.Due.HasValue && task.Due.Value.Date <= today && task.Status != GlassworkTask.Statuses.Done)
            {
                promoted = true;
            }

            // Conditions 3 & 4: Subtask-based promotion
            if (!promoted)
            {
                foreach (var subtask in task.Subtasks)
                {
                    // Condition 3: Flagged subtask
                    if (subtask.IsMyDay)
                    {
                        promoted = true;
                        break;
                    }

                    // Condition 4: Due subtask (not done)
                    if (subtask.Due.HasValue && subtask.Due.Value.Date <= today && !subtask.IsEffectivelyDone)
                    {
                        promoted = true;
                        break;
                    }
                }
            }

            // Apply includeDone filter
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
}
