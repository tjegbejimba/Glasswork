using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Default <see cref="IBacklinkIndex"/> backed by a recursive vault scan and
/// an in-memory map keyed by task id. Pure Core — no UI/dispatcher coupling.
/// Thread-safety: <see cref="Build"/> rebuilds atomically under an internal
/// lock; <see cref="GetBacklinks"/> snapshots the current per-task list.
/// </summary>
public sealed partial class BacklinkIndex : IBacklinkIndex
{
    // Matches [[stem]] and [[stem|display]]. Case-sensitive by default,
    // matching Obsidian's resolution. Mirrors FrontmatterParser.WikiLinkRegex.
    [GeneratedRegex(@"\[\[([^\]\|]+?)(?:\|([^\]]+))?\]\]")]
    private static partial Regex WikiLinkRegex();

    private readonly object _lock = new();
    private Dictionary<string, List<Backlink>> _byTaskId = new(StringComparer.Ordinal);

    public void Build(string vaultRoot)
    {
        var fresh = ScanVault(vaultRoot);
        lock (_lock) { _byTaskId = fresh; }
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

    private static Dictionary<string, List<Backlink>> ScanVault(string vaultRoot)
    {
        var result = new Dictionary<string, List<Backlink>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
            return result;

        var todoDir = Path.Combine(vaultRoot, "wiki", "todo");
        var taskIds = EnumerateTaskIds(todoDir);
        if (taskIds.Count == 0) return result;

        var todoPrefix = NormalizeDirectoryPrefix(todoDir);

        foreach (var file in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
        {
            if (IsUnderPrefix(file, todoPrefix)) continue;
            ProcessFile(file, vaultRoot, taskIds, result);
        }

        SortAll(result);
        return result;
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
        var entry = new Backlink(file, title, pageType, modified);

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

    private static void SortAll(Dictionary<string, List<Backlink>> map)
    {
        foreach (var list in map.Values)
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
