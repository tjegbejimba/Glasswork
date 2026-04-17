using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents a single task stored as a markdown file in the Obsidian vault.
/// </summary>
public partial class GlassworkTask : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Status { get; set; } = "todo";
    [ObservableProperty] public partial string Priority { get; set; } = "medium";
    [ObservableProperty] public partial DateTime Created { get; set; } = DateTime.Today;
    [ObservableProperty] public partial DateTime? CompletedAt { get; set; }
    [ObservableProperty] public partial DateTime? Due { get; set; }
    [ObservableProperty] public partial DateTime? MyDay { get; set; }
    [ObservableProperty] public partial int? AdoLink { get; set; }
    [ObservableProperty] public partial string? AdoTitle { get; set; }
    [ObservableProperty] public partial string? Parent { get; set; }
    [ObservableProperty] public partial string Body { get; set; } = string.Empty;
    [ObservableProperty] public partial List<string> ContextLinks { get; set; } = [];
    [ObservableProperty] public partial List<string> Tags { get; set; } = [];
    [ObservableProperty] public partial List<SubTask> Subtasks { get; set; } = [];

    public static class Statuses
    {
        public const string Todo = "todo";
        public const string InProgress = "in-progress";
        public const string Done = "done";
    }

    public static class Priorities
    {
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";
        public const string Urgent = "urgent";
    }

    /// <summary>
    /// Returns true if this task is marked for today's My Day view.
    /// </summary>
    public bool IsMyDay => MyDay.HasValue && MyDay.Value.Date == DateTime.Today;
}

/// <summary>
/// Represents an inline subtask (checkbox) within a parent task's body.
/// </summary>
public partial class SubTask : ObservableObject
{
    [ObservableProperty] public partial string Text { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsCompleted { get; set; }
}
