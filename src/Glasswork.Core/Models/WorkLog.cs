using System;
using System.Collections.Generic;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents a weekly work log journal entry stored at wiki/journal/YYYY-WNN.md.
/// Work logs are agent-generated summaries of completed work with anti-hallucination rules.
/// </summary>
public class WorkLog
{
    /// <summary>
    /// Type identifier, always "work-log".
    /// </summary>
    public string Type { get; set; } = "work-log";

    /// <summary>
    /// Period granularity: "week" for weekly logs.
    /// </summary>
    public string Period { get; set; } = "week";

    /// <summary>
    /// ISO week number in format "YYYY-WNN" (e.g., "2026-W21").
    /// </summary>
    public string Week { get; set; } = string.Empty;

    /// <summary>
    /// First date of the covered period (inclusive).
    /// </summary>
    public DateTime DateFrom { get; set; }

    /// <summary>
    /// Last date of the covered period (inclusive).
    /// </summary>
    public DateTime DateTo { get; set; }

    /// <summary>
    /// UTC timestamp when this log was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// The generator that created this log (e.g., "copilot").
    /// </summary>
    public string GeneratedBy { get; set; } = string.Empty;

    /// <summary>
    /// Task IDs referenced in this work log.
    /// </summary>
    public List<string> TasksReferenced { get; set; } = [];

    /// <summary>
    /// Main narrative summary (2-3 paragraphs).
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Key insights and breakthrough moments (optional section).
    /// </summary>
    public string KeyInsights { get; set; } = string.Empty;

    /// <summary>
    /// Strategic thinking highlights (optional section).
    /// </summary>
    public string StrategicThinking { get; set; } = string.Empty;

    /// <summary>
    /// Tasks completed during the period.
    /// </summary>
    public string TasksCompleted { get; set; } = string.Empty;

    /// <summary>
    /// Tasks in progress at the end of the period (optional section).
    /// </summary>
    public string InProgress { get; set; } = string.Empty;

    /// <summary>
    /// Frustrations or roadblocks encountered (optional section).
    /// </summary>
    public string Frustrations { get; set; } = string.Empty;
}
