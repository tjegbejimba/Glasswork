using System;
using System.Diagnostics;
using Glasswork.Core.Models;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;
using MarkdigBlock = Markdig.Syntax.Block;

namespace Glasswork.Controls;

/// <summary>
/// Attached property for rendering markdown into a <see cref="RichTextBlock"/>.
/// Treats input as untrusted (artifact bodies are agent-produced):
/// - Hyperlinks are filtered through <see cref="ArtifactLinkPolicy"/>;
///   blocked URLs render as plain non-clickable text.
/// - Images are not auto-loaded; they render as "[image: alt]" placeholders.
/// - Malformed markdown falls back to a single plain-text paragraph.
///
/// Usage in XAML:
///   &lt;RichTextBlock controls:MarkdownTextBlock.Source="{Binding Body}" /&gt;
/// </summary>
public static class MarkdownTextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(string.Empty, OnSourceChanged));

    public static string GetSource(DependencyObject obj) => (string)obj.GetValue(SourceProperty);
    public static void SetSource(DependencyObject obj, string value) => obj.SetValue(SourceProperty, value);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .Build();

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBlock rtb)
        {
            Render(rtb, (string?)e.NewValue ?? string.Empty);
        }
    }

    private static void Render(RichTextBlock rtb, string markdown)
    {
        rtb.Blocks.Clear();
        if (string.IsNullOrEmpty(markdown)) return;

        try
        {
            var doc = Markdig.Markdown.Parse(markdown, Pipeline);
            foreach (var node in doc)
            {
                AppendBlock(rtb, node);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MarkdownTextBlock: render failed — falling back to plain text. {ex.Message}");
            rtb.Blocks.Clear();
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = markdown });
            rtb.Blocks.Add(p);
        }
    }

    private static void AppendBlock(RichTextBlock rtb, MarkdigBlock node)
    {
        switch (node)
        {
            case HeadingBlock h:
                rtb.Blocks.Add(BuildHeading(h));
                break;
            case ParagraphBlock p:
                rtb.Blocks.Add(BuildParagraph(p.Inline));
                break;
            case ListBlock list:
                AppendList(rtb, list);
                break;
            case CodeBlock code:
                rtb.Blocks.Add(BuildCodeBlock(code));
                break;
            case ThematicBreakBlock:
                var sep = new Paragraph();
                sep.Inlines.Add(new Run
                {
                    Text = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                });
                rtb.Blocks.Add(sep);
                break;
            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    if (child is ParagraphBlock qp)
                    {
                        var qpara = BuildParagraph(qp.Inline);
                        qpara.Margin = new Thickness(16, 0, 0, 0);
                        qpara.FontStyle = Windows.UI.Text.FontStyle.Italic;
                        rtb.Blocks.Add(qpara);
                    }
                }
                break;
        }
    }

    private static Paragraph BuildHeading(HeadingBlock h)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 4),
        };
        var run = new Run
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = h.Level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 14,
            },
            Text = ExtractText(h.Inline),
        };
        p.Inlines.Add(run);
        return p;
    }

    private static Paragraph BuildParagraph(ContainerInline? inline)
    {
        var p = new Paragraph();
        AppendInlines(inline, p.Inlines);
        return p;
    }

    private static void AppendList(RichTextBlock rtb, ListBlock list)
    {
        var ordered = list.IsOrdered;
        int index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock li) continue;
            var bullet = ordered ? $"{index}. " : "\u2022 ";
            var p = new Paragraph { Margin = new Thickness(8, 0, 0, 0) };
            p.Inlines.Add(new Run { Text = bullet });
            foreach (var child in li)
            {
                if (child is ParagraphBlock cp)
                {
                    AppendInlines(cp.Inline, p.Inlines);
                }
            }
            rtb.Blocks.Add(p);
            index++;
        }
    }

    private static Paragraph BuildCodeBlock(CodeBlock code)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
        };
        var text = string.Join("\n", code.Lines);
        p.Inlines.Add(new Run
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
        });
        return p;
    }

    private static void AppendInlines(ContainerInline? container, InlineCollection sink)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sink.Add(new Run { Text = lit.Content.ToString() });
                    break;
                case EmphasisInline em:
                    Span span = em.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AppendInlines(em, span.Inlines);
                    sink.Add(span);
                    break;
                case CodeInline code:
                    sink.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new FontFamily("Consolas"),
                    });
                    break;
                case LinkInline { IsImage: true } img:
                    var alt = img.Title ?? string.Empty;
                    if (string.IsNullOrEmpty(alt))
                    {
                        var firstLit = img.FirstChild as LiteralInline;
                        alt = firstLit?.Content.ToString() ?? string.Empty;
                    }
                    sink.Add(new Run
                    {
                        Text = $"[image: {alt}]",
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    });
                    break;
                case LinkInline link:
                    var url = link.Url ?? string.Empty;
                    var label = ExtractLinkLabel(link, url);
                    if (ArtifactLinkPolicy.Decide(url) == ArtifactLinkPolicy.Decision.Allow
                        && Uri.TryCreate(url, UriKind.Absolute, out var navUri))
                    {
                        var hl = new Hyperlink { NavigateUri = navUri };
                        hl.Inlines.Add(new Run { Text = label });
                        sink.Add(hl);
                    }
                    else
                    {
                        sink.Add(new Run
                        {
                            Text = label,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                        });
                    }
                    break;
                case AutolinkInline auto:
                    var aurl = auto.Url ?? string.Empty;
                    if (ArtifactLinkPolicy.Decide(aurl) == ArtifactLinkPolicy.Decision.Allow
                        && Uri.TryCreate(aurl, UriKind.Absolute, out var aUri))
                    {
                        var hl = new Hyperlink { NavigateUri = aUri };
                        hl.Inlines.Add(new Run { Text = aurl });
                        sink.Add(hl);
                    }
                    else
                    {
                        sink.Add(new Run { Text = aurl });
                    }
                    break;
                case LineBreakInline lb:
                    sink.Add(lb.IsHard ? (XamlInline)new LineBreak() : new Run { Text = " " });
                    break;
                case ContainerInline c:
                    AppendInlines(c, sink);
                    break;
            }
        }
    }

    private static string ExtractText(ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        ExtractInto(container, sb);
        return sb.ToString();
    }

    private static void ExtractInto(ContainerInline container, System.Text.StringBuilder sb)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline c: ExtractInto(c, sb); break;
            }
        }
    }

    private static string ExtractLinkLabel(LinkInline link, string fallback)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in link)
        {
            if (child is LiteralInline lit) sb.Append(lit.Content.ToString());
            else if (child is CodeInline code) sb.Append(code.Content);
        }
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
