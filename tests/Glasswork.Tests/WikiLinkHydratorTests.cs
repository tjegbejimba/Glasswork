using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class WikiLinkHydratorTests
{
    private string _wikiRoot = string.Empty;

    [TestInitialize]
    public void Init()
    {
        _wikiRoot = Path.Combine(Path.GetTempPath(), "glasswork-hydrator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wikiRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_wikiRoot)) Directory.Delete(_wikiRoot, recursive: true);
    }

    private void WritePage(string relativePath, string frontmatter)
    {
        var full = Path.Combine(_wikiRoot, relativePath.Replace('/', Path.DirectorySeparatorChar) + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"---\n{frontmatter}\n---\n\nbody\n");
    }

    [TestMethod]
    public void Hydrate_ResolvesTitleTypeAndCreated_FromTargetFrontmatter()
    {
        WritePage("decisions/glasswork-v2-prd",
            "id: glasswork-v2-prd\ntitle: Glasswork V2 PRD\ntype: decision\ncreated: 2026-04-17");

        var hydrator = new WikiLinkHydrator();
        var input = new[] { new RelatedLink { Slug = "decisions/glasswork-v2-prd" } };

        var result = hydrator.Hydrate(input, _wikiRoot);

        Assert.AreEqual(1, result.Count);
        var h = result[0];
        Assert.IsFalse(h.IsMissing);
        Assert.AreEqual("Glasswork V2 PRD", h.Title);
        Assert.AreEqual("decision", h.Type);
        Assert.AreEqual(new DateTime(2026, 4, 17), h.Created);
        Assert.AreEqual("decisions/glasswork-v2-prd", h.Slug);
    }

    [TestMethod]
    public void Hydrate_DisplayNameOverride_PreservedOnHydratedView()
    {
        WritePage("contacts/jane",
            "id: jane\ntitle: Jane Doe (full)\ntype: contact\ncreated: 2026-01-01");

        var hydrator = new WikiLinkHydrator();
        var input = new[] { new RelatedLink { Slug = "contacts/jane", DisplayName = "Jane" } };

        var result = hydrator.Hydrate(input, _wikiRoot);

        Assert.AreEqual("Jane", result[0].DisplayName);
        Assert.AreEqual("Jane Doe (full)", result[0].Title);
    }

    [TestMethod]
    public void Hydrate_MissingFile_MarksIsMissingAndUsesSlugFallback()
    {
        var hydrator = new WikiLinkHydrator();
        var input = new[] { new RelatedLink { Slug = "decisions/does-not-exist" } };

        var result = hydrator.Hydrate(input, _wikiRoot);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].IsMissing);
        Assert.AreEqual("does-not-exist", result[0].Title);
        Assert.IsNull(result[0].Created);
        Assert.AreEqual("Missing", result[0].Subtitle);
    }

    [TestMethod]
    public void Hydrate_MissingTitleInFrontmatter_FallsBackToSlugSegment()
    {
        WritePage("notes/orphan", "id: orphan\ntype: note\ncreated: 2026-02-02");

        var hydrator = new WikiLinkHydrator();
        var input = new[] { new RelatedLink { Slug = "notes/orphan" } };

        var result = hydrator.Hydrate(input, _wikiRoot);

        Assert.IsFalse(result[0].IsMissing);
        Assert.AreEqual("orphan", result[0].Title);
        Assert.AreEqual("note", result[0].Type);
    }

    [TestMethod]
    public void Hydrate_FileWithoutFrontmatter_DoesNotThrow_AndUsesSlugFallback()
    {
        var full = Path.Combine(_wikiRoot, "notes", "raw.md");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "Just plain markdown, no frontmatter.");

        var hydrator = new WikiLinkHydrator();
        var result = hydrator.Hydrate(new[] { new RelatedLink { Slug = "notes/raw" } }, _wikiRoot);

        Assert.AreEqual(1, result.Count);
        Assert.IsFalse(result[0].IsMissing);
        Assert.AreEqual("raw", result[0].Title);
    }

    [TestMethod]
    public void Hydrate_EmptyInput_ReturnsEmptyList()
    {
        var hydrator = new WikiLinkHydrator();
        var result = hydrator.Hydrate(System.Array.Empty<RelatedLink>(), _wikiRoot);
        Assert.AreEqual(0, result.Count);
    }
}
