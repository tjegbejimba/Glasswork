using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class FileSystemArtifactStoreTests
{
    private string _vaultRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-artifact-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot)) Directory.Delete(_vaultRoot, recursive: true);
    }

    private string ArtifactsFolder(string taskId)
    {
        var folder = Path.Combine(_vaultRoot, "wiki", "todo", taskId + ".artifacts");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void Load_NoArtifactsFolder_ReturnsEmpty()
    {
        var store = new FileSystemArtifactStore(_vaultRoot);

        var result = store.Load("some-task");

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void Load_FolderWithSingleMarkdownNoFrontmatterNoHeading_TitleFallsBackToFilename()
    {
        var folder = ArtifactsFolder("my-task");
        File.WriteAllText(Path.Combine(folder, "plain-note.md"), "Just some body text.");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("plain-note", result[0].Title);
        Assert.AreEqual("Just some body text.", result[0].Body);
    }

    [TestMethod]
    public void Load_IgnoresNonMarkdownFiles()
    {
        var folder = ArtifactsFolder("my-task");
        File.WriteAllText(Path.Combine(folder, "real.md"), "real content");
        File.WriteAllText(Path.Combine(folder, "scratch.tmp"), "ignore me");
        File.WriteAllText(Path.Combine(folder, "data.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "draft.md.tmp"), "still being written");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("real", result[0].Title);
    }

    [TestMethod]
    public void Load_OrdersByModifiedTimeDescending()
    {
        var folder = ArtifactsFolder("my-task");
        var oldPath = Path.Combine(folder, "old.md");
        var midPath = Path.Combine(folder, "mid.md");
        var newPath = Path.Combine(folder, "new.md");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(midPath, "mid");
        File.WriteAllText(newPath, "new");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(oldPath, now.AddHours(-2));
        File.SetLastWriteTimeUtc(midPath, now.AddHours(-1));
        File.SetLastWriteTimeUtc(newPath, now);

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(3, result);
        Assert.AreEqual("new", result[0].Title);
        Assert.AreEqual("mid", result[1].Title);
        Assert.AreEqual("old", result[2].Title);
    }

    [TestMethod]
    public void Load_TitleFromFirstH1WhenNoFrontmatterTitle()
    {
        var folder = ArtifactsFolder("my-task");
        File.WriteAllText(Path.Combine(folder, "note.md"),
            "# My Heading\n\nSome body text.\n\n# Second Heading\n");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("My Heading", result[0].Title);
    }

    [TestMethod]
    public void Load_TitleFromFrontmatterWinsOverH1AndFilename()
    {
        var folder = ArtifactsFolder("my-task");
        File.WriteAllText(Path.Combine(folder, "filename-stem.md"),
            "---\ntitle: Frontmatter Wins\n---\n\n# H1 Loser\n\nbody\n");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("Frontmatter Wins", result[0].Title);
    }

    [TestMethod]
    public void Load_TruncatesH1TitleAtRoughly80Chars()
    {
        var folder = ArtifactsFolder("my-task");
        var longHeading = new string('x', 200);
        File.WriteAllText(Path.Combine(folder, "long.md"), $"# {longHeading}\n\nbody\n");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.IsLessThanOrEqualTo(80, result[0].Title.Length);
    }

    [TestMethod]
    public void Load_MalformedFrontmatterFallsBackWithoutThrowing()
    {
        var folder = ArtifactsFolder("my-task");
        File.WriteAllText(Path.Combine(folder, "broken.md"),
            "---\ntitle: [unterminated\n  bad: : yaml\n---\n\n# Good Fallback\n\nbody\n");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("my-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("Good Fallback", result[0].Title);
    }
}
