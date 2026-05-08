using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class LinkRowTests
{
    [TestMethod]
    public void Project_ReturnsCorrectDisplayTextForEachType()
    {
        // Arrange
        var links = new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Ado, Value = "1234" },
            new() { Type = TaskLink.Types.Pr, Value = "https://github.com/org/repo/pull/42" },
            new() { Type = TaskLink.Types.Incident, Value = "965114" },
            new() { Type = TaskLink.Types.Doc, Value = "https://eng.ms/docs/products" },
            new() { Type = TaskLink.Types.Build, Value = "https://dev.azure.com/build/123" },
            new() { Type = TaskLink.Types.Other, Value = "https://example.com", Label = "Custom Link" }
        };

        // Act
        var rows = LinkRow.Project(links);

        // Assert
        Assert.AreEqual(6, rows.Count);
        Assert.AreEqual("ADO #1234", rows[0].DisplayText);
        Assert.AreEqual("PR (github.com)", rows[1].DisplayText);
        Assert.AreEqual("ICM 965114", rows[2].DisplayText);
        Assert.AreEqual("Doc (eng.ms)", rows[3].DisplayText);
        Assert.AreEqual("Build (dev.azure.com)", rows[4].DisplayText);
        Assert.AreEqual("Custom Link", rows[5].DisplayText); // Uses label when present
    }

    [TestMethod]
    public void Project_PreservesSourceLink()
    {
        // Arrange
        var link = new TaskLink { Type = TaskLink.Types.Ado, Value = "1234" };

        // Act
        var rows = LinkRow.Project(new[] { link });

        // Assert
        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(link, rows[0].Source);
    }

    [TestMethod]
    public void Project_HandlesEmptyList()
    {
        // Act
        var rows = LinkRow.Project(Array.Empty<TaskLink>());

        // Assert
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void TypeBadgeText_ReturnsUppercaseType()
    {
        // Arrange
        var links = new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Ado, Value = "1" },
            new() { Type = TaskLink.Types.Pr, Value = "1" },
            new() { Type = TaskLink.Types.Incident, Value = "1" },
            new() { Type = TaskLink.Types.Doc, Value = "http://a.com" },
            new() { Type = TaskLink.Types.Build, Value = "http://a.com" },
            new() { Type = TaskLink.Types.Other, Value = "http://a.com" }
        };

        // Act
        var rows = LinkRow.Project(links);

        // Assert
        Assert.AreEqual("ADO", rows[0].TypeBadgeText);
        Assert.AreEqual("PR", rows[1].TypeBadgeText);
        Assert.AreEqual("ICM", rows[2].TypeBadgeText);
        Assert.AreEqual("DOC", rows[3].TypeBadgeText);
        Assert.AreEqual("BUILD", rows[4].TypeBadgeText);
        Assert.AreEqual("OTHER", rows[5].TypeBadgeText);
    }

    [TestMethod]
    public void TypeBadgeColor_ReturnsCorrectHexForEachType()
    {
        // Arrange
        var links = new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Ado, Value = "1" },
            new() { Type = TaskLink.Types.Pr, Value = "1" },
            new() { Type = TaskLink.Types.Incident, Value = "1" },
            new() { Type = TaskLink.Types.Doc, Value = "http://a.com" },
            new() { Type = TaskLink.Types.Build, Value = "http://a.com" },
            new() { Type = TaskLink.Types.Other, Value = "http://a.com" }
        };

        // Act
        var rows = LinkRow.Project(links);

        // Assert
        Assert.AreEqual("#0F6CBD", rows[0].TypeBadgeColor); // Blue
        Assert.AreEqual("#8764B8", rows[1].TypeBadgeColor); // Purple
        Assert.AreEqual("#C50F1F", rows[2].TypeBadgeColor); // Red
        Assert.AreEqual("#107C10", rows[3].TypeBadgeColor); // Green
        Assert.AreEqual("#F7630C", rows[4].TypeBadgeColor); // Orange
        Assert.AreEqual("#8A8886", rows[5].TypeBadgeColor); // Grey
    }
}
