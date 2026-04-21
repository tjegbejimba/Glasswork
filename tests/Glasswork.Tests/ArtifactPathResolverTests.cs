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
    public void ReturnsFalse_ForNonMdFile()
    {
        var path = @"C:\vault\wiki\todo\TASK-123.artifacts\plan.tmp";
        Assert.IsFalse(ArtifactPathResolver.TryGetTaskId(path, out _));
    }

    [TestMethod]
    public void IsCaseInsensitive_OnArtifactsSuffix()
    {
        var path = @"C:\vault\wiki\todo\TASK-123.Artifacts\plan.md";
        Assert.IsTrue(ArtifactPathResolver.TryGetTaskId(path, out var id));
        Assert.AreEqual("TASK-123", id);
    }
}
