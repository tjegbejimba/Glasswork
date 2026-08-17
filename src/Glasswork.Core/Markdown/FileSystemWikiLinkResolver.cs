using System.IO;

namespace Glasswork.Core.Markdown;

public sealed class FileSystemWikiLinkResolver : IWikiLinkResolver
{
    private readonly string _vaultRoot;
    private readonly string _todoRelative;

    public FileSystemWikiLinkResolver(string vaultRoot, string todoRelative)
    {
        _vaultRoot = vaultRoot ?? string.Empty;
        _todoRelative = todoRelative ?? string.Empty;
    }

    public WikiLinkResolution Resolve(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return WikiLinkResolution.Unresolved.Instance;

        var trimmed = stem.Trim();
        if (!trimmed.Contains('/')
            && !trimmed.Contains('\\')
            && !trimmed.StartsWith('_'))
        {
            var taskPath = Path.Combine(_vaultRoot, _todoRelative, trimmed + ".md");
            if (File.Exists(taskPath))
                return new WikiLinkResolution.Task(trimmed);
        }

        var pagePath = Path.Combine(
            _vaultRoot,
            trimmed.Replace('/', Path.DirectorySeparatorChar) + ".md");
        if (!File.Exists(pagePath))
            return WikiLinkResolution.Unresolved.Instance;

        var relativePath = Path.GetRelativePath(_vaultRoot, pagePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return new WikiLinkResolution.VaultPage(relativePath);
    }
}
