using System.IO;
using Glasswork.Core.Markdown;

namespace Glasswork.Services;

/// <summary>
/// Resolves wiki-link stems against the on-disk vault. Bare stems prefer the
/// task folder (<c>wiki/todo/&lt;stem&gt;.md</c>); otherwise the stem is treated
/// as a vault-relative path. Stems beginning with <c>_</c> never resolve to
/// tasks (matches BacklinkIndex's "private page" convention).
/// </summary>
public sealed class WikiLinkResolver : IWikiLinkResolver
{
    private readonly string _vaultRoot;
    private readonly string _todoRelative;

    public WikiLinkResolver(string vaultRoot, string todoRelative)
    {
        _vaultRoot = vaultRoot ?? string.Empty;
        _todoRelative = todoRelative ?? string.Empty;
    }

    public WikiLinkResolution Resolve(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return WikiLinkResolution.Unresolved.Instance;
        var trimmed = stem.Trim();

        if (!trimmed.Contains('/') && !trimmed.Contains('\\') && !trimmed.StartsWith('_'))
        {
            var taskPath = Path.Combine(_vaultRoot, _todoRelative, trimmed + ".md");
            if (File.Exists(taskPath))
            {
                return new WikiLinkResolution.Task(trimmed);
            }
        }

        var pagePath = Path.Combine(_vaultRoot, trimmed.Replace('/', Path.DirectorySeparatorChar) + ".md");
        if (File.Exists(pagePath))
        {
            var rel = Path.GetRelativePath(_vaultRoot, pagePath).Replace(Path.DirectorySeparatorChar, '/');
            return new WikiLinkResolution.VaultPage(rel);
        }

        return WikiLinkResolution.Unresolved.Instance;
    }
}
