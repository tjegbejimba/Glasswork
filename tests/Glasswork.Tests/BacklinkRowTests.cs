using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class BacklinkRowTests
{
    [TestMethod]
    public void Project_PreservesIndexOrder()
    {
        // Index already sorts by (PageType, Title, Path); projection must not reorder.
        var input = new[]
        {
            new Backlink(@"C:\v\wiki\concepts\a.md", "A", BacklinkPageType.Concept, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\concepts\b.md", "B", BacklinkPageType.Concept, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\decisions\d.md", "D", BacklinkPageType.Decision, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\notes\n.md", "N", BacklinkPageType.Other, DateTime.UtcNow),
        };

        var rows = BacklinkRow.Project(input);

        CollectionAssert.AreEqual(
            new[] { "A", "B", "D", "N" },
            rows.Select(r => r.Title).ToArray());
    }

    [TestMethod]
    public void Project_AssignsHumanReadableTypeLabel()
    {
        var input = new[]
        {
            new Backlink(@"C:\v\wiki\concepts\a.md", "C", BacklinkPageType.Concept, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\decisions\d.md", "D", BacklinkPageType.Decision, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\incidents\i.md", "I", BacklinkPageType.Incident, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\systems\s.md", "S", BacklinkPageType.System, DateTime.UtcNow),
            new Backlink(@"C:\v\wiki\misc\m.md", "M", BacklinkPageType.Other, DateTime.UtcNow),
        };

        var rows = BacklinkRow.Project(input);

        CollectionAssert.AreEqual(
            new[] { "concept", "decision", "incident", "system", "other" },
            rows.Select(r => r.TypeLabel).ToArray());
    }

    [TestMethod]
    public void Project_EmptyInput_ReturnsEmptyList()
    {
        var rows = BacklinkRow.Project(Array.Empty<Backlink>());
        Assert.AreEqual(0, rows.Count);
    }
}
