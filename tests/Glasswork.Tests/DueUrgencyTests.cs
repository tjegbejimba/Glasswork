using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Regression guard for the "future due shown as overdue" report. A task using the
/// newer frontmatter schema (a <c>links:</c> block sequence plus a past <c>my_day:</c>)
/// with a future <c>due:</c> must parse to <see cref="DueUrgency.Future"/>, never
/// <see cref="DueUrgency.Overdue"/>. Dates are computed relative to today so the test
/// does not rot when the wall clock passes a hardcoded date.
/// </summary>
[TestClass]
public class DueUrgencyTests
{
    private readonly FrontmatterParser _parser = new();

    [TestMethod]
    public void Parse_NewerSchema_FutureDue_IsFutureNotOverdue()
    {
        var due = DateTime.Today.AddDays(12);
        var myDay = DateTime.Today.AddDays(-1); // yesterday — must not feed overdue

        var markdown = $"""
            ---
            id: honor-manifest-expectedasyncoperationduration-on-subse
            title: Honor manifest expectedAsyncOperationDuration on subsequent batch polls
            status: in-progress
            priority: medium
            created: 2026-06-08
            due: {due:yyyy-MM-dd}
            my_day: {myDay:yyyy-MM-dd}
            parent: 32681761
            links:
            - type: ado
              value: 38213284
            ---

            Some notes.
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(due.Date, task.Due!.Value.Date, "newer-schema due should parse to the future date");
        Assert.AreNotEqual(DueUrgency.Overdue, task.DueUrgency, "a future due must not be flagged overdue");
        Assert.AreEqual(DueUrgency.Future, task.DueUrgency);
    }

    [TestMethod]
    public void Parse_OlderSchema_FutureDue_IsFutureNotOverdue()
    {
        // Parity check: the flat ado_link scalar (older schema, no my_day) yields the
        // same urgency, proving the schema is not what drives the overdue flag.
        var due = DateTime.Today.AddDays(12);

        var markdown = $"""
            ---
            id: legacy-task
            title: Legacy imported task
            status: in-progress
            priority: medium
            created: 2026-06-08
            due: {due:yyyy-MM-dd}
            ado_link: 38213284
            parent: 32681761
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.AreEqual(due.Date, task.Due!.Value.Date);
        Assert.AreEqual(DueUrgency.Future, task.DueUrgency);
    }

    [TestMethod]
    public void Parse_NoDue_IsNoneNotOverdue()
    {
        // A missing/unparsed due must read as None, never Overdue.
        var markdown = """
            ---
            id: no-due-task
            title: Task without a due date
            status: in-progress
            priority: medium
            created: 2026-06-08
            links:
            - type: ado
              value: 38213284
            ---
            """;

        var task = _parser.Parse(markdown);

        Assert.IsFalse(task.Due.HasValue);
        Assert.AreEqual(DueUrgency.None, task.DueUrgency);
        Assert.AreNotEqual(DueUrgency.Overdue, task.DueUrgency);
    }
}
