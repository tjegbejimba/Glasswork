using System;
using Glasswork.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glasswork.Converters;

/// <summary>
/// Maps a <see cref="SubTask"/>'s effective status to the segment brush used by the
/// segmented progress bar in the adaptive task row. Done / dropped / blocked / in_progress
/// each get a distinct color; todo renders as a muted outline.
/// </summary>
public sealed class SubtaskSegmentBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not SubTask s) return Muted();

        if (s.Status == "dropped") return Brush(0x8A, 0x88, 0x86);
        if (s.IsEffectivelyDone) return Brush(0x16, 0xA3, 0x4A);
        if (s.Status == "in_progress") return AccentLight();
        if (s.Status == "blocked") return Brush(0xC5, 0x0F, 0x1F);
        return Muted();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    private static SolidColorBrush Accent()
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color color)
            return new SolidColorBrush(color);
        return Brush(0x00, 0x78, 0xD4);
    }

    private static SolidColorBrush AccentLight()
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color color)
            return new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B));
        return new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x78, 0xD4));
    }

    private static SolidColorBrush Muted() => new(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromArgb(0xFF, r, g, b));
}

/// <summary>
/// Maps <see cref="DueUrgency"/> to the chip background brush used in the adaptive task row.
/// </summary>
public sealed class DueUrgencyBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DueUrgency u) return Neutral();
        return u switch
        {
            DueUrgency.Overdue => new SolidColorBrush(Color.FromArgb(0x33, 0xC5, 0x0F, 0x1F)),
            DueUrgency.Today => new SolidColorBrush(Color.FromArgb(0x33, 0xCA, 0x5C, 0x00)),
            DueUrgency.Soon => Accent(0x33),
            DueUrgency.Future => Neutral(),
            _ => Neutral(),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    private static SolidColorBrush Accent(byte alpha)
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color color)
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        return new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0x78, 0xD4));
    }

    private static SolidColorBrush Neutral() => new(Color.FromArgb(0x22, 0x80, 0x80, 0x80));
}

/// <summary>
/// Maps <see cref="DueUrgency"/> to the chip foreground brush.
/// </summary>
public sealed class DueUrgencyForegroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DueUrgency u) return new SolidColorBrush(Colors.Gray);
        return u switch
        {
            DueUrgency.Overdue => new SolidColorBrush(Color.FromArgb(0xFF, 0xC5, 0x0F, 0x1F)),
            DueUrgency.Today => new SolidColorBrush(Color.FromArgb(0xFF, 0xCA, 0x5C, 0x00)),
            _ => new SolidColorBrush(Color.FromArgb(0xCC, 0x60, 0x60, 0x60)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Bool → Visibility (true = Visible).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}


/// <summary>
/// Maps a bool 'IsCollapsed' to a Segoe Fluent Icons chevron glyph: right when collapsed, down when expanded.
/// </summary>
public sealed class CollapseChevronGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "" : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
/// <summary>
/// Builds a deterministic AutomationId for an artifact-row element by prefixing
/// the artifact Title (its filename) with the ConverterParameter. Visual
/// verification uses this to target a specific artifact's Expander
/// (parameter "row:") or HTML Preview button (parameter "preview:") without
/// relying on document order. Not used for any user-facing behavior.
/// </summary>
public sealed class ArtifactAutomationIdConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var prefix = parameter as string ?? string.Empty;
        var title = value as string ?? string.Empty;
        return prefix + title;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts hex color strings (e.g., "#0F6CBD") to SolidColorBrush instances.
/// Used for type badges in the Links section (slice 4).
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || !hex.StartsWith('#') || hex.Length != 7)
            return new SolidColorBrush(Colors.Gray); // Fallback for invalid hex

        try
        {
            var r = System.Convert.ToByte(hex.Substring(1, 2), 16);
            var g = System.Convert.ToByte(hex.Substring(3, 2), 16);
            var b = System.Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}