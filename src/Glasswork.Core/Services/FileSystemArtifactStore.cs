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

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(ArtifactCommitPolicy.IsCommitted);
        var artifacts = new List<Artifact>();
        
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            var kind = ArtifactKindResolver.Resolve(file);
            var sizeBytes = fileInfo.Length;
            var modified = fileInfo.LastWriteTimeUtc;
            
            string? body = null;
            string? loadError = null;
            
            // Read body only for Markdown/Text kinds AND under the inline cap
            if ((kind == ArtifactKind.Markdown || kind == ArtifactKind.Text) && 
                sizeBytes <= ArtifactCaps.InlineTextBytes)
            {
                try
                {
                    body = File.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    loadError = ex.Message;
                }
            }
            
            // Title: Markdown → WikiPageTitleResolver; others → filename with extension
            string title;
            if (kind == ArtifactKind.Markdown && body != null)
            {
                title = WikiPageTitleResolver.Resolve(body, file);
            }
            else
            {
                title = Path.GetFileName(file);
            }
            
            artifacts.Add(new Artifact(file, title, modified, body)
            {
                Kind = kind,
                SizeBytes = sizeBytes,
                LoadError = loadError
            });
        }

        return artifacts
            .OrderBy(a => a.ModifiedUtc)
            .ToList();
    }
}
