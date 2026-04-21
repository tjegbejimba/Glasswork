using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AdoWorkItemTitleParserTests
{
    [TestMethod]
    public void Null_ReturnsNull() => Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(null));

    [TestMethod]
    public void Empty_ReturnsNull() => Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(""));

    [TestMethod]
    public void NotJson_ReturnsNull() => Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle("not json at all"));

    [TestMethod]
    public void ValidAzOutput_ReturnsTitle()
    {
        const string json = """
        {
          "id": 12345,
          "fields": {
            "System.Title": "Fix Redis deployment pipeline",
            "System.State": "Active"
          }
        }
        """;
        Assert.AreEqual("Fix Redis deployment pipeline", AdoWorkItemTitleParser.TryParseTitle(json));
    }

    [TestMethod]
    public void TitleFieldMissing_ReturnsNull()
    {
        const string json = """{ "id": 1, "fields": { "System.State": "Active" } }""";
        Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(json));
    }

    [TestMethod]
    public void FieldsMissing_ReturnsNull()
    {
        Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle("""{ "id": 1 }"""));
    }

    [TestMethod]
    public void TitleEmpty_ReturnsNull()
    {
        const string json = """{ "fields": { "System.Title": "" } }""";
        Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(json));
    }

    [TestMethod]
    public void TitleWhitespace_ReturnsNull()
    {
        const string json = """{ "fields": { "System.Title": "   " } }""";
        Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(json));
    }

    [TestMethod]
    public void TitleIsTrimmed()
    {
        const string json = """{ "fields": { "System.Title": "  PBI title  " } }""";
        Assert.AreEqual("PBI title", AdoWorkItemTitleParser.TryParseTitle(json));
    }

    [TestMethod]
    public void LeadingWarningLines_AreStripped()
    {
        const string contaminated = """
        WARNING: You have 2 update(s) available.
        WARNING: Consider updating your CLI.
        { "fields": { "System.Title": "Real title" } }
        """;
        Assert.AreEqual("Real title", AdoWorkItemTitleParser.TryParseTitle(contaminated));
    }

    [TestMethod]
    public void TitleNotString_ReturnsNull()
    {
        const string json = """{ "fields": { "System.Title": 42 } }""";
        Assert.IsNull(AdoWorkItemTitleParser.TryParseTitle(json));
    }
}
