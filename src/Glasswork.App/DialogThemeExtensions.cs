using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork;

/// <summary>
/// WinUI 3 ContentDialogs render in a popup that does not inherit the app's
/// RequestedTheme from the XamlRoot's content. As a result, a dialog opened
/// from a dark-themed page may appear in light mode (or vice versa).
///
/// This extension propagates the host element's ActualTheme to the dialog so
/// dialogs match the rest of the app. Call after setting XamlRoot.
/// </summary>
internal static class DialogThemeExtensions
{
    /// <summary>
    /// Sets the dialog's RequestedTheme from the host element's ActualTheme.
    /// No-op if host is null or its ActualTheme is Default.
    /// </summary>
    public static T WithAppTheme<T>(this T dialog, FrameworkElement? host) where T : ContentDialog
    {
        if (host is null) return dialog;
        var theme = host.ActualTheme;
        if (theme != ElementTheme.Default)
            dialog.RequestedTheme = theme;
        return dialog;
    }
}
