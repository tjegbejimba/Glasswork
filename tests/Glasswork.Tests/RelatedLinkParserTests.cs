using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class RelatedLinkParserTests
{
    private readonly FrontmatterParser _parser = new();

    private const string Header = """
        ---
        id: t
        title: T
        created: 2026-04-17
        ---

        Body prose.

        """;

    [TestMethod]
    public void Parse_NoRelatedSection_ReturnsEmptyList()
    {
        var task = _parser.Parse(Header);
        Assert.AreEqual(0, task.RelatedLinks.Count);
    }

    [TestMethod]
    public void Parse_RelatedSection_BulletList_ParsesEachLink()
    {
        var md = Header + """
            ## Related

            - [[decisions/glasswork-v2-prd]]
            - [[contacts/jane-doe|Jane Doe]]
            - [[notes/loose-thought]]
            """;

        var task = _parser.Parse(md);

        Assert.AreEqual(3, task.RelatedLinks.Count);
        Assert.AreEqual("decisions/glasswork-v2-prd", task.RelatedLinks[0].Slug);
        Assert.IsNull(task.RelatedLinks[0].DisplayName);
        Assert.AreEqual("contacts/jane-doe", task.RelatedLinks[1].Slug);
        Assert.AreEqual("Jane Doe", task.RelatedLinks[1].DisplayName);
        Assert.AreEqual("notes/loose-thought", task.RelatedLinks[2].Slug);
    }

    [TestMethod]
    public void Parse_RelatedSection_BarePlainLines_ParsesEachLink()
    {
        var md = Header + """
            ## Related

            [[decisions/foo]]
            [[notes/bar|Bar Note]]
            """;

        var task = _parser.Parse(md);

        Assert.AreEqual(2, task.RelatedLinks.Count);
        Assert.AreEqual("decisions/foo", task.RelatedLinks[0].Slug);
        Assert.AreEqual("notes/bar", task.RelatedLinks[1].Slug);
        Assert.AreEqual("Bar Note", task.RelatedLinks[1].DisplayName);
    }

    [TestMethod]
    public void Parse_RelatedSection_TrimsWhitespace_AndIgnoresPlainText()
    {
        var md = Header + """
            ## Related

            Some agent commentary here, no link.
            -   [[decisions/foo]]   
            - some bullet without a link
            """;

        var task = _parser.Parse(md);

        Assert.AreEqual(1, task.RelatedLinks.Count);
        Assert.AreEqual("decisions/foo", task.RelatedLinks[0].Slug);
    }

    [TestMethod]
    public void Parse_RelatedSection_StopsAtNextHeading()
    {
        var md = Header + """
            ## Related

            - [[decisions/foo]]

            ## Notes

            - [[should/not/be/parsed]]
            """;

        var task = _parser.Parse(md);

        Assert.AreEqual(1, task.RelatedLinks.Count);
        Assert.AreEqual("decisions/foo", task.RelatedLinks[0].Slug);
    }

    [TestMethod]
    public void Parse_RelatedSection_PreservesSectionInBody_NotStrippedLikeSubtasks()
    {
        // Related is left in the body so Obsidian's graph view still works (D10).
        // Unlike Subtasks, we don't strip it from Body.
        var md = Header + """
            ## Related

            - [[decisions/foo]]
            """;

        var task = _parser.Parse(md);

        Assert.IsTrue(task.Description.Contains("## Related"), "Related section should remain in Description.");
        Assert.IsTrue(task.Description.Contains("[[decisions/foo]]"), "Related links should remain in Description.");
    }

    [TestMethod]
    public void RelatedLink_FallbackDisplay_UsesLastSegmentOfSlug()
    {
        var l = new RelatedLink { Slug = "decisions/glasswork-v2-prd" };
        Assert.AreEqual("glasswork-v2-prd", l.FallbackDisplay);

        var l2 = new RelatedLink { Slug = "flat" };
        Assert.AreEqual("flat", l2.FallbackDisplay);

        var l3 = new RelatedLink { Slug = "x/y", DisplayName = "Pretty Name" };
        Assert.AreEqual("Pretty Name", l3.FallbackDisplay);
    }

    [TestMethod]
    public void Serialize_EmitsRelatedSection_AfterSubtasks()
    {
        var task = new GlassworkTask
        {
            Id = "t",
            Title = "T",
            Created = new DateTime(2026, 4, 17),
            Description = "Some prose.",
            RelatedLinks =
            {
                new RelatedLink { Slug = "decisions/foo" },
                new RelatedLink { Slug = "contacts/jane", DisplayName = "Jane" },
            },
        };

        var md = _parser.Serialize(task);

        StringAssert.Contains(md, "## Related");
        StringAssert.Contains(md, "- [[decisions/foo]]");
        StringAssert.Contains(md, "- [[contacts/jane|Jane]]");
    }

    [TestMethod]
    public void Roundtrip_ParseSerializeParse_PreservesRelatedLinks()
    {
        var original = Header + """
            ## Related

            - [[decisions/foo]]
            - [[contacts/jane|Jane Doe]]
            """;

        var parsed = _parser.Parse(original);
        var reSerialized = _parser.Serialize(parsed);
        var reParsed = _parser.Parse(reSerialized);

        Assert.AreEqual(2, reParsed.RelatedLinks.Count);
        Assert.AreEqual("decisions/foo", reParsed.RelatedLinks[0].Slug);
        Assert.AreEqual("contacts/jane", reParsed.RelatedLinks[1].Slug);
        Assert.AreEqual("Jane Doe", reParsed.RelatedLinks[1].DisplayName);
    }
}
