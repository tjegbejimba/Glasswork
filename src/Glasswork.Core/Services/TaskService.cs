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

    public TaskService(VaultService vault)
    {
        _vault = vault;
    }

    /// <summary>
    /// Create a new task with auto-generated ID, save to vault.
    /// </summary>
    public GlassworkTask CreateTask(string title, string priority = "medium", string? parent = null)
    {
        var task = new GlassworkTask
        {
            Id = VaultService.GenerateId(title),
            Title = title,
            Status = GlassworkTask.Statuses.Todo,
            Priority = priority,
            Created = DateTime.Today,
            Parent = parent,
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
    /// Get incomplete tasks that were on My Day for a previous date (carryover candidates).
    /// </summary>
    public List<GlassworkTask> GetCarryoverTasks()
    {
        return _vault.LoadAll()
            .Where(t => t.MyDay.HasValue
                        && t.MyDay.Value.Date < DateTime.Today
                        && t.Status != GlassworkTask.Statuses.Done)
            .ToList();
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
}
