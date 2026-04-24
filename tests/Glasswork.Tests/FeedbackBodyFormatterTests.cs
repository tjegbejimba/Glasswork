using System;
using Glasswork.Core.Feedback;

namespace Glasswork.Tests;

[TestClass]
public class FeedbackBodyFormatterTests
{
    private static FeedbackContext SampleContext(
        string? pageName = "MyDayPage",
        string? activeTaskId = "task-2026-04-24-fix-thing",
        string appVersion = "0.5.0",
        string osDescription = "Microsoft Windows 10.0.26100",
        string runtimeVersion = ".NET 10.0.0") =>
        new(
            PageName: pageName,
            ActiveTaskId: activeTaskId,
            AppVersion: appVersion,
            OsDescription: osDescription,
            RuntimeVersion: runtimeVersion,
            CapturedAtUtc: new DateTimeOffset(2026, 4, 24, 22, 30, 15, TimeSpan.Zero));

    [TestMethod]
    public void Build_PreservesCategoryHeader()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "Something broke", SampleContext());

        StringAssert.Contains(body, "_Filed from Glasswork feedback dialog — category: **Bug**_");
    }

    [TestMethod]
    public void Build_PreservesUserBodyVerbatim()
    {
        const string userBody = "Steps:\n1. Open My Day\n2. Click reload\n\nExpected: refresh\nActual: crash";

        var body = FeedbackBodyFormatter.Build("Bug", userBody, SampleContext());

        StringAssert.Contains(body, userBody);
    }

    [TestMethod]
    public void Build_IncludesContextSection()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "broke", SampleContext());

        StringAssert.Contains(body, "## Context");
    }

    [TestMethod]
    public void Build_RendersAllContextFieldsWhenPresent()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "broke", SampleContext());

        StringAssert.Contains(body, "MyDayPage");
        StringAssert.Contains(body, "task-2026-04-24-fix-thing");
        StringAssert.Contains(body, "0.5.0");
        StringAssert.Contains(body, "Microsoft Windows 10.0.26100");
        StringAssert.Contains(body, ".NET 10.0.0");
        StringAssert.Contains(body, "2026-04-24T22:30:15");
    }

    [TestMethod]
    public void Build_ShowsNoneForMissingPage()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "broke",
            SampleContext(pageName: null));

        // Page row must still appear (so reviewer knows we tried to capture it),
        // but its value reads "(none)" rather than blank.
        StringAssert.Contains(body, "Page");
        StringAssert.Contains(body, "(none)");
    }

    [TestMethod]
    public void Build_ShowsNoneForMissingActiveTask()
    {
        var body = FeedbackBodyFormatter.Build("Feature Request", "idea",
            SampleContext(activeTaskId: null));

        StringAssert.Contains(body, "Active task");
        StringAssert.Contains(body, "(none)");
    }

    [TestMethod]
    public void Build_NullContext_OmitsContextSection()
    {
        var body = FeedbackBodyFormatter.Build("General Feedback", "thoughts", context: null);

        StringAssert.Contains(body, "_Filed from Glasswork feedback dialog");
        StringAssert.Contains(body, "thoughts");
        Assert.IsFalse(body.Contains("## Context"),
            "Context section should be omitted when context is null.");
    }

    [TestMethod]
    public void Build_EmptyBody_StillIncludesHeaderAndContext()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "", SampleContext());

        StringAssert.Contains(body, "_Filed from Glasswork feedback dialog");
        StringAssert.Contains(body, "## Context");
        StringAssert.Contains(body, "MyDayPage");
        // No trailing whitespace garbage at the very end.
        Assert.AreEqual(body, body.TrimEnd() + (body.EndsWith('\n') ? "\n" : ""),
            "Should not have stray trailing whitespace beyond a single optional newline.");
    }

    [TestMethod]
    public void Build_NullBody_TreatedAsEmpty()
    {
        var body = FeedbackBodyFormatter.Build("Bug", body: null, SampleContext());

        StringAssert.Contains(body, "_Filed from Glasswork feedback dialog");
        StringAssert.Contains(body, "## Context");
    }

    [TestMethod]
    public void Build_TimestampIsIso8601Utc()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "x", SampleContext());

        // We render captured time as an ISO-8601 instant ending in 'Z' so triage doesn't have
        // to guess the timezone.
        StringAssert.Contains(body, "2026-04-24T22:30:15Z");
    }

    [TestMethod]
    public void Build_EscapesPipesInCellValues()
    {
        // Auto-triage and humans both read the table; an unescaped pipe would split the
        // cell and corrupt every following row.
        var body = FeedbackBodyFormatter.Build("Bug", "x",
            SampleContext(activeTaskId: "task-foo|bar|baz"));

        StringAssert.Contains(body, "task-foo\\|bar\\|baz");
        Assert.IsFalse(body.Contains("task-foo|bar"),
            "Raw unescaped pipe leaked into table cell.");
    }

    [TestMethod]
    public void Build_CollapsesNewlinesInCellValues()
    {
        // A literal newline in a cell breaks the GFM table; the formatter must flatten them.
        var body = FeedbackBodyFormatter.Build("Bug", "x",
            SampleContext(osDescription: "Line one\r\nLine two\nLine three"));

        StringAssert.Contains(body, "Line one  Line two Line three");
        // No raw \n inside the OS row — find the row and check it's a single line.
        var osLineStart = body.IndexOf("| OS |", StringComparison.Ordinal);
        var osLineEnd = body.IndexOf('\n', osLineStart);
        var osRow = body.Substring(osLineStart, osLineEnd - osLineStart);
        Assert.IsFalse(osRow.Contains('\r') || osRow.Contains('\n'),
            $"OS row still contains raw newlines: '{osRow}'");
    }

    [TestMethod]
    public void Build_NormalizesNonUtcTimestampToUtc()
    {
        // 10:30:15 in -07:00 == 17:30:15 UTC. The Captured row must reflect UTC, not local.
        var ctx = new FeedbackContext(
            PageName: "MyDayPage",
            ActiveTaskId: "task-x",
            AppVersion: "1.0.0",
            OsDescription: "Windows",
            RuntimeVersion: ".NET 10",
            CapturedAtUtc: new DateTimeOffset(2026, 4, 24, 10, 30, 15, TimeSpan.FromHours(-7)));

        var body = FeedbackBodyFormatter.Build("Bug", "x", ctx);

        StringAssert.Contains(body, "2026-04-24T17:30:15Z");
        Assert.IsFalse(body.Contains("2026-04-24T10:30:15Z"),
            "Timestamp was not converted to UTC before rendering.");
    }

    [TestMethod]
    public void Build_HeaderAppearsBeforeContextBeforeUserBody()
    {
        var body = FeedbackBodyFormatter.Build("Bug", "USER_BODY_MARKER", SampleContext());

        var headerIdx = body.IndexOf("_Filed from Glasswork feedback dialog", StringComparison.Ordinal);
        var contextIdx = body.IndexOf("## Context", StringComparison.Ordinal);
        var userIdx = body.IndexOf("USER_BODY_MARKER", StringComparison.Ordinal);

        Assert.IsTrue(headerIdx >= 0 && contextIdx > headerIdx && userIdx > contextIdx,
            $"Expected header < context < user body. Got header={headerIdx} context={contextIdx} user={userIdx}.\nBody:\n{body}");
    }
}
