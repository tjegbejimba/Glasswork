using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Filesystem-backed <see cref="IArtifactStore"/> reading
/// &lt;vault&gt;/wiki/todo/&lt;taskId&gt;.artifacts/*.md.
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _vaultRoot;

    public FileSystemArtifactStore(string vaultRoot)
    {
        _vaultRoot = vaultRoot ?? throw new ArgumentNullException(nameof(vaultRoot));
    }

    public IReadOnlyList<Artifact> Load(string taskId)
    {
        var folder = Path.Combine(_vaultRoot, "wiki", "todo", taskId + ".artifacts");
        if (!Directory.Exists(folder))
        {
            return Array.Empty<Artifact>();
        }

        var files = Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly);
        var artifacts = new List<Artifact>();
        foreach (var file in files)
        {
            var raw = File.ReadAllText(file);
            var title = WikiPageTitleResolver.Resolve(raw, file);
            var modified = File.GetLastWriteTimeUtc(file);
            artifacts.Add(new Artifact(file, title, modified, raw));
        }

        return artifacts
            .OrderByDescending(a => a.ModifiedUtc)
            .ToList();
    }
}
