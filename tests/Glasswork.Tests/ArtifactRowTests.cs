using System;
using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactRowTests
{
    private static Artifact MakeArtifact(string title, DateTime mtimeUtc)
        => new(Path: $"C:\\fake\\{title}.md", Title: title, ModifiedUtc: mtimeUtc, Body: "body");

    [TestMethod]
    public void Project_Empty_ReturnsEmpty()
    {
        var rows = ArtifactRow.Project([], DateTime.UtcNow);
        Assert.IsEmpty(rows);
    }

    [TestMethod]
    public void Project_NewestRow_IsExpandedByDefault()
    {
        var now = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<Artifact>
        {
            MakeArtifact("oldest", now.AddDays(-3)),
            MakeArtifact("middle", now.AddHours(-2)),
            MakeArtifact("newest", now.AddMinutes(-5)),
        };

        var rows = ArtifactRow.Project(artifacts, now);

        Assert.HasCount(3, rows);
        Assert.IsFalse(rows[0].IsExpanded);
        Assert.IsFalse(rows[1].IsExpanded);
        Assert.IsTrue(rows[2].IsExpanded, "newest should auto-expand");
    }

    [TestMethod]
    public void Project_TimeBadge_UsesRelativeStrings()
    {
        var now = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<Artifact>
        {
            MakeArtifact("seconds", now.AddSeconds(-30)),
            MakeArtifact("minutes", now.AddMinutes(-10)),
            MakeArtifact("hours", now.AddHours(-3)),
            MakeArtifact("days", now.AddDays(-2)),
        };

        var rows = ArtifactRow.Project(artifacts, now);

        Assert.AreEqual("just now", rows[0].TimeBadge);
        Assert.AreEqual("10m ago", rows[1].TimeBadge);
        Assert.AreEqual("3h ago", rows[2].TimeBadge);
        Assert.AreEqual("2d ago", rows[3].TimeBadge);
    }

    [TestMethod]
    public void Project_PreservesArtifactReference()
    {
        var now = DateTime.UtcNow;
        var a = MakeArtifact("plan", now);
        var rows = ArtifactRow.Project([a], now);
        Assert.AreSame(a, rows[0].Artifact);
        Assert.AreEqual("plan", rows[0].Title);
        Assert.AreEqual("body", rows[0].Body);
    }
}
