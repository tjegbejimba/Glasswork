using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class GlassworkUriParserTests
{
    // ── Parse: task ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_TaskUri_ReturnsTaskWithId()
    {
        var result = GlassworkUriParser.Parse("glasswork://task/TASK-1");
        Assert.IsInstanceOfType<GlassworkUri.Task>(result);
        Assert.AreEqual("TASK-1", ((GlassworkUri.Task)result!).TaskId);
    }

    [TestMethod]
    public void Parse_TaskUri_CaseInsensitiveScheme()
    {
        var result = GlassworkUriParser.Parse("GLASSWORK://task/TASK-1");
        Assert.IsInstanceOfType<GlassworkUri.Task>(result);
    }

    [TestMethod]
    public void Parse_TaskUri_DecodesPercentEncodedId()
    {
        var result = GlassworkUriParser.Parse("glasswork://task/TASK%2D1");
        Assert.IsInstanceOfType<GlassworkUri.Task>(result);
        Assert.AreEqual("TASK-1", ((GlassworkUri.Task)result!).TaskId);
    }

    [TestMethod]
    public void Parse_TaskUri_NoId_ReturnsNull()
    {
        Assert.IsNull(GlassworkUriParser.Parse("glasswork://task/"));
        Assert.IsNull(GlassworkUriParser.Parse("glasswork://task"));
    }

    // ── Parse: my-day ────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_MyDayUri_ReturnsMyDay()
    {
        var result = GlassworkUriParser.Parse("glasswork://my-day");
        Assert.IsInstanceOfType<GlassworkUri.MyDay>(result);
    }

    [TestMethod]
    public void Parse_MyDayUri_WithTrailingSlash_ReturnsMyDay()
    {
        var result = GlassworkUriParser.Parse("glasswork://my-day/");
        Assert.IsInstanceOfType<GlassworkUri.MyDay>(result);
    }

    // ── Parse: backlog ───────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_BacklogUri_ReturnsBacklog()
    {
        var result = GlassworkUriParser.Parse("glasswork://backlog");
        Assert.IsInstanceOfType<GlassworkUri.Backlog>(result);
    }

    // ── Parse: invalid / unknown ─────────────────────────────────────────────

    [TestMethod]
    public void Parse_WrongScheme_ReturnsNull()
    {
        Assert.IsNull(GlassworkUriParser.Parse("obsidian://task/TASK-1"));
        Assert.IsNull(GlassworkUriParser.Parse("https://task/TASK-1"));
    }

    [TestMethod]
    public void Parse_UnknownHost_ReturnsNull()
    {
        Assert.IsNull(GlassworkUriParser.Parse("glasswork://settings"));
        Assert.IsNull(GlassworkUriParser.Parse("glasswork://unknown/path"));
    }

    [TestMethod]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(GlassworkUriParser.Parse(null));
        Assert.IsNull(GlassworkUriParser.Parse(""));
        Assert.IsNull(GlassworkUriParser.Parse("   "));
    }

    [TestMethod]
    public void Parse_NotAUri_ReturnsNull()
    {
        Assert.IsNull(GlassworkUriParser.Parse("not a uri at all"));
    }

    // ── Build ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Build_Task_ProducesExpectedUri()
    {
        var uri = GlassworkUriParser.Build(new GlassworkUri.Task("TASK-1"));
        Assert.AreEqual("glasswork://task/TASK-1", uri);
    }

    [TestMethod]
    public void Build_Task_EncodesSpecialChars()
    {
        var uri = GlassworkUriParser.Build(new GlassworkUri.Task("my task/1"));
        Assert.AreEqual("glasswork://task/my%20task%2F1", uri);
    }

    [TestMethod]
    public void Build_MyDay_ProducesExpectedUri()
    {
        var uri = GlassworkUriParser.Build(new GlassworkUri.MyDay());
        Assert.AreEqual("glasswork://my-day", uri);
    }

    [TestMethod]
    public void Build_Backlog_ProducesExpectedUri()
    {
        var uri = GlassworkUriParser.Build(new GlassworkUri.Backlog());
        Assert.AreEqual("glasswork://backlog", uri);
    }

    // ── Round-trips ───────────────────────────────────────────────────────────

    [TestMethod]
    public void RoundTrip_Task()
    {
        var original = new GlassworkUri.Task("MY-TASK-42");
        var parsed = GlassworkUriParser.Parse(GlassworkUriParser.Build(original));
        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void RoundTrip_MyDay()
    {
        var original = new GlassworkUri.MyDay();
        var parsed = GlassworkUriParser.Parse(GlassworkUriParser.Build(original));
        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void RoundTrip_Backlog()
    {
        var original = new GlassworkUri.Backlog();
        var parsed = GlassworkUriParser.Parse(GlassworkUriParser.Build(original));
        Assert.AreEqual(original, parsed);
    }
}
