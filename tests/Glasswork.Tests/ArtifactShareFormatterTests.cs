using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactShareFormatterTests
{
    [TestMethod]
    public void BuildClipboardPayload_MarkdownFormatted_ProducesPlainTextAndHtml()
    {
        var markdown = "# Release summary\n\nThis is **ready**.\n\n- Copy\n- Paste";
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\summary.md",
            Title: "Release summary",
            ModifiedUtc: DateTime.UtcNow,
            Body: markdown)
        {
            Kind = ArtifactKind.Markdown,
            SizeBytes = markdown.Length,
        };

        var payload = ArtifactShareFormatter.BuildClipboardPayload(
            artifact,
            ArtifactShareClipboardFormat.Formatted);

        Assert.AreEqual(markdown, payload.PlainText);
        Assert.IsNotNull(payload.HtmlFragment);
        StringAssert.Contains(payload.HtmlFragment, "<h1>Release summary</h1>");
        StringAssert.Contains(payload.HtmlFragment, "<strong>ready</strong>");
        StringAssert.Contains(payload.HtmlFragment, "<ul>");
        StringAssert.Contains(payload.HtmlFragment, "<li>Copy</li>");
        Assert.AreEqual("summary.md", payload.SuggestedFileName);
    }

    [TestMethod]
    public void BuildClipboardPayload_MarkdownSource_ProducesPlainTextOnly()
    {
        var markdown = "# Release summary\n\nThis is **ready**.";
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\summary.md",
            Title: "Release summary",
            ModifiedUtc: DateTime.UtcNow,
            Body: markdown)
        {
            Kind = ArtifactKind.Markdown,
            SizeBytes = markdown.Length,
        };

        var payload = ArtifactShareFormatter.BuildClipboardPayload(
            artifact,
            ArtifactShareClipboardFormat.Markdown);

        Assert.AreEqual(markdown, payload.PlainText);
        Assert.IsNull(payload.HtmlFragment);
    }

    [TestMethod]
    public void BuildClipboardPayload_MarkdownFormatted_EscapesRawHtmlAndUnsafeLinks()
    {
        var markdown = "Before <script>alert(1)</script> [run](javascript:alert(1)) [safe](https://example.com)";
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\unsafe.md",
            Title: "Unsafe",
            ModifiedUtc: DateTime.UtcNow,
            Body: markdown)
        {
            Kind = ArtifactKind.Markdown,
            SizeBytes = markdown.Length,
        };

        var payload = ArtifactShareFormatter.BuildClipboardPayload(
            artifact,
            ArtifactShareClipboardFormat.Formatted);

        StringAssert.Contains(payload.HtmlFragment!, "alert(1)");
        Assert.IsFalse(payload.HtmlFragment!.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(payload.HtmlFragment.Contains("href=\"javascript:", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(payload.HtmlFragment, " run ");
        StringAssert.Contains(payload.HtmlFragment, "<a href=\"https://example.com\">safe</a>");
    }

    [TestMethod]
    public void BuildClipboardPayload_TextFormatted_ProducesEscapedPreformattedHtml()
    {
        var text = "line 1\n<script>alert(1)</script>";
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\notes.log",
            Title: "notes.log",
            ModifiedUtc: DateTime.UtcNow,
            Body: text)
        {
            Kind = ArtifactKind.Text,
            SizeBytes = text.Length,
        };

        var payload = ArtifactShareFormatter.BuildClipboardPayload(
            artifact,
            ArtifactShareClipboardFormat.Formatted);

        Assert.AreEqual(text, payload.PlainText);
        Assert.AreEqual("<pre><code>line 1\n&lt;script&gt;alert(1)&lt;/script&gt;</code></pre>", payload.HtmlFragment);
    }

    [TestMethod]
    public void BuildClipboardPayload_HtmlFormatted_EscapesSourceInsteadOfCopyingExecutableHtml()
    {
        var html = "<h1 onclick=\"alert(1)\">Release</h1><script>alert(1)</script>";
        var artifact = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\summary.html",
            Title: "summary.html",
            ModifiedUtc: DateTime.UtcNow,
            Body: null)
        {
            Kind = ArtifactKind.Html,
            SizeBytes = html.Length,
        };

        var payload = ArtifactShareFormatter.BuildClipboardPayload(
            artifact,
            ArtifactShareClipboardFormat.Formatted,
            html);

        Assert.AreEqual(html, payload.PlainText);
        Assert.AreEqual("<pre><code>&lt;h1 onclick=&quot;alert(1)&quot;&gt;Release&lt;/h1&gt;&lt;script&gt;alert(1)&lt;/script&gt;</code></pre>", payload.HtmlFragment);
    }

    [TestMethod]
    public void GetAvailability_ImageAndOverCapText_DoNotAllowContentCopy()
    {
        var image = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\screenshot.png",
            Title: "screenshot.png",
            ModifiedUtc: DateTime.UtcNow,
            Body: null)
        {
            Kind = ArtifactKind.Image,
            SizeBytes = 1024,
        };
        var overCapText = new Artifact(
            Path: @"C:\vault\wiki\todo\task.artifacts\huge.log",
            Title: "huge.log",
            ModifiedUtc: DateTime.UtcNow,
            Body: null)
        {
            Kind = ArtifactKind.Text,
            SizeBytes = ArtifactCaps.InlineTextBytes + 1,
        };

        var imageAvailability = ArtifactShareFormatter.GetAvailability(image);
        var textAvailability = ArtifactShareFormatter.GetAvailability(overCapText);

        Assert.IsFalse(imageAvailability.CanCopyFormatted);
        Assert.IsFalse(imageAvailability.CanCopyMarkdown);
        Assert.IsTrue(imageAvailability.CanSaveCopy);
        Assert.IsTrue(imageAvailability.CanShowInFolder);
        Assert.IsFalse(textAvailability.CanCopyFormatted);
        Assert.IsFalse(textAvailability.CanCopyMarkdown);
        StringAssert.Contains(textAvailability.ContentUnavailableReason!, "too large");
    }

    [TestMethod]
    public async Task CopyFileAsync_PreservesArtifactBytes()
    {
        var source = Path.Combine(Path.GetTempPath(), "glasswork-share-source-" + Path.GetRandomFileName());
        var destination = Path.Combine(Path.GetTempPath(), "glasswork-share-destination-" + Path.GetRandomFileName());
        var bytes = new byte[] { 0, 1, 2, 3, 254, 255 };

        try
        {
            await File.WriteAllBytesAsync(source, bytes);

            await ArtifactShareFileCopier.CopyFileAsync(source, destination);

            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task CopyFileAsync_RejectsSameSourceAndDestinationWithoutTruncatingArtifact()
    {
        var source = Path.Combine(Path.GetTempPath(), "glasswork-share-source-" + Path.GetRandomFileName());
        var bytes = new byte[] { 0, 1, 2, 3, 254, 255 };

        try
        {
            await File.WriteAllBytesAsync(source, bytes);

            InvalidOperationException? exception = null;
            try
            {
                await ArtifactShareFileCopier.CopyFileAsync(source, source);
            }
            catch (InvalidOperationException ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
        }
    }
}
