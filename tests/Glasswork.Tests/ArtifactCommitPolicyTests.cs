using Glasswork.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public sealed class ArtifactCommitPolicyTests
{
    [TestMethod]
    public void IsCommitted_NormalFile_ReturnsTrue()
    {
        Assert.IsTrue(ArtifactCommitPolicy.IsCommitted("plan.md"));
        Assert.IsTrue(ArtifactCommitPolicy.IsCommitted("report.html"));
        Assert.IsTrue(ArtifactCommitPolicy.IsCommitted("data.json"));
        Assert.IsTrue(ArtifactCommitPolicy.IsCommitted("screenshot.png"));
    }

    [TestMethod]
    public void IsCommitted_DotFiles_ReturnsFalse()
    {
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(".gitignore"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(".hidden"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(".DS_Store"));
    }

    [TestMethod]
    public void IsCommitted_TempFiles_ReturnsFalse()
    {
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("data.tmp"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("download.part"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("file.crdownload"));
    }

    [TestMethod]
    public void IsCommitted_OfficeTempFiles_ReturnsFalse()
    {
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("~$document.docx"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("~$report.xlsx"));
    }

    [TestMethod]
    public void IsCommitted_OsJunkFiles_ReturnsFalse()
    {
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("Thumbs.db"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("desktop.ini"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(".DS_Store"));
    }

    [TestMethod]
    public void IsCommitted_CaseInsensitive()
    {
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("DATA.TMP"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("THUMBS.DB"));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted("DESKTOP.INI"));
    }

    [TestMethod]
    public void IsCommitted_PathWithDirectory_UsesBaseName()
    {
        var artifactDirectory = Path.Combine(
            "vault",
            "wiki",
            "todo",
            "task-1.artifacts");

        Assert.IsTrue(ArtifactCommitPolicy.IsCommitted(
            Path.Combine(artifactDirectory, "plan.md")));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(
            Path.Combine(artifactDirectory, ".hidden")));
        Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(
            Path.Combine(artifactDirectory, "data.tmp")));
    }

    [TestMethod]
    public void IsCommitted_HiddenFile_UsesPlatformSemantics()
    {
        var fileName = OperatingSystem.IsWindows()
            ? $"test-hidden-artifact-{Guid.NewGuid():N}.md"
            : $".test-hidden-artifact-{Guid.NewGuid():N}.md";
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);

        try
        {
            File.WriteAllText(tempPath, "test content");

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(tempPath, FileAttributes.Hidden);
                Assert.IsTrue(
                    File.GetAttributes(tempPath).HasFlag(FileAttributes.Hidden));
            }

            Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(tempPath));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
    }

    [TestMethod]
    public void IsCommitted_SystemAttribute_UsesPlatformSemantics()
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"test-system-artifact-{Guid.NewGuid():N}.md");

        try
        {
            File.WriteAllText(tempPath, "test content");

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(tempPath, FileAttributes.System);
                Assert.IsTrue(
                    File.GetAttributes(tempPath).HasFlag(FileAttributes.System));
                Assert.IsFalse(ArtifactCommitPolicy.IsCommitted(tempPath));
            }
            else
            {
                Assert.IsFalse(
                    File.GetAttributes(tempPath).HasFlag(FileAttributes.System));
                Assert.IsTrue(ArtifactCommitPolicy.IsCommitted(tempPath));
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
    }

    [TestMethod]
    public void IsCommitted_NormalAttributeFile_ReturnsTrue()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "test-artifact.md");
        try
        {
            File.WriteAllText(tempPath, "test content");
            File.SetAttributes(tempPath, FileAttributes.Normal);
            Assert.IsTrue(ArtifactCommitPolicy.IsCommitted(tempPath));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
