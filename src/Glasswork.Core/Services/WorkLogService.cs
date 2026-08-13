using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Glasswork.Core.Models;
using Glasswork.Core.Queries;

namespace Glasswork.Core.Services;

/// <summary>
/// Generates weekly work log markdown from completed tasks,
/// grouped by ADO work item for connect-season reporting.
/// </summary>
public class WorkLogService
{
    private readonly VaultService _vault;
    private readonly ITaskQuery _taskQuery;

    public WorkLogService(VaultService vault, IndexService index)
        : this(vault, new WarmIndexTaskQuery(index, new BacklinkIndex()))
    {
    }

    public WorkLogService(VaultService vault, ITaskQuery taskQuery)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _taskQuery = taskQuery ?? throw new ArgumentNullException(nameof(taskQuery));
    }

    /// <summary>
    /// Generate a markdown work log for the week starting at <paramref name="weekStart"/>.
    /// Groups completed tasks by ADO item, then lists standalone tasks.
    /// </summary>
    public string GenerateWeeklyLog(DateTime weekStart)
    {
        var result = _taskQuery.Execute(new TaskQueryRequest(
            DateTimeOffset.Now,
            new CompletedWorkTaskSelection(weekStart, weekStart.AddDays(7))));
        if (!result.IsSuccess)
        {
            var diagnostics = string.Join(
                "; ",
                result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new InvalidOperationException($"Completed-work Task Query failed: {diagnostics}");
        }

        var completed = result.MaterializeTasks();

        var sb = new StringBuilder();
        sb.AppendLine($"# Work Log: Week of {weekStart:yyyy-MM-dd}");
        sb.AppendLine();

        // Group by ADO link
        var adoGroups = completed
            .Where(t => t.AdoLink.HasValue)
            .GroupBy(t => t.AdoLink!.Value)
            .OrderBy(g => g.Key);

        foreach (var group in adoGroups)
        {
            var adoTitle = group.First().AdoTitle ?? $"ADO #{group.Key}";
            sb.AppendLine($"## {adoTitle} (#{group.Key})");
            sb.AppendLine();
            foreach (var task in group)
            {
                sb.AppendLine($"- [x] {task.Title} — completed {task.CompletedAt:yyyy-MM-dd}");
            }
            sb.AppendLine();
        }

        // Standalone tasks (no ADO link)
        var standalone = completed.Where(t => !t.AdoLink.HasValue).ToList();
        if (standalone.Count > 0)
        {
            sb.AppendLine("## Other Tasks");
            sb.AppendLine();
            foreach (var task in standalone)
            {
                sb.AppendLine($"- [x] {task.Title} — completed {task.CompletedAt:yyyy-MM-dd}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate and save the work log to the vault as a special _worklog file.
    /// </summary>
    public string GenerateAndSave(DateTime weekStart)
    {
        var log = GenerateWeeklyLog(weekStart);
        var fileName = $"_worklog-{weekStart:yyyy-MM-dd}.md";
        var path = Path.Combine(_vault.VaultPath, fileName);
        File.WriteAllText(path, log);
        return log;
    }
}
