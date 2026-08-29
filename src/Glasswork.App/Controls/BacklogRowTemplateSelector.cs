using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

/// <summary>
/// Routes Backlog row items to the appropriate template:
/// - <see cref="BacklogParentGroupHeader"/> → <see cref="GroupHeaderTemplate"/>
/// - <see cref="BacklogHierarchyRow"/> → <see cref="HierarchyTemplate"/>
/// - <see cref="GlassworkTask"/> → <see cref="TaskTemplate"/>
/// </summary>
public partial class BacklogRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TaskTemplate { get; set; }
    public DataTemplate? GroupHeaderTemplate { get; set; }
    public DataTemplate? HierarchyTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item switch
        {
            BacklogParentGroupHeader => GroupHeaderTemplate!,
            BacklogHierarchyRow => HierarchyTemplate!,
            _ => TaskTemplate!,
        };
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
