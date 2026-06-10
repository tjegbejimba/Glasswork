using System;
using System.Collections.Generic;
using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactRowTests
{
    private static Artifact MakeArtifact(string title, DateTime mtimeUtc)
        => new(Path: $"C:\\fake\\{title}.md", Title: title, ModifiedUtc: mtimeUtc, Body: "body");

    private static ArtifactRow MakeRow(
        string path,
        ArtifactKind kind,
        string? body,
        long sizeBytes = 0,
        string? loadError = null)
    {
        var artifact = new Artifact(
            Path: path,
            Title: System.IO.Path.GetFileName(path),
            ModifiedUtc: DateTime.UtcNow,
            Body: body)
        {
            Kind = kind,
            SizeBytes = sizeBytes,
            LoadError = loadError,
        };
        return new ArtifactRow(artifact, IsExpanded: false, TimeBadge: "just now");
    }

    [TestMethod]
    public void Project_Empty_ReturnsEmpty()
    {
        var rows = ArtifactRow.Project([], DateTime.UtcNow);
        Assert.IsEmpty(rows);
    }

    [TestMethod]
    public void Project_NewestRow_IsExpandedByDefault()
    {
        var now = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<Artifact>
        {
            MakeArtifact("oldest", now.AddDays(-3)),
            MakeArtifact("middle", now.AddHours(-2)),
            MakeArtifact("newest", now.AddMinutes(-5)),
        };

        var rows = ArtifactRow.Project(artifacts, now);

        Assert.HasCount(3, rows);
        Assert.IsFalse(rows[0].IsExpanded);
        Assert.IsFalse(rows[1].IsExpanded);
        Assert.IsTrue(rows[2].IsExpanded, "newest should auto-expand");
    }

    [TestMethod]
    public void Project_TimeBadge_UsesRelativeStrings()
    {
        var now = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<Artifact>
        {
            MakeArtifact("seconds", now.AddSeconds(-30)),
            MakeArtifact("minutes", now.AddMinutes(-10)),
            MakeArtifact("hours", now.AddHours(-3)),
            MakeArtifact("days", now.AddDays(-2)),
        };

        var rows = ArtifactRow.Project(artifacts, now);

        Assert.AreEqual("just now", rows[0].TimeBadge);
        Assert.AreEqual("10m ago", rows[1].TimeBadge);
        Assert.AreEqual("3h ago", rows[2].TimeBadge);
        Assert.AreEqual("2d ago", rows[3].TimeBadge);
    }

    [TestMethod]
    public void Project_PreservesArtifactReference()
    {
        var now = DateTime.UtcNow;
        var a = MakeArtifact("plan", now);
        var rows = ArtifactRow.Project([a], now);
        Assert.AreSame(a, rows[0].Artifact);
        Assert.AreEqual("plan", rows[0].Title);
        Assert.AreEqual("body", rows[0].Body);
    }

    // ---- Per-kind flags (multi-format artifacts, ADR 0015) ----

    [TestMethod]
    public void Kind_And_SizeBytes_PassThroughFromArtifact()
    {
        var row = MakeRow("C:\\v\\a.png", ArtifactKind.Image, body: null, sizeBytes: 4096);
        Assert.AreEqual(ArtifactKind.Image, row.Kind);
        Assert.AreEqual(4096, row.SizeBytes);
    }

    [TestMethod]
    public void HasInlineBody_TrueOnlyWhenBodyNonNull()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x").HasInlineBody);
        Assert.IsFalse(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: null).HasInlineBody);
        // Body getter coalesces null->"" so a null check on Body is unreliable; HasInlineBody is the source of truth.
        Assert.AreEqual("", MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: null).Body);
    }

    [TestMethod]
    public void HasLoadError_ReflectsLoadError()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: null, loadError: "boom").HasLoadError);
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x").HasLoadError);
    }

    [TestMethod]
    public void ShouldRenderInlineMarkdown_RequiresMarkdownKind_Body_AndNoError()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x").ShouldRenderInlineMarkdown);
        Assert.IsFalse(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: null).ShouldRenderInlineMarkdown);
        Assert.IsFalse(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x", loadError: "e").ShouldRenderInlineMarkdown);
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x").ShouldRenderInlineMarkdown);
    }

    [TestMethod]
    public void ShouldRenderInlineText_RequiresTextKind_Body_AndNoError()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x").ShouldRenderInlineText);
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: null).ShouldRenderInlineText);
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x", loadError: "e").ShouldRenderInlineText);
        Assert.IsFalse(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x").ShouldRenderInlineText);
    }

    [TestMethod]
    public void ShowOpenInObsidian_OnlyForMarkdown()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x").ShowOpenInObsidian);
        Assert.IsTrue(MakeRow("C:\\v\\big.md", ArtifactKind.Markdown, body: null).ShowOpenInObsidian);
        Assert.IsFalse(MakeRow("C:\\v\\a.html", ArtifactKind.Html, body: null).ShowOpenInObsidian);
        Assert.IsFalse(MakeRow("C:\\v\\a.png", ArtifactKind.Image, body: null).ShowOpenInObsidian);
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x").ShowOpenInObsidian);
    }

    [TestMethod]
    public void IsSvg_OnlyForSvgImage()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.svg", ArtifactKind.Image, body: null).IsSvg);
        Assert.IsTrue(MakeRow("C:\\v\\A.SVG", ArtifactKind.Image, body: null).IsSvg);
        Assert.IsFalse(MakeRow("C:\\v\\a.png", ArtifactKind.Image, body: null).IsSvg);
        Assert.IsFalse(MakeRow("C:\\v\\a.svg", ArtifactKind.Other, body: null).IsSvg);
    }

    [TestMethod]
    public void LaunchGating_DeniesExecutableExtensions()
    {
        var denied = MakeRow("C:\\v\\run.ps1", ArtifactKind.Text, body: "x");
        Assert.IsTrue(denied.IsLaunchDenied);
        Assert.IsFalse(denied.CanLaunchExternally);

        var allowed = MakeRow("C:\\v\\data.json", ArtifactKind.Text, body: "x");
        Assert.IsFalse(allowed.IsLaunchDenied);
        Assert.IsTrue(allowed.CanLaunchExternally);
    }

    [TestMethod]
    public void IsReference_TrueForOther_LoadError_OverCapImage_AndBodylessTextOrMarkdown()
    {
        Assert.IsTrue(MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null).IsReference, "Other");
        Assert.IsTrue(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: null, loadError: "e").IsReference, "load error");
        Assert.IsTrue(MakeRow("C:\\v\\big.md", ArtifactKind.Markdown, body: null).IsReference, "over-cap markdown (null body)");
        Assert.IsTrue(MakeRow("C:\\v\\big.txt", ArtifactKind.Text, body: null).IsReference, "over-cap text (null body)");
        Assert.IsTrue(MakeRow("C:\\v\\huge.png", ArtifactKind.Image, body: null, sizeBytes: ArtifactCaps.InlineImageBytes + 1).IsReference, "over-cap image");
    }

    [TestMethod]
    public void IsReference_FalseForRenderableKinds()
    {
        Assert.IsFalse(MakeRow("C:\\v\\a.md", ArtifactKind.Markdown, body: "x").IsReference, "inline markdown");
        Assert.IsFalse(MakeRow("C:\\v\\a.txt", ArtifactKind.Text, body: "x").IsReference, "inline text");
        Assert.IsFalse(MakeRow("C:\\v\\a.png", ArtifactKind.Image, body: null, sizeBytes: 4096).IsReference, "under-cap image (null body is expected)");
        Assert.IsFalse(MakeRow("C:\\v\\a.html", ArtifactKind.Html, body: null).IsReference, "html renders via source");
    }

    [TestMethod]
    public void SizeDisplay_FormatsHumanReadable()
    {
        Assert.AreEqual("0 B", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 0).SizeDisplay);
        Assert.AreEqual("512 B", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 512).SizeDisplay);
        Assert.AreEqual("1.0 KB", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 1024).SizeDisplay);
        Assert.AreEqual("12.3 KB", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 12595).SizeDisplay);
        Assert.AreEqual("1.5 MB", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 1572864).SizeDisplay);
        Assert.AreEqual("2.0 GB", MakeRow("C:\\v\\a.bin", ArtifactKind.Other, body: null, sizeBytes: 2L * 1024 * 1024 * 1024).SizeDisplay);
    }
}
