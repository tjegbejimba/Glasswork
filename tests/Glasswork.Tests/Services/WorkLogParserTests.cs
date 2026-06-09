using System;
using System.Collections.Generic;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests.Services;

[TestClass]
public class WorkLogParserTests
{
    [TestMethod]
    public void Parse_ValidWorkLog_ReturnsWorkLogWithAllSections()
    {
        // Arrange
        var markdown = @"---
type: work-log
period: week
week: 2026-W21
date_from: 2026-05-18
date_to: 2026-05-24
generated_at: 2026-05-26T14:00:00Z
generated_by: copilot
tasks_referenced: [auth-fix, my-day-virtual-promotion]
---

## Summary
Completed 7 tasks across 3 projects. Fixed authentication flow and promoted My Day feature to production.

## Key Insights & Breakthrough Moments
- Discovered race condition in token refresh
- Realized virtual scrolling doubles My Day responsiveness

## Strategic Thinking Highlights
- Prioritized auth fix over new features
- Deferred migration to focus on stability

## Tasks Completed
- [[auth-fix]] — Fix token refresh race condition, ADO #1234
- [[my-day-virtual-promotion]] — Virtual scrolling for My Day list

## In Progress at Week End
- [[migration]] — Database schema migration in progress

## Frustrations or Roadblocks
- Build server downtime on Tuesday blocked PR merge
";

        var parser = new WorkLogParser();

        // Act
        var workLog = parser.Parse(markdown);

        // Assert
        Assert.AreEqual("work-log", workLog.Type);
        Assert.AreEqual("week", workLog.Period);
        Assert.AreEqual("2026-W21", workLog.Week);
        Assert.AreEqual(new DateTime(2026, 5, 18), workLog.DateFrom);
        Assert.AreEqual(new DateTime(2026, 5, 24), workLog.DateTo);
        Assert.AreEqual(new DateTime(2026, 5, 26, 14, 0, 0, DateTimeKind.Utc), workLog.GeneratedAt);
        Assert.AreEqual("copilot", workLog.GeneratedBy);
        CollectionAssert.AreEqual(new[] { "auth-fix", "my-day-virtual-promotion" }, workLog.TasksReferenced);
        
        Assert.IsTrue(workLog.Summary.Contains("Completed 7 tasks"));
        Assert.IsTrue(workLog.KeyInsights.Contains("race condition"));
        Assert.IsTrue(workLog.StrategicThinking.Contains("Prioritized auth fix"));
        Assert.IsTrue(workLog.TasksCompleted.Contains("[[auth-fix]]"));
        Assert.IsTrue(workLog.InProgress.Contains("[[migration]]"));
        Assert.IsTrue(workLog.Frustrations.Contains("Build server downtime"));
    }

    [TestMethod]
    public void Parse_OptionalSectionsMissing_ReturnsWorkLogWithEmptyStrings()
    {
        // Arrange - minimal work log with only required sections
        var markdown = @"---
type: work-log
period: week
week: 2026-W21
date_from: 2026-05-18
date_to: 2026-05-24
generated_at: 2026-05-26T14:00:00Z
generated_by: copilot
---

## Summary
Minimal work log with only summary section.

## Tasks Completed
- [[task-1]] — Completed basic task
";

        var parser = new WorkLogParser();

        // Act
        var workLog = parser.Parse(markdown);

        // Assert - optional sections should be empty strings
        Assert.AreEqual(string.Empty, workLog.KeyInsights);
        Assert.AreEqual(string.Empty, workLog.StrategicThinking);
        Assert.AreEqual(string.Empty, workLog.InProgress);
        Assert.AreEqual(string.Empty, workLog.Frustrations);
        
        // Required sections should have content
        Assert.IsTrue(workLog.Summary.Contains("Minimal work log"));
        Assert.IsTrue(workLog.TasksCompleted.Contains("[[task-1]]"));
    }

    [TestMethod]
    public void Parse_MissingRequiredFrontmatter_ThrowsFormatException()
    {
        // Arrange - missing required field 'generated_by'
        var markdown = @"---
type: work-log
period: week
week: 2026-W21
date_from: 2026-05-18
date_to: 2026-05-24
generated_at: 2026-05-26T14:00:00Z
---

## Summary
Content here.
";

        var parser = new WorkLogParser();

        // Act & Assert
        var ex = Assert.ThrowsExactly<FormatException>(() => parser.Parse(markdown));
        Assert.IsTrue(ex.Message.Contains("generated_by"));
    }

    [TestMethod]
    public void Parse_EmptyTasksReferenced_ReturnsEmptyList()
    {
        // Arrange - no tasks_referenced field
        var markdown = @"---
type: work-log
period: week
week: 2026-W21
date_from: 2026-05-18
date_to: 2026-05-24
generated_at: 2026-05-26T14:00:00Z
generated_by: copilot
---

## Summary
No tasks this week.
";

        var parser = new WorkLogParser();

        // Act
        var workLog = parser.Parse(markdown);

        // Assert
        Assert.AreEqual(0, workLog.TasksReferenced.Count);
    }

    [TestMethod]
    public void Parse_InvalidDateFormat_ThrowsFormatException()
    {
        // Arrange - invalid date format
        var markdown = @"---
type: work-log
period: week
week: 2026-W21
date_from: invalid-date
date_to: 2026-05-24
generated_at: 2026-05-26T14:00:00Z
generated_by: copilot
---

## Summary
Content.
";

        var parser = new WorkLogParser();

        // Act & Assert
        Assert.ThrowsExactly<FormatException>(() => parser.Parse(markdown));
    }

    [TestMethod]
    public void Parse_MissingFrontmatterDelimiters_ThrowsFormatException()
    {
        // Arrange - no frontmatter delimiters
        var markdown = @"## Summary
This is not a valid work log format.
";

        var parser = new WorkLogParser();

        // Act & Assert
        var ex = Assert.ThrowsExactly<FormatException>(() => parser.Parse(markdown));
        Assert.IsTrue(ex.Message.Contains("frontmatter delimiters"));
    }
}
