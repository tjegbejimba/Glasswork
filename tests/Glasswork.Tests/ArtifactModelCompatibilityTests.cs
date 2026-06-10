using Glasswork.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public sealed class ArtifactModelTests
{
    [TestMethod]
    public void Artifact_LegacyConstructor_PreservesCompatibility()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task-1.artifacts\plan.md",
            Title: "Plan",
            ModifiedUtc: DateTime.UtcNow,
            Body: "# Plan content"
        );

        Assert.AreEqual(@"C:\vault\wiki\todo\task-1.artifacts\plan.md", artifact.Path);
        Assert.AreEqual("Plan", artifact.Title);
        Assert.AreEqual("# Plan content", artifact.Body);
        Assert.IsNotNull(artifact.ModifiedUtc);
    }

    [TestMethod]
    public void Artifact_DefaultKind_IsMarkdown()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\plan.md",
            Title: "Plan",
            ModifiedUtc: DateTime.UtcNow,
            Body: "content"
        );

        Assert.AreEqual(ArtifactKind.Markdown, artifact.Kind);
    }

    [TestMethod]
    public void Artifact_DefaultSizeBytes_IsZero()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\plan.md",
            Title: "Plan",
            ModifiedUtc: DateTime.UtcNow,
            Body: "content"
        );

        Assert.AreEqual(0L, artifact.SizeBytes);
    }

    [TestMethod]
    public void Artifact_DefaultLoadError_IsNull()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\plan.md",
            Title: "Plan",
            ModifiedUtc: DateTime.UtcNow,
            Body: "content"
        );

        Assert.IsNull(artifact.LoadError);
    }

    [TestMethod]
    public void Artifact_NullableBody_AcceptsNull()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\image.png",
            Title: "Screenshot",
            ModifiedUtc: DateTime.UtcNow,
            Body: null
        )
        {
            Kind = ArtifactKind.Image,
            SizeBytes = 12345
        };

        Assert.IsNull(artifact.Body);
        Assert.AreEqual(ArtifactKind.Image, artifact.Kind);
        Assert.AreEqual(12345L, artifact.SizeBytes);
    }

    [TestMethod]
    public void Artifact_InitProperties_CanBeSet()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\report.html",
            Title: "Report",
            ModifiedUtc: DateTime.UtcNow,
            Body: "<html></html>"
        )
        {
            Kind = ArtifactKind.Html,
            SizeBytes = 999,
            LoadError = "Too large"
        };

        Assert.AreEqual(ArtifactKind.Html, artifact.Kind);
        Assert.AreEqual(999L, artifact.SizeBytes);
        Assert.AreEqual("Too large", artifact.LoadError);
    }

    [TestMethod]
    public void ArtifactRow_BodyProperty_CoalescesNullToEmpty()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\image.png",
            Title: "Screenshot",
            ModifiedUtc: DateTime.UtcNow,
            Body: null
        )
        {
            Kind = ArtifactKind.Image
        };

        var row = new ArtifactRow(artifact, IsExpanded: true, TimeBadge: "just now");

        Assert.AreEqual("", row.Body);
    }

    [TestMethod]
    public void ArtifactRow_BodyProperty_PreservesNonNullBody()
    {
        var artifact = new Artifact(
            Path: @"C:\vault\plan.md",
            Title: "Plan",
            ModifiedUtc: DateTime.UtcNow,
            Body: "# Content"
        );

        var row = new ArtifactRow(artifact, IsExpanded: true, TimeBadge: "just now");

        Assert.AreEqual("# Content", row.Body);
    }
}
