using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Default <see cref="IBacklinkIndex"/> backed by a recursive vault scan and
/// an in-memory map keyed by task id. Pure Core — no UI/dispatcher coupling.
/// Thread-safety: all mutations and reads are guarded by an internal lock;
/// <see cref="GetBacklinks"/> snapshots the per-task list before returning.
/// </summary>
public sealed partial class BacklinkIndex : IBacklinkIndex
{
    // Matches [[stem]] and [[stem|display]]. Case-sensitive by default,
    // matching Obsidian's resolution. Pattern is shared with FrontmatterParser
    // and the markdown renderer via WikiLinkParser.Pattern.
    [GeneratedRegex(WikiLinkParser.Pattern)]
    private static partial Regex WikiLinkRegex();

    private readonly object _lock = new();
    private Dictionary<string, List<Backlink>> _byTaskId = new(StringComparer.Ordinal);

    // Side-index: which task ids does each linking page currently contribute to?
    // Keyed by full file path (case-insensitive — Windows). Lets RemoveForFile
    // and UpdateForFile find and prune the old entries without rescanning.
    private Dictionary<string, HashSet<string>> _fileToTaskIds =
        new(StringComparer.OrdinalIgnoreCase);

    public void Build(string vaultRoot)
    {
        var (byTask, byFile) = ScanVault(vaultRoot);
        lock (_lock)
        {
            _byTaskId = byTask;
            _fileToTaskIds = byFile;
        }
    }

    public IReadOnlyList<Backlink> GetBacklinks(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return Array.Empty<Backlink>();
        lock (_lock)
        {
            if (_byTaskId.TryGetValue(taskId, out var list))
                return list.ToArray();
            return Array.Empty<Backlink>();
        }
    }

    public IReadOnlyCollection<string> UpdateForFile(string vaultRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot) || string.IsNullOrWhiteSpace(filePath))
            return Array.Empty<string>();

        // Ignore anything outside the vault, or under wiki/todo/ (task files
        // themselves are never indexed as linking pages).
        string fullVault, fullFile;
        try
        {
            fullVault = Path.GetFullPath(vaultRoot);
            fullFile = Path.GetFullPath(filePath);
        }
        catch { return Array.Empty<string>(); }

        if (!IsUnderPrefix(fullFile, NormalizeDirectoryPrefix(fullVault)))
            return Array.Empty<string>();

        var todoPrefix = NormalizeDirectoryPrefix(Path.Combine(fullVault, "wiki", "todo"));
        if (IsUnderPrefix(fullFile, todoPrefix))
            return Array.Empty<string>();

        var todoDir = Path.Combine(fullVault, "wiki", "todo");
        var taskIds = EnumerateTaskIds(todoDir);

        lock (_lock)
        {
            var affected = new HashSet<string>(StringComparer.Ordinal);
            RemoveFileEntries(fullFile, affected);

            if (File.Exists(fullFile))
            {
                var sink = new Dictionary<string, List<Backlink>>(StringComparer.Ordinal);
                ProcessFile(fullFile, fullVault, taskIds, sink);
                foreach (var (stem, list) in sink)
                {
                    if (!_byTaskId.TryGetValue(stem, out var existing))
                    {
                        existing = new List<Backlink>();
                        _byTaskId[stem] = existing;
                    }
                    existing.AddRange(list);
                    affected.Add(stem);

                    if (!_fileToTaskIds.TryGetValue(fullFile, out var fileSet))
                    {
                        fileSet = new HashSet<string>(StringComparer.Ordinal);
                        _fileToTaskIds[fullFile] = fileSet;
                    }
                    fileSet.Add(stem);
                }
            }

            ReSortAffected(affected);
            return affected;
        }
    }

    public IReadOnlyCollection<string> RemoveForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<string>();
        string fullFile;
        try { fullFile = Path.GetFullPath(filePath); }
        catch { return Array.Empty<string>(); }

        lock (_lock)
        {
            var affected = new HashSet<string>(StringComparer.Ordinal);
            RemoveFileEntries(fullFile, affected);
            ReSortAffected(affected);
            return affected;
        }
    }

    public IReadOnlyCollection<string> Rename(string vaultRoot, string oldPath, string newPath)
    {
        var removed = RemoveForFile(oldPath);
        var added = UpdateForFile(vaultRoot, newPath);
        if (removed.Count == 0) return added;
        if (added.Count == 0) return removed;
        var union = new HashSet<string>(removed, StringComparer.Ordinal);
        foreach (var t in added) union.Add(t);
        return union;
    }

    /// <summary>
    /// Caller must already hold <see cref="_lock"/>. Drops every entry in
    /// <see cref="_byTaskId"/> whose path matches <paramref name="fullFile"/>,
    /// updates the side-map, and accumulates affected task ids.
    /// </summary>
    private void RemoveFileEntries(string fullFile, HashSet<string> affected)
    {
        if (!_fileToTaskIds.TryGetValue(fullFile, out var oldTaskIds))
            return;

        foreach (var taskId in oldTaskIds)
        {
            if (!_byTaskId.TryGetValue(taskId, out var list)) continue;
            list.RemoveAll(b => string.Equals(b.LinkingPagePath, fullFile, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) _byTaskId.Remove(taskId);
            affected.Add(taskId);
        }
        _fileToTaskIds.Remove(fullFile);
    }

    /// <summary>
    /// Caller must already hold <see cref="_lock"/>. Re-sorts the lists for
    /// every task id in <paramref name="affected"/> so the documented
    /// (PageType, Title, Path) order is preserved.
    /// </summary>
    private void ReSortAffected(IEnumerable<string> affected)
    {
        foreach (var id in affected)
        {
            if (_byTaskId.TryGetValue(id, out var list))
                SortList(list);
        }
    }

    private static (Dictionary<string, List<Backlink>> ByTask, Dictionary<string, HashSet<string>> ByFile)
        ScanVault(string vaultRoot)
    {
        var byTask = new Dictionary<string, List<Backlink>>(StringComparer.Ordinal);
        var byFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
            return (byTask, byFile);

        var todoDir = Path.Combine(vaultRoot, "wiki", "todo");
        var taskIds = EnumerateTaskIds(todoDir);
        if (taskIds.Count == 0) return (byTask, byFile);

        var todoPrefix = NormalizeDirectoryPrefix(todoDir);

        foreach (var file in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
        {
            if (IsUnderPrefix(file, todoPrefix)) continue;
            var perFile = new Dictionary<string, List<Backlink>>(StringComparer.Ordinal);
            ProcessFile(file, vaultRoot, taskIds, perFile);
            if (perFile.Count == 0) continue;

            var fullFile = Path.GetFullPath(file);
            var fileSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (stem, list) in perFile)
            {
                if (!byTask.TryGetValue(stem, out var dest))
                {
                    dest = new List<Backlink>();
                    byTask[stem] = dest;
                }
                dest.AddRange(list);
                fileSet.Add(stem);
            }
            byFile[fullFile] = fileSet;
        }

        foreach (var list in byTask.Values) SortList(list);
        return (byTask, byFile);
    }

    private static HashSet<string> EnumerateTaskIds(string todoDir)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(todoDir)) return ids;
        foreach (var file in Directory.EnumerateFiles(todoDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            // Skip Glasswork's auto-generated index files (_index.md, _today.md, etc.).
            if (fileName.StartsWith('_')) continue;
            ids.Add(Path.GetFileNameWithoutExtension(file));
        }
        return ids;
    }

    private static void ProcessFile(
        string file,
        string vaultRoot,
        HashSet<string> taskIds,
        Dictionary<string, List<Backlink>> sink)
    {
        string content;
        try { content = File.ReadAllText(file); }
        catch { return; }

        // Per-file dedup: collect each task id at most once even if the page
        // mentions it multiple times.
        var matchedStems = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WikiLinkRegex().Matches(content))
        {
            var stem = m.Groups[1].Value.Trim();
            if (stem.Length == 0) continue;
            if (taskIds.Contains(stem)) matchedStems.Add(stem);
        }
        if (matchedStems.Count == 0) return;

        var pageType = ClassifyPageType(file, vaultRoot);
        var title = WikiPageTitleResolver.Resolve(content, file);
        var modified = File.GetLastWriteTimeUtc(file);
        var entry = new Backlink(Path.GetFullPath(file), title, pageType, modified);

        foreach (var stem in matchedStems)
        {
            if (!sink.TryGetValue(stem, out var list))
            {
                list = new List<Backlink>();
                sink[stem] = list;
            }
            list.Add(entry);
        }
    }

    private static BacklinkPageType ClassifyPageType(string file, string vaultRoot)
    {
        string relative;
        try { relative = Path.GetRelativePath(vaultRoot, file); }
        catch { return BacklinkPageType.Other; }

        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        for (int i = 0; i < parts.Length - 2; i++)
        {
            if (!parts[i].Equals("wiki", StringComparison.OrdinalIgnoreCase)) continue;
            return parts[i + 1].ToLowerInvariant() switch
            {
                "concepts" => BacklinkPageType.Concept,
                "decisions" => BacklinkPageType.Decision,
                "incidents" => BacklinkPageType.Incident,
                "systems" => BacklinkPageType.System,
                _ => BacklinkPageType.Other,
            };
        }
        return BacklinkPageType.Other;
    }

    private static void SortList(List<Backlink> list)
    {
        list.Sort((a, b) =>
        {
            var byType = ((int)a.PageType).CompareTo((int)b.PageType);
            if (byType != 0) return byType;
            var byTitle = string.Compare(a.LinkingPageTitle, b.LinkingPageTitle, StringComparison.OrdinalIgnoreCase);
            if (byTitle != 0) return byTitle;
            return string.Compare(a.LinkingPagePath, b.LinkingPagePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeDirectoryPrefix(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!full.EndsWith(Path.DirectorySeparatorChar))
            full += Path.DirectorySeparatorChar;
        return full;
    }

    private static bool IsUnderPrefix(string file, string normalizedPrefix)
    {
        var full = Path.GetFullPath(file);
        return full.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
