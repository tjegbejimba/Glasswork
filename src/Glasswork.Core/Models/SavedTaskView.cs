using System;
using System.Collections.Generic;

namespace Glasswork.Core.Models;

/// <summary>
/// A named reusable filter over Tasks. Saved Task views describe how a Page lists
/// tasks, not task data itself, so they are persisted through UI State.
/// </summary>
public sealed class SavedTaskView
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TaskViewFilter Filter { get; set; } = new();
}

public sealed class TaskViewFilter
{
    public List<string> Statuses { get; set; } = [];
    public List<string> Priorities { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> LinkTypes { get; set; } = [];
    public string? SearchText { get; set; }
    public string Due { get; set; } = DueWindows.Any;
    public bool? HasBlockedSubtasks { get; set; }
    public bool? HasLinks { get; set; }
    public bool? InMyDayToday { get; set; }
    public bool? Ready { get; set; }
    public double? MinimumUrgencyScore { get; set; }
    public int? RecentActivityDays { get; set; }

    public static class DueWindows
    {
        public const string Any = "any";
        public const string None = "none";
        public const string Overdue = "overdue";
        public const string Today = "today";
        public const string Next7Days = "next-7-days";
        public const string Future = "future";
    }
}
