using System;
using System.Collections.Concurrent;

namespace Glasswork.Core.Services;

/// <summary>
/// Tracks paths the app is about to write to, so the FileSystemWatcher
/// callback can distinguish "we did this" from "someone else did this" and
/// avoid raising a false-positive "file changed on disk" reload banner.
///
/// Each registered path is suppressed for a short TTL window (default 1500ms)
/// to cover the gap between calling File.WriteAllText and the watcher event
/// landing on a thread-pool thread.
/// </summary>
public class SelfWriteCoordinator
{
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, DateTime> _recentWrites =
        new(StringComparer.OrdinalIgnoreCase);

    public SelfWriteCoordinator() : this(TimeSpan.FromMilliseconds(1500)) { }

    public SelfWriteCoordinator(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    /// <summary>Mark a path as one we are about to write to ourselves.</summary>
    public void RegisterWrite(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        _recentWrites[fullPath] = DateTime.UtcNow;
    }

    /// <summary>True if the given path was recently registered (within TTL).</summary>
    public bool IsSuppressed(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (!_recentWrites.TryGetValue(fullPath, out var when)) return false;

        if (DateTime.UtcNow - when <= _ttl) return true;

        // Expired — drop it so the dictionary doesn't grow unbounded.
        _recentWrites.TryRemove(fullPath, out _);
        return false;
    }
}
