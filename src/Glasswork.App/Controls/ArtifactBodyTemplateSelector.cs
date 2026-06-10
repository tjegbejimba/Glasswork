using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

/// <summary>
/// Selects the per-kind body template for an artifact row inside the
/// artifacts <c>Expander</c>. Selection is driven entirely by the explicit
/// flags on <see cref="ArtifactRow"/> (never a raw <c>Body == null</c> check —
/// <see cref="ArtifactRow.Body"/> coalesces null to ""). Anything that cannot
/// render inline — load errors, <see cref="ArtifactKind.Other"/>, over-cap
/// markdown/text/image — falls back to the by-reference card.
/// </summary>
public partial class ArtifactBodyTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MarkdownTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? HtmlTemplate { get; set; }
    public DataTemplate? ReferenceTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is not ArtifactRow row)
        {
            return ReferenceTemplate!;
        }

        if (row.IsReference)
        {
            return ReferenceTemplate!;
        }

        return row.Kind switch
        {
            ArtifactKind.Markdown => row.ShouldRenderInlineMarkdown ? MarkdownTemplate! : ReferenceTemplate!,
            ArtifactKind.Text => row.ShouldRenderInlineText ? TextTemplate! : ReferenceTemplate!,
            ArtifactKind.Image => ImageTemplate!,
            ArtifactKind.Html => HtmlTemplate!,
            _ => ReferenceTemplate!,
        };
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
