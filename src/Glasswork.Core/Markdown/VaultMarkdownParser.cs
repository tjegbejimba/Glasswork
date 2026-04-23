using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
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
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    private readonly Func<string, MarkdigDocument> _parse;
    private readonly IWikiLinkResolver? _resolver;

    public VaultMarkdownParser()
        : this(static md => Markdig.Markdown.Parse(md, DefaultPipeline), resolver: null)
    {
    }

    public VaultMarkdownParser(IWikiLinkResolver? resolver)
        : this(static md => Markdig.Markdown.Parse(md, DefaultPipeline), resolver)
    {
    }

    /// <summary>
    /// Test seam. Allows injecting a parse function that throws so the
    /// fallback path can be deterministically exercised.
    /// </summary>
    public VaultMarkdownParser(Func<string, MarkdigDocument> parse)
        : this(parse, resolver: null)
    {
    }

    public VaultMarkdownParser(Func<string, MarkdigDocument> parse, IWikiLinkResolver? resolver)
    {
        _parse = parse ?? throw new ArgumentNullException(nameof(parse));
        _resolver = resolver;
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

    private MarkdownBlock? ConvertBlock(MarkdigBlock node) => node switch
    {
        Markdig.Syntax.HeadingBlock h => new HeadingBlock(h.Level, ConvertInlines(h.Inline)),
        Markdig.Extensions.Tables.Table table => ConvertTable(table),
        Markdig.Syntax.ParagraphBlock p => new ParagraphBlock(ConvertInlines(p.Inline)),
        Markdig.Syntax.ListBlock list => ConvertList(list),
        FencedCodeBlock fenced => new CodeBlockNode(JoinLines(fenced), NullIfEmpty(fenced.Info)),
        Markdig.Syntax.CodeBlock code => new CodeBlockNode(JoinLines(code), null),
        ThematicBreakBlock => new ThematicBreakNode(),
        QuoteBlock quote => new QuoteBlockNode(ConvertChildren(quote)),
        _ => null,
    };

    private TableBlock ConvertTable(Markdig.Extensions.Tables.Table table)
    {
        TableRow header = new(Array.Empty<TableCell>());
        var body = new List<TableRow>();
        foreach (var rowBlock in table)
        {
            if (rowBlock is not Markdig.Extensions.Tables.TableRow row) continue;
            var cells = new List<TableCell>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is not Markdig.Extensions.Tables.TableCell cell) continue;
                cells.Add(new TableCell(ExtractCellInlines(cell)));
            }
            var converted = new TableRow(cells);
            if (row.IsHeader) header = converted;
            else body.Add(converted);
        }

        // Defensive: if Markdig produced no header row, use first body row.
        if (header.Cells.Count == 0 && body.Count > 0)
        {
            header = body[0];
            body = body.GetRange(1, body.Count - 1);
        }

        // Markdig's pipe-table extension can emit a phantom trailing column
        // for the closing "|". Trim ColumnDefinitions to the actual cell count
        // so renderers don't lay out an empty extra column.
        int actualCount = header.Cells.Count;
        foreach (var r in body) if (r.Cells.Count > actualCount) actualCount = r.Cells.Count;

        var columns = new List<TableColumn>();
        for (int i = 0; i < actualCount; i++)
        {
            var align = i < table.ColumnDefinitions.Count
                ? MapAlignment(table.ColumnDefinitions[i].Alignment)
                : TableAlignment.Default;
            columns.Add(new TableColumn(align));
        }

        return new TableBlock(columns, header, body);
    }

    private IReadOnlyList<InlineSpan> ExtractCellInlines(Markdig.Extensions.Tables.TableCell cell)
    {
        var inlines = new List<InlineSpan>();
        foreach (var child in cell)
        {
            if (child is Markdig.Syntax.ParagraphBlock cp)
            {
                foreach (var span in ConvertInlines(cp.Inline)) inlines.Add(span);
            }
        }
        return inlines;
    }

    private static TableAlignment MapAlignment(Markdig.Extensions.Tables.TableColumnAlign? align) => align switch
    {
        Markdig.Extensions.Tables.TableColumnAlign.Left => TableAlignment.Left,
        Markdig.Extensions.Tables.TableColumnAlign.Center => TableAlignment.Center,
        Markdig.Extensions.Tables.TableColumnAlign.Right => TableAlignment.Right,
        _ => TableAlignment.Default,
    };

    private ListBlock ConvertList(Markdig.Syntax.ListBlock list)
    {
        var items = new List<ListItemBlock>();
        foreach (var item in list)
        {
            if (item is not Markdig.Syntax.ListItemBlock li) continue;
            var inlines = new List<InlineSpan>();
            bool? isChecked = null;
            foreach (var child in li)
            {
                if (child is Markdig.Syntax.ParagraphBlock cp)
                {
                    if (cp.Inline is not null)
                    {
                        foreach (var raw in cp.Inline)
                        {
                            if (raw is Markdig.Extensions.TaskLists.TaskList tl)
                            {
                                isChecked = tl.Checked;
                            }
                        }
                    }
                    foreach (var span in ConvertInlines(cp.Inline))
                    {
                        inlines.Add(span);
                    }
                }
            }
            items.Add(new ListItemBlock(inlines, isChecked));
        }
        return new ListBlock(list.IsOrdered, items);
    }

    private IReadOnlyList<MarkdownBlock> ConvertChildren(ContainerBlock container)
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

    private IReadOnlyList<InlineSpan> ConvertInlines(ContainerInline? container)
    {
        if (container is null) return Array.Empty<InlineSpan>();
        var spans = new List<InlineSpan>();
        var literalBuffer = new System.Text.StringBuilder();
        FlattenInto(container, spans, literalBuffer);
        FlushLiteralBuffer(literalBuffer, spans);
        return spans;
    }

    private void FlattenInto(
        ContainerInline container,
        List<InlineSpan> spans,
        System.Text.StringBuilder literalBuffer)
    {
        foreach (var inline in container)
        {
            if (inline is LiteralInline lit)
            {
                literalBuffer.Append(lit.Content.ToString());
                continue;
            }
            // Markdig's pipe-tables extension wraps the rest of a paragraph's
            // inlines under a PipeTableDelimiterInline (a ContainerInline)
            // when it sees a stray "|" outside of a table row. That breaks
            // wiki-link parsing for "[[stem|display]]". Treat it as a literal
            // pipe and recurse so the buffer keeps assembling.
            if (inline is Markdig.Extensions.Tables.PipeTableDelimiterInline delim)
            {
                literalBuffer.Append('|');
                FlattenInto(delim, spans, literalBuffer);
                continue;
            }
            FlushLiteralBuffer(literalBuffer, spans);
            ConvertNonLiteralInto(inline, spans);
        }
    }

    private void FlushLiteralBuffer(System.Text.StringBuilder buffer, List<InlineSpan> sink)
    {
        if (buffer.Length == 0) return;
        ExpandLiteralWithWikiLinks(buffer.ToString(), sink);
        buffer.Clear();
    }

    private void ConvertNonLiteralInto(Markdig.Syntax.Inlines.Inline inline, List<InlineSpan> sink)
    {
        switch (inline)
        {
            case EmphasisInline em when em.DelimiterChar == '~':
                sink.Add(new StrikethroughSpan(ConvertInlines(em)));
                return;
            case EmphasisInline em:
                sink.Add(em.DelimiterCount >= 2
                    ? new BoldSpan(ConvertInlines(em))
                    : new ItalicSpan(ConvertInlines(em)));
                return;
            case CodeInline code:
                sink.Add(new CodeSpan(code.Content));
                return;
            case LinkInline { IsImage: true } img:
                sink.Add(new ImagePlaceholderSpan(ExtractAlt(img)));
                return;
            case LinkInline link:
                sink.Add(new LinkSpan(link.Url ?? string.Empty, ConvertInlines(link)));
                return;
            case AutolinkInline auto:
                sink.Add(new LinkSpan(auto.Url ?? string.Empty,
                    new InlineSpan[] { new TextSpan(auto.Url ?? string.Empty) }));
                return;
            case LineBreakInline { IsHard: true }:
                sink.Add(new HardLineBreakSpan());
                return;
            case LineBreakInline:
                sink.Add(new SoftLineBreakSpan());
                return;
            case Markdig.Extensions.TaskLists.TaskList:
                // Captured at the list-item level; suppress to avoid leaking
                // a stray "[x]" into the rendered inline stream.
                return;
            case ContainerInline c:
                sink.Add(new TextSpan(ExtractText(c)));
                return;
        }
    }

    private void ExpandLiteralWithWikiLinks(string text, List<InlineSpan> sink)
    {
        var matches = WikiLinkParser.Find(text);
        if (matches.Count == 0)
        {
            if (text.Length > 0) sink.Add(new TextSpan(text));
            return;
        }
        int cursor = 0;
        foreach (var m in matches)
        {
            if (m.Index > cursor)
            {
                sink.Add(new TextSpan(text.Substring(cursor, m.Index - cursor)));
            }
            var resolution = _resolver?.Resolve(m.Stem) ?? WikiLinkResolution.Unresolved.Instance;
            sink.Add(new WikiLinkSpan(m.Stem, m.Display, resolution));
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
        {
            sink.Add(new TextSpan(text.Substring(cursor)));
        }
    }

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
