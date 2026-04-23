using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigBlock = Markdig.Syntax.Block;
using MarkdigDocument = Markdig.Syntax.MarkdownDocument;

namespace Glasswork.Core.Markdown;

/// <summary>
/// Pure parser that converts markdown source to Glasswork's typed block AST.
/// Markdig pipeline preserves <c>UseAutoLinks()</c> for parity with the prior
/// <c>MarkdownTextBlock</c> implementation. Parse exceptions are caught and
/// returned as a single <see cref="FallbackPlainTextNode"/>; the renderer is
/// responsible for the visual "(render failed)" caption.
/// </summary>
public sealed class VaultMarkdownParser
{
    private static readonly MarkdownPipeline DefaultPipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .Build();

    private readonly Func<string, MarkdigDocument> _parse;

    public VaultMarkdownParser()
        : this(static md => Markdig.Markdown.Parse(md, DefaultPipeline))
    {
    }

    /// <summary>
    /// Test seam. Allows injecting a parse function that throws so the
    /// fallback path can be deterministically exercised.
    /// </summary>
    public VaultMarkdownParser(Func<string, MarkdigDocument> parse)
    {
        _parse = parse ?? throw new ArgumentNullException(nameof(parse));
    }

    public IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<MarkdownBlock>();
        }

        MarkdigDocument doc;
        try
        {
            doc = _parse(markdown);
        }
        catch
        {
            return new MarkdownBlock[] { new FallbackPlainTextNode(markdown) };
        }

        var blocks = new List<MarkdownBlock>();
        foreach (var node in doc)
        {
            var block = ConvertBlock(node);
            if (block is not null)
            {
                blocks.Add(block);
            }
        }
        return blocks;
    }

    private static MarkdownBlock? ConvertBlock(MarkdigBlock node) => node switch
    {
        Markdig.Syntax.HeadingBlock h => new HeadingBlock(h.Level, ConvertInlines(h.Inline)),
        Markdig.Syntax.ParagraphBlock p => new ParagraphBlock(ConvertInlines(p.Inline)),
        Markdig.Syntax.ListBlock list => ConvertList(list),
        FencedCodeBlock fenced => new CodeBlockNode(JoinLines(fenced), NullIfEmpty(fenced.Info)),
        Markdig.Syntax.CodeBlock code => new CodeBlockNode(JoinLines(code), null),
        ThematicBreakBlock => new ThematicBreakNode(),
        QuoteBlock quote => new QuoteBlockNode(ConvertChildren(quote)),
        _ => null,
    };

    private static ListBlock ConvertList(Markdig.Syntax.ListBlock list)
    {
        var items = new List<ListItemBlock>();
        foreach (var item in list)
        {
            if (item is not Markdig.Syntax.ListItemBlock li) continue;
            var inlines = new List<InlineSpan>();
            foreach (var child in li)
            {
                if (child is Markdig.Syntax.ParagraphBlock cp)
                {
                    foreach (var span in ConvertInlines(cp.Inline))
                    {
                        inlines.Add(span);
                    }
                }
            }
            items.Add(new ListItemBlock(inlines));
        }
        return new ListBlock(list.IsOrdered, items);
    }

    private static IReadOnlyList<MarkdownBlock> ConvertChildren(ContainerBlock container)
    {
        var children = new List<MarkdownBlock>();
        foreach (var child in container)
        {
            var converted = ConvertBlock(child);
            if (converted is not null)
            {
                children.Add(converted);
            }
        }
        return children;
    }

    private static IReadOnlyList<InlineSpan> ConvertInlines(ContainerInline? container)
    {
        if (container is null) return Array.Empty<InlineSpan>();
        var spans = new List<InlineSpan>();
        foreach (var inline in container)
        {
            var converted = ConvertInline(inline);
            if (converted is not null)
            {
                spans.Add(converted);
            }
        }
        return spans;
    }

    private static InlineSpan? ConvertInline(Markdig.Syntax.Inlines.Inline inline) => inline switch
    {
        LiteralInline lit => new TextSpan(lit.Content.ToString()),
        EmphasisInline em => em.DelimiterCount >= 2
            ? new BoldSpan(ConvertInlines(em))
            : new ItalicSpan(ConvertInlines(em)),
        CodeInline code => new CodeSpan(code.Content),
        LinkInline { IsImage: true } img => new ImagePlaceholderSpan(ExtractAlt(img)),
        LinkInline link => new LinkSpan(link.Url ?? string.Empty, ConvertInlines(link)),
        AutolinkInline auto => new LinkSpan(auto.Url ?? string.Empty,
            new InlineSpan[] { new TextSpan(auto.Url ?? string.Empty) }),
        LineBreakInline { IsHard: true } => new HardLineBreakSpan(),
        LineBreakInline => new SoftLineBreakSpan(),
        ContainerInline c => new TextSpan(ExtractText(c)),
        _ => null,
    };

    private static string ExtractAlt(LinkInline image)
    {
        if (!string.IsNullOrEmpty(image.Title)) return image.Title!;
        if (image.FirstChild is LiteralInline lit) return lit.Content.ToString();
        return string.Empty;
    }

    private static string ExtractText(ContainerInline container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case ContainerInline c: sb.Append(ExtractText(c)); break;
            }
        }
        return sb.ToString();
    }

    private static string JoinLines(LeafBlock block)
    {
        var sb = new System.Text.StringBuilder();
        var lines = block.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines.Lines[i].ToString());
        }
        return sb.ToString();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
