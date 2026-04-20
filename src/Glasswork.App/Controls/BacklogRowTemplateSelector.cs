using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

/// <summary>
/// Routes Backlog row items to the appropriate template:
/// - <see cref="BacklogParentGroupHeader"/> → <see cref="GroupHeaderTemplate"/>
/// - <see cref="GlassworkTask"/> → <see cref="TaskTemplate"/>
/// </summary>
public partial class BacklogRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TaskTemplate { get; set; }
    public DataTemplate? GroupHeaderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item is BacklogParentGroupHeader
            ? GroupHeaderTemplate!
            : TaskTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
