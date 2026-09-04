using Glasswork.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public sealed class ArtifactKindResolverTests
{
    [TestMethod]
    public void Resolve_MarkdownExtensions_ReturnsMarkdown()
    {
        Assert.AreEqual(ArtifactKind.Markdown, ArtifactKindResolver.Resolve("plan.md"));
        Assert.AreEqual(ArtifactKind.Markdown, ArtifactKindResolver.Resolve("design.markdown"));
    }

    [TestMethod]
    public void Resolve_HtmlExtensions_ReturnsHtml()
    {
        Assert.AreEqual(ArtifactKind.Html, ArtifactKindResolver.Resolve("report.html"));
        Assert.AreEqual(ArtifactKind.Html, ArtifactKindResolver.Resolve("output.htm"));
    }

    [TestMethod]
    public void Resolve_ImageExtensions_ReturnsImage()
    {
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("screenshot.png"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("diagram.jpg"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("photo.jpeg"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("icon.gif"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("image.webp"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("bitmap.bmp"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("vector.svg"));
    }

    [TestMethod]
    public void Resolve_CommonTextExtensions_ReturnsText()
    {
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("readme.txt"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("data.json"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("config.yaml"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("settings.yml"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("doc.xml"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("data.csv"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("data.tsv"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("app.log"));
    }

    [TestMethod]
    public void Resolve_CodeExtensions_ReturnsText()
    {
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("script.js"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("app.ts"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("main.py"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("Program.cs"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("run.sh"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("deploy.ps1"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("query.sql"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("main.rs"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("main.go"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("App.java"));
    }

    [TestMethod]
    public void Resolve_UnrecognizedExtension_ReturnsOther()
    {
        Assert.AreEqual(ArtifactKind.Other, ArtifactKindResolver.Resolve("data.bin"));
        Assert.AreEqual(ArtifactKind.Other, ArtifactKindResolver.Resolve("archive.zip"));
        Assert.AreEqual(ArtifactKind.Other, ArtifactKindResolver.Resolve("movie.mp4"));
    }

    [TestMethod]
    public void Resolve_ExtensionlessSmallUtf8_ReturnsText()
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        var extensionless = Path.Combine(Path.GetDirectoryName(tempFile)!, "testfile");
        try
        {
            File.WriteAllText(extensionless, "Hello, world!");
            Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve(extensionless));
        }
        finally
        {
            if (File.Exists(extensionless)) File.Delete(extensionless);
        }
    }

    [TestMethod]
    public void Resolve_ExtensionlessBinaryOrLarge_ReturnsOther()
    {
        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);
        var extensionless = Path.Combine(Path.GetDirectoryName(tempFile)!, "binaryfile");
        try
        {
            // Write binary data (not valid UTF-8)
            File.WriteAllBytes(extensionless, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC });
            Assert.AreEqual(ArtifactKind.Other, ArtifactKindResolver.Resolve(extensionless));
        }
        finally
        {
            if (File.Exists(extensionless)) File.Delete(extensionless);
        }
    }

    [TestMethod]
    public void ExecutableDenyList_ContainsCommonExecutableExtensions()
    {
        var denyList = ArtifactKindResolver.ExecutableDenyList;

        Assert.Contains(".exe", denyList);
        Assert.Contains(".com", denyList);
        Assert.Contains(".cmd", denyList);
        Assert.Contains(".bat", denyList);
        Assert.Contains(".ps1", denyList);
        Assert.Contains(".psm1", denyList);
        Assert.Contains(".vbs", denyList);
        Assert.Contains(".js", denyList);
        Assert.Contains(".jar", denyList);
        Assert.Contains(".msi", denyList);
        Assert.Contains(".dll", denyList);
        Assert.Contains(".lnk", denyList);
        Assert.Contains(".url", denyList);
    }

    [TestMethod]
    public void ExecutableDenyList_JsAndPs1InBothTextAndDenyList()
    {
        // .js and .ps1 should resolve to Text for rendering but be in deny list for launch
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("script.js"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("deploy.ps1"));

        var denyList = ArtifactKindResolver.ExecutableDenyList;
        Assert.Contains(".js", denyList);
        Assert.Contains(".ps1", denyList);
    }

    [TestMethod]
    public void Resolve_CaseInsensitive()
    {
        Assert.AreEqual(ArtifactKind.Markdown, ArtifactKindResolver.Resolve("plan.MD"));
        Assert.AreEqual(ArtifactKind.Html, ArtifactKindResolver.Resolve("report.HTML"));
        Assert.AreEqual(ArtifactKind.Image, ArtifactKindResolver.Resolve("screenshot.PNG"));
        Assert.AreEqual(ArtifactKind.Text, ArtifactKindResolver.Resolve("script.JS"));
    }
}
