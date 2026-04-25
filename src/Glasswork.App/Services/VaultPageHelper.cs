using System.IO;
using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Glasswork.Services;

/// <summary>
/// Shared vault-path and focus utilities used by page code-behinds to open vault
/// files in Obsidian. Keeps the logic in one place rather than duplicating it
/// across BacklogPage, MyDayPage, and TaskDetailPage.
/// </summary>
internal static class VaultPageHelper
{
    /// <summary>
    /// Converts an absolute on-disk path to a vault-relative path suitable for
    /// <see cref="Core.Services.IObsidianLauncher.Open"/>.
    /// Returns null when the path cannot be relativized (e.g. outside the vault).
    /// </summary>
    internal static string? ToVaultRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        // App.Vault.VaultPath is wiki/todo/. The Obsidian vault root is two levels up.
        var todoDir = App.Vault.VaultPath.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                                   System.IO.Path.AltDirectorySeparatorChar);
        var vaultRoot = Path.GetDirectoryName(Path.GetDirectoryName(todoDir));
        if (string.IsNullOrWhiteSpace(vaultRoot)) return null;

        try { return Path.GetRelativePath(vaultRoot, absolutePath); }
        catch { return null; }
    }

    /// <summary>
    /// Walks up the visual tree from the currently-focused element to find the
    /// nearest <see cref="GlassworkTask"/> DataContext. Returns null when no task
    /// row currently has focus.
    /// </summary>
    internal static GlassworkTask? GetFocusedTask(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null) return null;
        var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is FrameworkElement fe && fe.DataContext is GlassworkTask task)
                return task;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return null;
    }
}
