using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Glasswork.Core.Services;

/// <summary>
/// Tracks paths the app is about to write to, so the FileSystemWatcher
/// callback can distinguish "we did this" from "someone else did this" and
/// avoid raising a false-positive "file changed on disk" reload banner.
///
/// Each registered path is suppressed for a short TTL window (default 1500ms)
/// to cover the gap between calling File.WriteAllText and the watcher event
/// landing on a thread-pool thread.
///
/// When a vault path is provided the coordinator also writes
/// <c>&lt;vault&gt;/.glasswork/recent-writes.json</c> so that external writers
/// (e.g. a separate-process MCP server) can register self-writes that the
/// running app will honour.  The in-memory dictionary remains the fast path for
/// same-process writes.
/// </summary>
public class SelfWriteCoordinator
{
    private readonly TimeSpan _ttl;
    private readonly string? _markerFilePath;
    private readonly ConcurrentDictionary<string, DateTime> _recentWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();

    public SelfWriteCoordinator() : this(TimeSpan.FromMilliseconds(1500)) { }

    public SelfWriteCoordinator(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    public SelfWriteCoordinator(string vaultPath) : this(vaultPath, TimeSpan.FromMilliseconds(1500)) { }

    public SelfWriteCoordinator(string vaultPath, TimeSpan ttl)
    {
        _ttl = ttl;
        if (!string.IsNullOrEmpty(vaultPath))
            _markerFilePath = Path.Combine(vaultPath, ".glasswork", "recent-writes.json");
    }

    /// <summary>Mark a path as one we are about to write to ourselves.</summary>
    public void RegisterWrite(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        var now = DateTime.UtcNow;
        _recentWrites[fullPath] = now;
        if (_markerFilePath != null)
            WriteMarkerFile(fullPath, now);
    }

    /// <summary>True if the given path was recently registered (within TTL).</summary>
    public bool IsSuppressed(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        // Fast path: check in-memory dictionary first (same-process writes).
        if (_recentWrites.TryGetValue(fullPath, out var when))
        {
            if (DateTime.UtcNow - when <= _ttl) return true;
            // Expired — drop it so the dictionary doesn't grow unbounded.
            _recentWrites.TryRemove(fullPath, out _);
        }

        // Cross-process path: consult the vault-local marker file.
        if (_markerFilePath != null)
            return CheckMarkerFile(fullPath);

        return false;
    }

    // --- marker file helpers -------------------------------------------------

    private void WriteMarkerFile(string newPath, DateTime timestamp)
    {
        lock (_fileLock)
        {
            var entries = ReadEntries();
            entries[newPath] = timestamp.ToString("O", CultureInfo.InvariantCulture);
            PruneExpired(entries);
            WriteAtomically(entries);
        }
    }

    private bool CheckMarkerFile(string fullPath)
    {
        lock (_fileLock)
        {
            var entries = ReadEntries();
            if (!entries.TryGetValue(fullPath, out var raw)) return false;

            if (!DateTime.TryParseExact(raw, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var when))
                return false;

            if (DateTime.UtcNow - when <= _ttl) return true;

            // Expired — prune and rewrite.
            entries.Remove(fullPath);
            WriteAtomically(entries);
            return false;
        }
    }

    /// <summary>
    /// Reads the marker file and returns its entries as a case-insensitive dictionary.
    /// Returns an empty dictionary when the file is missing or corrupt.
    /// </summary>
    private Dictionary<string, string> ReadEntries()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_markerFilePath == null || !File.Exists(_markerFilePath)) return result;

        try
        {
            var json = File.ReadAllText(_markerFilePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw != null)
                foreach (var kv in raw)
                    result[kv.Key] = kv.Value;
        }
        catch
        {
            // Corrupt or missing file — start fresh; do not crash.
        }

        return result;
    }

    private void PruneExpired(Dictionary<string, string> entries)
    {
        var cutoff = DateTime.UtcNow - _ttl;
        var toRemove = new List<string>();
        foreach (var kv in entries)
        {
            if (DateTime.TryParseExact(kv.Value, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt) && dt < cutoff)
                toRemove.Add(kv.Key);
        }
        foreach (var key in toRemove) entries.Remove(key);
    }

    private void WriteAtomically(Dictionary<string, string> entries)
    {
        // _markerFilePath is always non-null when WriteAtomically is called
        // (only called from paths guarded by `if (_markerFilePath != null)`).
        var markerFile = _markerFilePath!;
        var dir = Path.GetDirectoryName(markerFile)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var tmp = markerFile + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(markerFile))
            File.Replace(tmp, markerFile, null);
        else
            File.Move(tmp, markerFile);
    }
}
