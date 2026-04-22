using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class BacklinkIndexTests
{
    private string _vaultRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-backlinks-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot)) Directory.Delete(_vaultRoot, recursive: true);
    }

    private void SeedTask(string id) =>
        File.WriteAllText(
            Path.Combine(_vaultRoot, "wiki", "todo", id + ".md"),
            $"---\nid: {id}\ntitle: {id}\n---\n\nbody\n");

    private string SeedWikiPage(string relativeFolder, string fileName, string body)
    {
        var folder = Path.Combine(_vaultRoot, "wiki", relativeFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    [TestMethod]
    public void GetBacklinks_EmptyVault_ReturnsEmpty()
    {
        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.IsEmpty(index.GetBacklinks("anything"));
    }

    [TestMethod]
    public void GetBacklinks_NoLinkingPages_ReturnsEmpty()
    {
        SeedTask("task-a");
        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.IsEmpty(index.GetBacklinks("task-a"));
    }

    [TestMethod]
    public void GetBacklinks_SingleLinkingPage_SurfacesOneEntryForRightTask()
    {
        SeedTask("task-a");
        SeedTask("task-b");
        SeedWikiPage("concepts", "alpha.md", "# Alpha\n\nMentions [[task-a]] once.\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        var aLinks = index.GetBacklinks("task-a");
        Assert.HasCount(1, aLinks);
        Assert.AreEqual("Alpha", aLinks[0].LinkingPageTitle);
        Assert.IsEmpty(index.GetBacklinks("task-b"));
    }

    [TestMethod]
    public void GetBacklinks_PageMentioningTaskThreeTimes_DeduplicatesToOneEntry()
    {
        SeedTask("task-a");
        SeedWikiPage("concepts", "repeat.md",
            "# Repeat\n\nFirst [[task-a]], second [[task-a]], third [[task-a]].\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.HasCount(1, index.GetBacklinks("task-a"));
    }

    [TestMethod]
    public void GetBacklinks_AliasedLink_MatchesSameAsBareLink()
    {
        SeedTask("task-a");
        SeedWikiPage("concepts", "aliased.md", "# Aliased\n\nSee [[task-a|Alpha Task]].\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.HasCount(1, index.GetBacklinks("task-a"));
    }

    [TestMethod]
    public void GetBacklinks_PagesInsideWikiTodo_AreExcluded()
    {
        SeedTask("task-a");
        // A second task file that links to task-a — should NOT show up as a backlink.
        File.WriteAllText(
            Path.Combine(_vaultRoot, "wiki", "todo", "task-b.md"),
            "---\nid: task-b\ntitle: task-b\n---\n\nLinks to [[task-a]].\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.IsEmpty(index.GetBacklinks("task-a"));
    }

    [TestMethod]
    public void GetBacklinks_PageTypeClassification_FromParentFolder()
    {
        SeedTask("task-a");
        SeedWikiPage("concepts", "c.md", "# C\n[[task-a]]\n");
        SeedWikiPage("decisions", "d.md", "# D\n[[task-a]]\n");
        SeedWikiPage("incidents", "i.md", "# I\n[[task-a]]\n");
        SeedWikiPage("systems", "s.md", "# S\n[[task-a]]\n");
        SeedWikiPage("misc", "o.md", "# O\n[[task-a]]\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        var byTitle = index.GetBacklinks("task-a")
            .ToDictionary(b => b.LinkingPageTitle, b => b.PageType);

        Assert.AreEqual(BacklinkPageType.Concept, byTitle["C"]);
        Assert.AreEqual(BacklinkPageType.Decision, byTitle["D"]);
        Assert.AreEqual(BacklinkPageType.Incident, byTitle["I"]);
        Assert.AreEqual(BacklinkPageType.System, byTitle["S"]);
        Assert.AreEqual(BacklinkPageType.Other, byTitle["O"]);
    }

    [TestMethod]
    public void GetBacklinks_TitleResolution_FrontmatterWinsOverH1WinsOverFilename()
    {
        SeedTask("task-a");
        SeedWikiPage("concepts", "from-fm.md",
            "---\ntitle: Frontmatter Wins\n---\n\n# H1 Loser\n\n[[task-a]]\n");
        SeedWikiPage("concepts", "from-h1.md",
            "# H1 Wins\n\n[[task-a]]\n");
        SeedWikiPage("concepts", "from-filename.md", "Just a body. [[task-a]]\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        var titles = index.GetBacklinks("task-a").Select(b => b.LinkingPageTitle).ToHashSet();
        Assert.Contains("Frontmatter Wins", titles);
        Assert.Contains("H1 Wins", titles);
        Assert.Contains("from-filename", titles);
    }

    [TestMethod]
    public void GetBacklinks_StemMatchIsCaseSensitive()
    {
        SeedTask("task-a");
        // Wrong case — must not match.
        SeedWikiPage("concepts", "wrong-case.md", "# Wrong\n[[Task-A]]\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.IsEmpty(index.GetBacklinks("task-a"));
    }

    [TestMethod]
    public void GetBacklinks_OrderedByPageTypeBucketThenAlphabeticalTitle()
    {
        SeedTask("task-a");
        SeedWikiPage("systems", "z-system.md", "# Z System\n[[task-a]]\n");
        SeedWikiPage("concepts", "beta.md", "# Beta\n[[task-a]]\n");
        SeedWikiPage("concepts", "alpha.md", "# Alpha\n[[task-a]]\n");
        SeedWikiPage("decisions", "decide.md", "# Decide\n[[task-a]]\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        var titles = index.GetBacklinks("task-a").Select(b => b.LinkingPageTitle).ToList();
        CollectionAssert.AreEqual(
            new[] { "Alpha", "Beta", "Decide", "Z System" },
            titles);
    }

    [TestMethod]
    public void GetBacklinks_PageLinkingMultipleTasks_AppearsUnderEachTask()
    {
        SeedTask("task-a");
        SeedTask("task-b");
        SeedWikiPage("concepts", "multi.md", "# Multi\n\n[[task-a]] and [[task-b]].\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);

        Assert.HasCount(1, index.GetBacklinks("task-a"));
        Assert.HasCount(1, index.GetBacklinks("task-b"));
    }

    [TestMethod]
    public void Build_IsRepeatable_ReplacesPreviousIndex()
    {
        SeedTask("task-a");
        var page = SeedWikiPage("concepts", "alpha.md", "# Alpha\n\n[[task-a]]\n");

        var index = new BacklinkIndex();
        index.Build(_vaultRoot);
        Assert.HasCount(1, index.GetBacklinks("task-a"));

        // Remove the link from the page and rebuild — should drop to zero.
        File.WriteAllText(page, "# Alpha\n\nno more link\n");
        index.Build(_vaultRoot);

        Assert.IsEmpty(index.GetBacklinks("task-a"));
    }
}
