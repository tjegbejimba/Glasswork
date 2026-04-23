using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Core.Markdown;

namespace Glasswork.Tests;

[TestClass]
public class VaultMarkdownParserTests
{
    private static VaultMarkdownParser NewParser() => new();

    [TestMethod]
    public void Empty_ReturnsZeroBlocks()
    {
        Assert.AreEqual(0, NewParser().Parse(string.Empty).Count);
        Assert.AreEqual(0, NewParser().Parse(null!).Count);
    }

    [TestMethod]
    public void SingleHeading_ProducesHeadingBlockWithCorrectLevel()
    {
        var blocks = NewParser().Parse("## Hello");
        Assert.AreEqual(1, blocks.Count);
        var heading = (HeadingBlock)blocks[0];
        Assert.AreEqual(2, heading.Level);
        Assert.AreEqual("Hello", ((TextSpan)heading.Inlines[0]).Text);
    }

    [TestMethod]
    public void MixedDocument_ProducesExpectedBlockSequence()
    {
        var md = string.Join('\n',
            "# Title",
            "",
            "Para text.",
            "",
            "- a",
            "- b",
            "",
            "1. one",
            "2. two",
            "",
            "```",
            "code",
            "```",
            "",
            "> quoted",
            "",
            "---");
        var blocks = NewParser().Parse(md);

        // Expect: heading, paragraph, unordered list, ordered list, code, quote, hr
        Assert.AreEqual(7, blocks.Count);
        Assert.IsInstanceOfType(blocks[0], typeof(HeadingBlock));
        Assert.IsInstanceOfType(blocks[1], typeof(ParagraphBlock));
        var ul = (ListBlock)blocks[2];
        Assert.IsFalse(ul.Ordered);
        Assert.AreEqual(2, ul.Items.Count);
        var ol = (ListBlock)blocks[3];
        Assert.IsTrue(ol.Ordered);
        Assert.AreEqual(2, ol.Items.Count);
        Assert.IsInstanceOfType(blocks[4], typeof(CodeBlockNode));
        Assert.IsInstanceOfType(blocks[5], typeof(QuoteBlockNode));
        Assert.IsInstanceOfType(blocks[6], typeof(ThematicBreakNode));
    }

    [TestMethod]
    public void InlineLink_PreservesRawHrefWithoutPolicyDecision()
    {
        var blocks = NewParser().Parse("See [docs](https://example.com/x).");
        var para = (ParagraphBlock)blocks[0];
        var link = para.Inlines.OfType<LinkSpan>().Single();
        Assert.AreEqual("https://example.com/x", link.Href);
        Assert.AreEqual("docs", ((TextSpan)link.Inlines[0]).Text);
    }

    [TestMethod]
    public void Autolink_ProducesLinkSpan()
    {
        var blocks = NewParser().Parse("Visit https://example.com today.");
        var para = (ParagraphBlock)blocks[0];
        var link = para.Inlines.OfType<LinkSpan>().Single();
        Assert.AreEqual("https://example.com", link.Href);
    }

    [TestMethod]
    public void BlockedSchemeUrl_StillParsedAsLinkSpan_PolicyAppliedByRenderer()
    {
        // Parser is syntax-only. file:// will be blocked by ArtifactLinkPolicy
        // in the renderer, but it must still appear here as a LinkSpan.
        var blocks = NewParser().Parse("[bad](file:///etc/passwd)");
        var link = ((ParagraphBlock)blocks[0]).Inlines.OfType<LinkSpan>().Single();
        Assert.AreEqual("file:///etc/passwd", link.Href);
    }

    [TestMethod]
    public void Image_ProducesPlaceholderWithAltText()
    {
        var blocks = NewParser().Parse("![cover](https://example.com/x.png)");
        var para = (ParagraphBlock)blocks[0];
        var image = para.Inlines.OfType<ImagePlaceholderSpan>().Single();
        Assert.AreEqual("cover", image.Alt);
    }

    [TestMethod]
    public void EmphasisAndCode_ProduceCorrectInlineTree()
    {
        var blocks = NewParser().Parse("**bold** and *italic* and `code`");
        var inlines = ((ParagraphBlock)blocks[0]).Inlines;
        Assert.IsTrue(inlines.OfType<BoldSpan>().Any());
        Assert.IsTrue(inlines.OfType<ItalicSpan>().Any());
        Assert.IsTrue(inlines.OfType<CodeSpan>().Any());
        var code = inlines.OfType<CodeSpan>().Single();
        Assert.AreEqual("code", code.Text);
    }

    [TestMethod]
    public void ParseException_ProducesSingleFallbackPlainTextNode()
    {
        var thrower = new VaultMarkdownParser(_ => throw new InvalidOperationException("forced"));
        var blocks = thrower.Parse("# would have been a heading");
        Assert.AreEqual(1, blocks.Count);
        var fallback = (FallbackPlainTextNode)blocks[0];
        Assert.AreEqual("# would have been a heading", fallback.Text);
    }

    [TestMethod]
    public void Blockquote_ContainsNestedParagraph()
    {
        var blocks = NewParser().Parse("> hello\n> world");
        var quote = (QuoteBlockNode)blocks[0];
        Assert.AreEqual(1, quote.Children.Count);
        Assert.IsInstanceOfType(quote.Children[0], typeof(ParagraphBlock));
    }

    [TestMethod]
    public void FencedCodeBlock_PreservesLanguageInfo()
    {
        var blocks = NewParser().Parse("```csharp\nvar x = 1;\n```");
        var code = (CodeBlockNode)blocks[0];
        Assert.AreEqual("csharp", code.Language);
        Assert.AreEqual("var x = 1;", code.Text);
    }

    // ---- M3 #74: wiki-links ----

    private static IReadOnlyList<InlineSpan> ParaInlines(IReadOnlyList<MarkdownBlock> blocks) =>
        ((ParagraphBlock)blocks[0]).Inlines;

    private sealed class StubResolver(System.Func<string, WikiLinkResolution> map) : IWikiLinkResolver
    {
        public WikiLinkResolution Resolve(string stem) => map(stem);
    }

    [TestMethod]
    public void WikiLink_BareStem_NoResolver_IsUnresolved()
    {
        var inlines = ParaInlines(NewParser().Parse("[[foo]]"));
        Assert.AreEqual(1, inlines.Count);
        var link = (WikiLinkSpan)inlines[0];
        Assert.AreEqual("foo", link.Stem);
        Assert.IsNull(link.Display);
        Assert.IsInstanceOfType(link.Resolution, typeof(WikiLinkResolution.Unresolved));
    }

    [TestMethod]
    public void WikiLink_WithDisplay_PreservesDisplayText()
    {
        var inlines = ParaInlines(NewParser().Parse("[[foo|click here]]"));
        var link = (WikiLinkSpan)inlines[0];
        Assert.AreEqual("foo", link.Stem);
        Assert.AreEqual("click here", link.Display);
    }

    [TestMethod]
    public void WikiLink_MixedWithText_ProducesTextThenLinkThenText()
    {
        var inlines = ParaInlines(NewParser().Parse("see [[foo]] for context"));
        Assert.AreEqual(3, inlines.Count);
        Assert.AreEqual("see ", ((TextSpan)inlines[0]).Text);
        Assert.AreEqual("foo", ((WikiLinkSpan)inlines[1]).Stem);
        Assert.AreEqual(" for context", ((TextSpan)inlines[2]).Text);
    }

    [TestMethod]
    public void WikiLink_MultipleInOneParagraph_EachResolvedIndependently()
    {
        var resolver = new StubResolver(s => s switch
        {
            "TASK-1" => new WikiLinkResolution.Task("TASK-1"),
            "concept" => new WikiLinkResolution.VaultPage("wiki/concepts/concept.md"),
            _ => WikiLinkResolution.Unresolved.Instance,
        });
        var parser = new VaultMarkdownParser(resolver);
        var inlines = ParaInlines(parser.Parse("[[TASK-1]] and [[concept]] and [[gone]]"));
        Assert.IsInstanceOfType(((WikiLinkSpan)inlines[0]).Resolution, typeof(WikiLinkResolution.Task));
        Assert.IsInstanceOfType(((WikiLinkSpan)inlines[2]).Resolution, typeof(WikiLinkResolution.VaultPage));
        Assert.IsInstanceOfType(((WikiLinkSpan)inlines[4]).Resolution, typeof(WikiLinkResolution.Unresolved));
    }

    [TestMethod]
    public void WikiLink_EmptyStem_LeftAsLiteralText()
    {
        var inlines = ParaInlines(NewParser().Parse("[[ ]]"));
        Assert.AreEqual(1, inlines.Count);
        Assert.IsInstanceOfType(inlines[0], typeof(TextSpan));
    }

    [TestMethod]
    public void WikiLink_InsideEmphasis_StillParsedAsWikiLink()
    {
        var inlines = ParaInlines(NewParser().Parse("*[[foo]]*"));
        var italic = (ItalicSpan)inlines[0];
        Assert.AreEqual(1, italic.Inlines.Count);
        Assert.IsInstanceOfType(italic.Inlines[0], typeof(WikiLinkSpan));
    }

    [TestMethod]
    public void WikiLink_InsideInlineCode_NotParsed()
    {
        var inlines = ParaInlines(NewParser().Parse("see `[[foo]]` literal"));
        var code = (CodeSpan)inlines[1];
        Assert.AreEqual("[[foo]]", code.Text);
    }
}
