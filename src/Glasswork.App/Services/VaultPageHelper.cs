using System.IO;
using System.Threading.Tasks;
using Glasswork.Controls;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;
using Glasswork.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// Builds a wiki-link resolver rooted at the current vault layout.
    /// Returns null when the vault path cannot be parsed (e.g. vault not yet configured).
    /// </summary>
    internal static IWikiLinkResolver? BuildWikiLinkResolver()
    {
        var todoDir = App.Vault.VaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var vaultRoot = Path.GetDirectoryName(Path.GetDirectoryName(todoDir));
        if (string.IsNullOrWhiteSpace(vaultRoot)) return null;
        try
        {
            var rel = Path.GetRelativePath(vaultRoot, todoDir).Replace(Path.DirectorySeparatorChar, '/');
            return new WikiLinkResolver(vaultRoot, rel);
        }
        catch { return null; }
    }

    /// <summary>
    /// Routes a <see cref="LinkClickedEventArgs"/> to the correct destination:
    /// wiki-links navigate in-app or open Obsidian; <c>glasswork://</c> URIs navigate
    /// in-app; all other URL links were already opened by <c>Hyperlink.NavigateUri</c>.
    /// </summary>
    internal static async Task RouteLinkClickAsync(Frame? frame, LinkClickedEventArgs e)
    {
        // Wiki-link routing.
        switch (e.Resolution)
        {
            case WikiLinkResolution.Task task:
            {
                var loaded = App.Vault.Load(task.TaskId);
                if (loaded is null || frame is null) return;
                frame.Navigate(typeof(TaskDetailPage), loaded);
                return;
            }
            case WikiLinkResolution.VaultPage page:
                await App.ObsidianLauncher.Open(page.VaultRelativePath);
                return;
            // WikiLinkResolution.Unresolved: rendered muted and non-interactive — LinkClicked
            // fires only for URL links (where Resolution is Unresolved by construction).
        }

        if (e.Kind != LinkKind.Url) return;

        // glasswork:// URIs: VaultMarkdownView does NOT set NavigateUri for these, so we
        // handle in-app navigation here.
        var gwUri = GlassworkUriParser.Parse(e.Href);
        if (gwUri is null || frame is null) return;

        switch (gwUri)
        {
            case GlassworkUri.Task t:
                var loaded = App.Vault.Load(t.TaskId);
                if (loaded is not null) frame.Navigate(typeof(TaskDetailPage), loaded);
                break;
            case GlassworkUri.MyDay:
                frame.Navigate(typeof(MyDayPage));
                break;
            case GlassworkUri.Backlog:
                frame.Navigate(typeof(BacklogPage));
                break;
        }
        // All other URL links (http/https/obsidian) were already opened by Hyperlink.NavigateUri.
    }
}
