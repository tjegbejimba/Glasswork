using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glasswork.Converters;

/// <summary>
/// Bool → SolidColorBrush converter that highlights the My Day toggle when active.
/// True returns a sunny gold matching the sun glyph; false returns a muted gray to
/// indicate the toggle is available but inactive.
///
/// Lives in <c>Glasswork.Converters</c> so any Page can declare it as a resource.
/// Used today by TaskDetailPage subtask suns and Backlog list/board card suns.
/// </summary>
public sealed class MyDayBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
        {
            // Active: sunny gold to match the sun glyph.
            return new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
        }
        return new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
