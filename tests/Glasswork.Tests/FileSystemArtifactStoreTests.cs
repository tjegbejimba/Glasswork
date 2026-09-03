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

        // After Slice 2: we include data.json as Text, but still exclude .tmp files
        Assert.HasCount(2, result);
        Assert.IsTrue(result.Any(a => a.Path.EndsWith("real.md")));
        Assert.IsTrue(result.Any(a => a.Path.EndsWith("data.json")));
        Assert.IsFalse(result.Any(a => a.Path.Contains(".tmp")));
    }

    [TestMethod]
    public void Load_OrdersByModifiedTimeNewestFirst()
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

    // === Multi-format artifact tests (Slice 2) ===

    [TestMethod]
    public void Load_MixedFormatFolder_EachRowHasCorrectKindAndSize()
    {
        var folder = ArtifactsFolder("multi-task");
        var mdPath = Path.Combine(folder, "plan.md");
        var htmlPath = Path.Combine(folder, "report.html");
        var pngPath = Path.Combine(folder, "screenshot.png");
        var txtPath = Path.Combine(folder, "notes.txt");
        var svgPath = Path.Combine(folder, "diagram.svg");
        
        File.WriteAllText(mdPath, "# Plan\n\nSome markdown");
        File.WriteAllText(htmlPath, "<html><body>Report</body></html>");
        File.WriteAllBytes(pngPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header
        File.WriteAllText(txtPath, "Plain text notes");
        File.WriteAllText(svgPath, "<svg></svg>");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("multi-task");

        Assert.HasCount(5, result);
        
        var md = result.First(a => a.Path.EndsWith("plan.md"));
        Assert.AreEqual(ArtifactKind.Markdown, md.Kind);
        Assert.IsNotNull(md.Body);
        Assert.IsTrue(md.SizeBytes > 0);
        
        var html = result.First(a => a.Path.EndsWith("report.html"));
        Assert.AreEqual(ArtifactKind.Html, html.Kind);
        Assert.IsNull(html.Body);
        Assert.IsTrue(html.SizeBytes > 0);
        
        var png = result.First(a => a.Path.EndsWith("screenshot.png"));
        Assert.AreEqual(ArtifactKind.Image, png.Kind);
        Assert.IsNull(png.Body);
        Assert.IsTrue(png.SizeBytes > 0);
        
        var txt = result.First(a => a.Path.EndsWith("notes.txt"));
        Assert.AreEqual(ArtifactKind.Text, txt.Kind);
        Assert.IsNotNull(txt.Body);
        Assert.IsTrue(txt.SizeBytes > 0);
        
        var svg = result.First(a => a.Path.EndsWith("diagram.svg"));
        Assert.AreEqual(ArtifactKind.Image, svg.Kind);
        Assert.IsNull(svg.Body);
        Assert.IsTrue(svg.SizeBytes > 0);
    }

    [TestMethod]
    public void Load_JunkAndTransientFiles_Excluded()
    {
        var folder = ArtifactsFolder("junk-task");
        File.WriteAllText(Path.Combine(folder, "real.md"), "real content");
        File.WriteAllText(Path.Combine(folder, ".DS_Store"), "junk");
        File.WriteAllText(Path.Combine(folder, "~$temp.md"), "office temp");
        File.WriteAllText(Path.Combine(folder, "scratch.tmp"), "transient");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("junk-task");

        Assert.HasCount(1, result);
        Assert.AreEqual("real", result[0].Title);
    }

    [TestMethod]
    public void Load_OverCapTextFile_BodyNullButSizeCorrect()
    {
        var folder = ArtifactsFolder("big-task");
        var bigContent = new string('x', (int)ArtifactCaps.InlineTextBytes + 1000);
        var path = Path.Combine(folder, "huge.txt");
        File.WriteAllText(path, bigContent);

        var result = new FileSystemArtifactStore(_vaultRoot).Load("big-task");

        Assert.HasCount(1, result);
        var artifact = result[0];
        Assert.AreEqual(ArtifactKind.Text, artifact.Kind);
        Assert.IsNull(artifact.Body);
        Assert.IsTrue(artifact.SizeBytes > ArtifactCaps.InlineTextBytes);
        Assert.AreEqual("huge.txt", artifact.Title);
    }

    [TestMethod]
    public void Load_UnreadableFile_LoadErrorPopulatedOtherRowsReturned()
    {
        var folder = ArtifactsFolder("error-task");
        var goodPath = Path.Combine(folder, "good.txt");
        var badPath = Path.Combine(folder, "bad.txt");
        File.WriteAllText(goodPath, "good content");
        File.WriteAllText(badPath, "bad content");
        
        // Make the file unreadable by opening it exclusively
        using (var stream = File.Open(badPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new FileSystemArtifactStore(_vaultRoot).Load("error-task");

            Assert.HasCount(2, result);
            var good = result.First(a => a.Path.EndsWith("good.txt"));
            Assert.IsNull(good.LoadError);
            Assert.IsNotNull(good.Body);
            
            var bad = result.First(a => a.Path.EndsWith("bad.txt"));
            Assert.IsNotNull(bad.LoadError);
            Assert.IsNull(bad.Body);
        }
    }

    [TestMethod]
    public void Load_NonMarkdownFiles_TitleIsFilenameWithExtension()
    {
        var folder = ArtifactsFolder("title-task");
        File.WriteAllText(Path.Combine(folder, "report.html"), "<html>test</html>");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "plain text");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("title-task");

        Assert.HasCount(2, result);
        var html = result.First(a => a.Path.EndsWith("report.html"));
        Assert.AreEqual("report.html", html.Title);
        
        var txt = result.First(a => a.Path.EndsWith("notes.txt"));
        Assert.AreEqual("notes.txt", txt.Title);
    }

    [TestMethod]
    public void Load_TextExtensionWithBinaryContent_FailsLocallyInsteadOfInliningReplacementText()
    {
        var folder = ArtifactsFolder("binary-text");
        File.WriteAllBytes(Path.Combine(folder, "hostile.txt"), [0xff, 0xfe, 0xfd, 0x00]);
        File.WriteAllText(Path.Combine(folder, "safe.txt"), "safe");

        var result = new FileSystemArtifactStore(_vaultRoot).Load("binary-text");

        var hostile = result.Single(a => a.Title == "hostile.txt");
        Assert.IsNull(hostile.Body);
        Assert.IsNotNull(hostile.LoadError);
        Assert.AreEqual("safe", result.Single(a => a.Title == "safe.txt").Body);
    }
}
