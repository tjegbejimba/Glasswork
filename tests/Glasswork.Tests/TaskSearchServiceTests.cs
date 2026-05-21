using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class TaskSearchServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskSearchService _search = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-search-" + Guid.NewGuid().ToString("N"));
        _vault = new VaultService(_tempDir);
        _search = new TaskSearchService(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Search_MatchesDescriptionAndNotes()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "desc-notes",
            Title = "Unrelated title",
            Description = "Discusses frobulator rollout.",
            Notes = "Follow-up on frobulator incidents."
        });

        var hits = _search.Search("frobulator");

        Assert.AreEqual(1, hits.Count);
        CollectionAssert.AreEquivalent(
            new[] { "description", "notes" },
            hits[0].MatchedIn.ToArray());
    }

    [TestMethod]
    public void Search_MatchesSubtasksAndTags()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "subtags",
            Title = "Unrelated",
            Tags = ["batch-api"],
            Subtasks =
            [
                new SubTask { Text = "Validate batch gateway path", Notes = "handles transport edge cases" }
            ]
        });

        var hits = _search.Search("batch", fields: ["subtasks", "tags"]);

        Assert.AreEqual(1, hits.Count);
        CollectionAssert.AreEquivalent(
            new[] { "subtasks", "tags" },
            hits[0].MatchedIn.ToArray());
    }

    [TestMethod]
    public void Search_InScopeRestrictsSearchFields()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "scoped",
            Title = "No match in title",
            Notes = "contains snowflake-term"
        });

        var hits = _search.Search("snowflake-term", fields: ["title"]);

        Assert.AreEqual(0, hits.Count);
    }

    [TestMethod]
    public void Search_TitleHitsRankBeforeBodyHits()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "body-hit",
            Title = "Unrelated",
            Notes = "batch appears in notes only"
        });
        _vault.Save(new GlassworkTask
        {
            Id = "title-hit",
            Title = "Batch processing task"
        });

        var hits = _search.Search("batch");

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual("title-hit", hits[0].Id);
    }

    [TestMethod]
    public void Search_ArtifactBodyIsOutOfScope()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "artifact-only",
            Title = "No keyword here"
        });

        var artifactDir = Path.Combine(_tempDir, "artifact-only.artifacts");
        Directory.CreateDirectory(artifactDir);
        File.WriteAllText(Path.Combine(artifactDir, "plan.md"), "contains secret-batch-token only in artifact body");

        var hits = _search.Search("secret-batch-token");

        Assert.AreEqual(0, hits.Count);
    }

    [TestMethod]
    public void Search_RejectsEmptyQuery()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _search.Search(" "));
    }
}
