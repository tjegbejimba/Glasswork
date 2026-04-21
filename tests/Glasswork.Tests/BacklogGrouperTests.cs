using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class BacklogGrouperTests
{
    private static GlassworkTask Task(string id, string? parent = null) =>
        new() { Id = id, Title = id, Parent = parent };

    [TestMethod]
    public void EmptyInput_ReturnsEmpty()
    {
        var rows = BacklogGrouper.Group([]);
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void AllParentless_ReturnsTasksFlat_NoHeaders()
    {
        var rows = BacklogGrouper.Group([Task("a"), Task("b"), Task("c")]);

        Assert.AreEqual(3, rows.Count);
        Assert.IsFalse(rows.Any(r => r is BacklogParentGroupHeader));
        CollectionAssert.AreEqual(
            new[] { "a", "b", "c" },
            rows.Cast<GlassworkTask>().Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void SingleGroup_EmitsOneHeaderPlusTasks()
    {
        var rows = BacklogGrouper.Group([
            Task("a", "PBI 1"),
            Task("b", "PBI 1"),
        ]);

        Assert.AreEqual(3, rows.Count);
        Assert.IsInstanceOfType(rows[0], typeof(BacklogParentGroupHeader));
        var header = (BacklogParentGroupHeader)rows[0];
        Assert.AreEqual("PBI 1", header.DisplayHeader);
        Assert.AreEqual(2, header.TotalCount);
        Assert.IsFalse(header.IsCollapsed);
    }

    [TestMethod]
    public void MixedInput_ParentlessFirst_ThenAlphabeticalGroups()
    {
        var rows = BacklogGrouper.Group([
            Task("orphan1"),
            Task("zeta-task", "Zeta"),
            Task("alpha-task", "Alpha"),
            Task("orphan2"),
            Task("middle-task", "Middle"),
        ]);

        // Expected: orphan1, orphan2, [Alpha header], alpha-task, [Middle header], middle-task, [Zeta header], zeta-task
        Assert.AreEqual(8, rows.Count);
        Assert.AreEqual("orphan1", ((GlassworkTask)rows[0]).Id);
        Assert.AreEqual("orphan2", ((GlassworkTask)rows[1]).Id);
        Assert.AreEqual("Alpha", ((BacklogParentGroupHeader)rows[2]).DisplayHeader);
        Assert.AreEqual("alpha-task", ((GlassworkTask)rows[3]).Id);
        Assert.AreEqual("Middle", ((BacklogParentGroupHeader)rows[4]).DisplayHeader);
        Assert.AreEqual("middle-task", ((GlassworkTask)rows[5]).Id);
        Assert.AreEqual("Zeta", ((BacklogParentGroupHeader)rows[6]).DisplayHeader);
        Assert.AreEqual("zeta-task", ((GlassworkTask)rows[7]).Id);
    }

    [TestMethod]
    public void Grouping_IsCaseInsensitive()
    {
        var rows = BacklogGrouper.Group([
            Task("a", "PBI"),
            Task("b", "pbi"),
            Task("c", "PbI"),
        ]);

        var headers = rows.OfType<BacklogParentGroupHeader>().ToList();
        Assert.AreEqual(1, headers.Count);
        Assert.AreEqual(3, headers[0].TotalCount);
    }

    [TestMethod]
    public void Display_PreservesFirstEncounteredCasing()
    {
        var rows = BacklogGrouper.Group([
            Task("a", "MyParent"),
            Task("b", "myparent"),
        ]);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("MyParent", header.DisplayHeader);
    }

    [TestMethod]
    public void Grouping_TrimsWhitespace()
    {
        var rows = BacklogGrouper.Group([
            Task("a", "  PBI  "),
            Task("b", "PBI"),
        ]);

        var headers = rows.OfType<BacklogParentGroupHeader>().ToList();
        Assert.AreEqual(1, headers.Count);
        Assert.AreEqual("PBI", headers[0].DisplayHeader);
        Assert.AreEqual(2, headers[0].TotalCount);
    }

    [TestMethod]
    public void EmptyStringParent_TreatedAsParentless()
    {
        var rows = BacklogGrouper.Group([
            Task("a", ""),
            Task("b", "   "),
            Task("c", null),
        ]);

        Assert.AreEqual(3, rows.Count);
        Assert.IsFalse(rows.Any(r => r is BacklogParentGroupHeader));
    }

    [TestMethod]
    public void CollapsedGroup_OmitsTasks_KeepsHeader()
    {
        var collapseState = new Dictionary<string, bool> { ["pbi 1"] = true };

        var rows = BacklogGrouper.Group([
            Task("a", "PBI 1"),
            Task("b", "PBI 1"),
            Task("c", "PBI 2"),
        ], collapseState);

        // Expected: [PBI 1 header (collapsed)], [PBI 2 header], c
        Assert.AreEqual(3, rows.Count);
        var pbi1 = (BacklogParentGroupHeader)rows[0];
        Assert.AreEqual("PBI 1", pbi1.DisplayHeader);
        Assert.IsTrue(pbi1.IsCollapsed);
        Assert.AreEqual(2, pbi1.TotalCount);

        var pbi2 = (BacklogParentGroupHeader)rows[1];
        Assert.AreEqual("PBI 2", pbi2.DisplayHeader);
        Assert.IsFalse(pbi2.IsCollapsed);
        Assert.AreEqual("c", ((GlassworkTask)rows[2]).Id);
    }

    [TestMethod]
    public void SingleTaskGroup_StillEmitsHeader()
    {
        var rows = BacklogGrouper.Group([Task("a", "Lonely")]);

        Assert.AreEqual(2, rows.Count);
        var header = (BacklogParentGroupHeader)rows[0];
        Assert.AreEqual("Lonely", header.DisplayHeader);
        Assert.AreEqual(1, header.TotalCount);
        Assert.AreEqual("a", ((GlassworkTask)rows[1]).Id);
    }

    [TestMethod]
    public void TaskOrderWithinGroup_PreservesInputOrder()
    {
        // Caller is responsible for sorting; we don't reorder.
        var rows = BacklogGrouper.Group([
            Task("z-task", "P"),
            Task("a-task", "P"),
            Task("m-task", "P"),
        ]);

        Assert.AreEqual(4, rows.Count);
        CollectionAssert.AreEqual(
            new[] { "z-task", "a-task", "m-task" },
            rows.OfType<GlassworkTask>().Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void GroupOrder_AlphabeticalByNormalizedKey()
    {
        // Display labels with mixed casing should still order by lowercase key.
        var rows = BacklogGrouper.Group([
            Task("a", "Beta"),
            Task("b", "alpha"),
            Task("c", "GAMMA"),
        ]);

        var headers = rows.OfType<BacklogParentGroupHeader>().Select(h => h.DisplayHeader).ToList();
        CollectionAssert.AreEqual(new[] { "alpha", "Beta", "GAMMA" }, headers);
    }

    [TestMethod]
    public void HeaderAdoUrl_NullByDefault_WhenNoBaseUrl()
    {
        var rows = BacklogGrouper.Group([Task("a", "12345"), Task("b", "PBI X")]);
        foreach (var h in rows.OfType<BacklogParentGroupHeader>())
        {
            Assert.IsNull(h.AdoUrl);
        }
    }

    [TestMethod]
    public void HeaderAdoUrl_PopulatedFromNumericParentAndBaseUrl()
    {
        var rows = BacklogGrouper.Group(
            [Task("a", "12345"), Task("b", "PBI X")],
            collapseState: null,
            adoBaseUrl: "https://dev.azure.com/org/proj");

        var numericHeader = rows.OfType<BacklogParentGroupHeader>()
            .Single(h => h.DisplayHeader == "12345");
        Assert.AreEqual("https://dev.azure.com/org/proj/_workitems/edit/12345", numericHeader.AdoUrl);

        var nonNumericHeader = rows.OfType<BacklogParentGroupHeader>()
            .Single(h => h.DisplayHeader == "PBI X");
        Assert.IsNull(nonNumericHeader.AdoUrl);
    }

    [TestMethod]
    public void HeaderAdoUrl_PopulatedFromUrlParent_NoBaseUrlNeeded()
    {
        var rows = BacklogGrouper.Group(
            [Task("a", "https://example.com/work/1")]);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("https://example.com/work/1", header.AdoUrl);
    }

    // ─── parent title resolver ─────────────────────────────────────────────
    // The resolver is an opt-in callback: given a raw parent string, returns
    // the resolved work-item title or null. Used to render nicer headers like
    // "#37226063 — Fix Redis pipeline" instead of bare IDs.

    [TestMethod]
    public void ParentTitleResolver_Numeric_EnrichesDisplayHeader()
    {
        Func<string, string?> resolver = p => p == "12345" ? "Fix Redis pipeline" : null;

        var rows = BacklogGrouper.Group(
            [Task("a", "12345")],
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("#12345 — Fix Redis pipeline", header.DisplayHeader);
    }

    [TestMethod]
    public void ParentTitleResolver_NonNumeric_StillEnrichedAsSuffix()
    {
        Func<string, string?> resolver = p => "Resolved title";

        var rows = BacklogGrouper.Group(
            [Task("a", "PBI X")],
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("PBI X — Resolved title", header.DisplayHeader);
    }

    [TestMethod]
    public void ParentTitleResolver_ReturnsNull_DisplayHeaderNormalizedToHashId()
    {
        // Even without an enriched title, numeric and URL-form parents are normalized
        // to "#{id}" so headers don't show ugly raw URLs.
        Func<string, string?> resolver = _ => null;

        var rows = BacklogGrouper.Group(
            [Task("a", "12345")],
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("#12345", header.DisplayHeader);
    }

    [TestMethod]
    public void ParentTitleResolver_ReturnsWhitespace_TreatedAsNull()
    {
        Func<string, string?> resolver = _ => "   ";

        var rows = BacklogGrouper.Group(
            [Task("a", "12345")],
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("#12345", header.DisplayHeader);
    }

    [TestMethod]
    public void ParentTitleResolver_UrlFormParent_NormalizedToHashId()
    {
        // URL-form parents (which the EditParent dialog accepts) are collapsed
        // down to "#{id}" in the header to avoid showing the full URL.
        Func<string, string?> resolver = _ => "Fix the thing";

        var rows = BacklogGrouper.Group(
            [Task("a", "https://dev.azure.com/org/proj/_workitems/edit/12345")],
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("#12345 — Fix the thing", header.DisplayHeader);
    }

    [TestMethod]
    public void ParentTitleResolver_GroupingKeyUnchanged_ByEnrichment()
    {
        // Two tasks under the same numeric parent; resolver enriches the displayed
        // header but the grouping key remains the lowered raw parent so collapse
        // state and equality continue to work.
        var collapseState = new Dictionary<string, bool> { ["12345"] = true };
        Func<string, string?> resolver = _ => "Some title";

        var rows = BacklogGrouper.Group(
            [Task("a", "12345"), Task("b", "12345")],
            collapseState: collapseState,
            parentTitleResolver: resolver);

        var header = rows.OfType<BacklogParentGroupHeader>().Single();
        Assert.AreEqual("#12345 — Some title", header.DisplayHeader);
        Assert.AreEqual("12345", header.Key);
        Assert.IsTrue(header.IsCollapsed);
        Assert.AreEqual(2, header.TotalCount);
    }
}
