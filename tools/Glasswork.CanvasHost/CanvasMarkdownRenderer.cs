using System.Net;
using System.Text;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;

namespace Glasswork.CanvasHost;

internal sealed class CanvasMarkdownRenderer(string vaultRoot)
{
    private readonly VaultMarkdownParser _parser = new(
        new FileSystemWikiLinkResolver(vaultRoot, Path.Combine("wiki", "todo")));

    public string Render(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        try
        {
            var output = new StringBuilder();
            foreach (var block in _parser.Parse(markdown)) RenderBlock(output, block);
            return output.ToString();
        }
        catch
        {
            return $"<p class=\"render-failed\">(render failed)</p><pre>{Encode(markdown)}</pre>";
        }
    }

    private static void RenderBlock(StringBuilder output, MarkdownBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var level = Math.Clamp(heading.Level, 1, 6);
                output.Append($"<h{level}>");
                RenderInlines(output, heading.Inlines);
                output.Append($"</h{level}>");
                break;
            case ParagraphBlock paragraph:
                output.Append("<p>");
                RenderInlines(output, paragraph.Inlines);
                output.Append("</p>");
                break;
            case ListBlock list:
                output.Append(list.Ordered ? "<ol>" : "<ul>");
                foreach (var item in list.Items)
                {
                    output.Append("<li>");
                    if (item.IsChecked is { } check)
                        output.Append(check ? "<span aria-label=\"completed\">☑ </span>" : "<span aria-label=\"not completed\">☐ </span>");
                    RenderInlines(output, item.Inlines);
                    output.Append("</li>");
                }
                output.Append(list.Ordered ? "</ol>" : "</ul>");
                break;
            case CodeBlockNode code:
                output.Append("<pre><code");
                if (!string.IsNullOrWhiteSpace(code.Language))
                    output.Append(" data-language=\"").Append(Attr(code.Language)).Append('"');
                output.Append('>').Append(Encode(code.Text)).Append("</code></pre>");
                break;
            case QuoteBlockNode quote:
                output.Append("<blockquote>");
                foreach (var child in quote.Children) RenderBlock(output, child);
                output.Append("</blockquote>");
                break;
            case CalloutBlock callout:
                output.Append("<aside class=\"callout ").Append(callout.Type.ToString().ToLowerInvariant()).Append("\"><strong>")
                    .Append(Encode(callout.Title ?? callout.Type.ToString())).Append("</strong>");
                foreach (var child in callout.Body) RenderBlock(output, child);
                output.Append("</aside>");
                break;
            case TableBlock table:
                output.Append("<div class=\"table-scroll\"><table><thead>");
                RenderRow(output, table.Header, true);
                output.Append("</thead><tbody>");
                foreach (var row in table.Body) RenderRow(output, row, false);
                output.Append("</tbody></table></div>");
                break;
            case ThematicBreakNode:
                output.Append("<hr>");
                break;
            case FallbackPlainTextNode fallback:
                output.Append("<p class=\"render-failed\">(render failed)</p><pre>")
                    .Append(Encode(fallback.Text)).Append("</pre>");
                break;
        }
    }

    private static void RenderRow(StringBuilder output, TableRow row, bool header)
    {
        output.Append("<tr>");
        var tag = header ? "th" : "td";
        foreach (var cell in row.Cells)
        {
            output.Append('<').Append(tag).Append('>');
            RenderInlines(output, cell.Inlines);
            output.Append("</").Append(tag).Append('>');
        }
        output.Append("</tr>");
    }

    private static void RenderInlines(StringBuilder output, IReadOnlyList<InlineSpan> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextSpan text: output.Append(Encode(text.Text)); break;
                case BoldSpan bold: output.Append("<strong>"); RenderInlines(output, bold.Inlines); output.Append("</strong>"); break;
                case ItalicSpan italic: output.Append("<em>"); RenderInlines(output, italic.Inlines); output.Append("</em>"); break;
                case StrikethroughSpan strike: output.Append("<s>"); RenderInlines(output, strike.Inlines); output.Append("</s>"); break;
                case CodeSpan code: output.Append("<code>").Append(Encode(code.Text)).Append("</code>"); break;
                case HardLineBreakSpan: output.Append("<br>"); break;
                case SoftLineBreakSpan: output.Append(' '); break;
                case ImagePlaceholderSpan image:
                    output.Append("<em>[image: ").Append(Encode(image.Alt)).Append("]</em>");
                    break;
                case LinkSpan link:
                    RenderPolicyLink(output, link.Href, link.Inlines);
                    break;
                case WikiLinkSpan wiki:
                    RenderWikiLink(output, wiki);
                    break;
            }
        }
    }

    private static void RenderPolicyLink(StringBuilder output, string href, IReadOnlyList<InlineSpan> label)
    {
        if (ArtifactLinkPolicy.Decide(href) != ArtifactLinkPolicy.Decision.Allow)
        {
            output.Append("<span class=\"blocked-link\">");
            RenderInlines(output, label);
            output.Append("</span>");
            return;
        }
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme.Equals("glasswork", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            output.Append("<button class=\"inline-link\" data-task-id=\"").Append(Attr(uri.AbsolutePath.Trim('/'))).Append("\">");
        }
        else
        {
            output.Append("<button class=\"inline-link\" data-external-url=\"").Append(Attr(href)).Append("\">");
        }
        RenderInlines(output, label);
        output.Append("</button>");
    }

    private static void RenderWikiLink(StringBuilder output, WikiLinkSpan wiki)
    {
        var label = wiki.Display ?? wiki.Stem;
        switch (wiki.Resolution)
        {
            case WikiLinkResolution.Task task:
                output.Append("<button class=\"inline-link\" data-task-id=\"").Append(Attr(task.TaskId)).Append("\">")
                    .Append(Encode(label)).Append("</button>");
                break;
            case WikiLinkResolution.VaultPage page:
                output.Append("<button class=\"inline-link\" data-vault-path=\"").Append(Attr(page.VaultRelativePath)).Append("\">")
                    .Append(Encode(label)).Append("</button>");
                break;
            default:
                output.Append("<span class=\"unresolved-link\">").Append(Encode(label)).Append("</span>");
                break;
        }
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Attr(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
