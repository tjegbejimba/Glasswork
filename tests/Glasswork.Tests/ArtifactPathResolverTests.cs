using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactPathResolverTests
{
    [TestMethod]
    public void Resolves_TaskId_FromArtifactsFolder()
    {
        var path = @"C:\vault\wiki\todo\TASK-123.artifacts\plan.md";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.AreEqual("TASK-123", id);
    }

    [TestMethod]
    public void Resolves_TaskId_WithComplexId()
    {
        var path = @"C:\vault\wiki\todo\artifacts-feature-49.artifacts\notes.md";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.AreEqual("artifacts-feature-49", id);
    }

    [TestMethod]
    public void Resolves_TaskId_WithForwardSlashes()
    {
        var path = "C:/vault/wiki/todo/abc.artifacts/x.md";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.AreEqual("abc", id);
    }

    [TestMethod]
    public void ReturnsFalse_ForTopLevelMd()
    {
        var path = @"C:\vault\wiki\todo\TASK-123.md";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.IsNull(id);
    }

    [TestMethod]
    public void ReturnsFalse_ForUnrelatedPath()
    {
        var path = @"C:\vault\wiki\todo\some-folder\file.md";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.IsNull(id);
    }

    [TestMethod]
    public void ReturnsFalse_ForNullOrEmpty()
    {
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(null, out _));
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId("", out _));
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId("   ", out _));
    }

    [TestMethod]
    public void Resolves_TaskId_FromCommittedNonMdFile()
    {
        // Multi-format: accept any committed file, not just .md
        var htmlPath = @"C:\vault\wiki\todo\TASK-123.artifacts\report.html";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(htmlPath, out var id1));
        Assert.AreEqual("TASK-123", id1);

        var pngPath = @"C:\vault\wiki\todo\TASK-456.artifacts\screenshot.png";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(pngPath, out var id2));
        Assert.AreEqual("TASK-456", id2);

        var jsonPath = @"C:\vault\wiki\todo\my-task.artifacts\data.json";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(jsonPath, out var id3));
        Assert.AreEqual("my-task", id3);
    }

    [TestMethod]
    public void ReturnsFalse_ForTransientFiles()
    {
        // Reject transient files per ArtifactCommitPolicy
        var tmpPath = @"C:\vault\wiki\todo\TASK-123.artifacts\plan.tmp";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(tmpPath, out _));

        var partPath = @"C:\vault\wiki\todo\TASK-123.artifacts\download.part";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(partPath, out _));

        var dotfilePath = @"C:\vault\wiki\todo\TASK-123.artifacts\.hidden";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(dotfilePath, out _));

        var officePath = @"C:\vault\wiki\todo\TASK-123.artifacts\~$doc.docx";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(officePath, out _));
    }

    [TestMethod]
    public void ReturnsFalse_ForOsJunkFiles()
    {
        var thumbsPath = @"C:\vault\wiki\todo\TASK-123.artifacts\Thumbs.db";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(thumbsPath, out _));

        var dsStorePath = @"C:\vault\wiki\todo\TASK-123.artifacts\.DS_Store";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(dsStorePath, out _));

        var desktopIniPath = @"C:\vault\wiki\todo\TASK-123.artifacts\desktop.ini";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(desktopIniPath, out _));
    }

    [TestMethod]
    public void IsCaseInsensitive_OnArtifactsSuffix()
    {
        var path = @"C:\vault\wiki\todo\TASK-123.Artifacts\plan.md";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.AreEqual("TASK-123", id);
    }
}
