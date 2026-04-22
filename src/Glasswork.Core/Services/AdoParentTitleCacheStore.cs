using System;
using System.Collections.Generic;
using System.Globalization;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// App-local persistence facade for resolved ADO parent work-item titles. Backs the
/// in-memory cache used by the Backlog view's parent group headers so that the
/// resolved title (e.g. <c>"#12345 — Real Title"</c>) renders from the first frame
/// after launch instead of flickering the bare numeric ID while a network fetch
/// runs.
///
/// Key shape: <c>ado.parent.title.&lt;id&gt;</c>. Value shape: <see cref="AdoParentTitleEntry"/>.
/// Entries past the 30-day TTL are treated as cache misses on hydration; they will
/// be overwritten on the next successful re-resolve.
///
/// Negative results (failed lookups) are NOT persisted — they stay in-memory only
/// and are retried next session, since fetch failures are usually transient.
/// </summary>
public sealed class AdoParentTitleCacheStore
{
    public const string KeyPrefix = "ado.parent.title.";
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    private readonly IUiStateService _ui;

    public AdoParentTitleCacheStore(IUiStateService ui) => _ui = ui;

    private static string KeyFor(int id) => KeyPrefix + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Fetches stored titles for the given candidate IDs, skipping entries past the
    /// 30-day TTL. Returns only entries with a non-empty title.
    /// </summary>
    public IReadOnlyDictionary<int, string> LoadFresh(IEnumerable<int> candidateIds)
    {
        var now = DateTime.UtcNow;
        var result = new Dictionary<int, string>();
        foreach (var id in candidateIds)
        {
            var entry = _ui.Get<AdoParentTitleEntry>(KeyFor(id));
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.Title)) continue;
            if (now - entry.ResolvedAt > Ttl) continue;
            result[id] = entry.Title;
        }
        return result;
    }

    /// <summary>
    /// Stages a resolved title for persistence. Caller invokes <see cref="Save"/>
    /// once per batch to flush to disk.
    /// </summary>
    public void Set(int id, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _ui.Set(KeyFor(id), new AdoParentTitleEntry(title, DateTime.UtcNow));
    }

    /// <summary>
    /// Removes any persisted parent-title entries whose ID is not in
    /// <paramref name="liveIds"/>. Caller invokes <see cref="Save"/> to flush.
    /// </summary>
    public void Compact(IEnumerable<int> liveIds)
    {
        var suffixes = new HashSet<string>();
        foreach (var id in liveIds)
        {
            suffixes.Add(id.ToString(CultureInfo.InvariantCulture));
        }
        _ui.RemoveKeysNotIn(KeyPrefix, suffixes);
    }

    /// <summary>Flushes pending writes (calls through to <see cref="IUiStateService.Save"/>).</summary>
    public void Save() => _ui.Save();
}
