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
    private readonly ConcurrentDictionary<string, long> _writeGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _consumedWriteGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, RegistrationSnapshot> _registrationSnapshots = new();
    private readonly ConcurrentDictionary<long, byte> _cancelledWriteGenerations = new();
    private readonly ConcurrentDictionary<long, byte> _activeWriteRegistrations = new();
    private long _nextWriteGeneration;
    private readonly object _fileLock = new();
    private readonly object _registrationLock = new();

    internal int PendingRegistrationCount => _registrationSnapshots.Count;
    internal int CancelledRegistrationCount => _cancelledWriteGenerations.Count;

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
        using var registration = BeginWrite(fullPath);
        registration.Commit();
    }

    /// <summary>
    /// Tentatively marks a path as a self-write. Commit after the filesystem
    /// mutation succeeds; disposing without commit restores the prior marker.
    /// </summary>
    public SelfWriteRegistration BeginWrite(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return SelfWriteRegistration.Empty;

        lock (_registrationLock)
        {
            var previous = new RegistrationSnapshot(
                _recentWrites.TryGetValue(fullPath, out var priorWhen)
                    ? priorWhen
                    : null,
                _writeGenerations.TryGetValue(fullPath, out var priorGeneration)
                    ? priorGeneration
                    : null,
                _consumedWriteGenerations.TryGetValue(fullPath, out var priorConsumed)
                    ? priorConsumed
                    : null,
                ReadMarkerValue(fullPath));
            var now = DateTime.UtcNow;
            var generation = Interlocked.Increment(ref _nextWriteGeneration);
            _recentWrites[fullPath] = now;
            _writeGenerations[fullPath] = generation;
            _consumedWriteGenerations.TryRemove(fullPath, out _);
            _registrationSnapshots[generation] = previous;
            _activeWriteRegistrations[generation] = 0;
            try
            {
                if (_markerFilePath != null)
                    WriteMarkerFile(fullPath, now);
            }
            catch
            {
                RestoreRegistration(fullPath, generation, now, previous);
                throw;
            }

            return new SelfWriteRegistration(
                this,
                fullPath,
                generation,
                now,
                previous);
        }
    }

    /// <summary>True if the given path was recently registered (within TTL).</summary>
    public bool IsSuppressed(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        // Fast path: check in-memory dictionary first (same-process writes).
        if (IsOwnProcessWrite(fullPath)) return true;

        // Cross-process path: consult the vault-local marker file.
        if (_markerFilePath != null)
            return CheckMarkerFile(fullPath);

        return false;
    }

    /// <summary>
    /// True only if **this process** registered the write (within TTL); ignores the
    /// cross-process marker file. Use this for callers that need to update their
    /// in-memory state from cross-process writes (e.g. <c>IndexService</c> consuming
    /// watcher events from MCP edits) while leaving the broader
    /// <see cref="IsSuppressed"/> predicate available for the existing
    /// external-edit / conflict-banner suppression path. See issue #184.
    /// </summary>
    public bool IsOwnProcessWrite(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        if (_recentWrites.TryGetValue(fullPath, out var when))
        {
            if (DateTime.UtcNow - when <= _ttl) return true;
            // Expired — drop it so the dictionary doesn't grow unbounded.
            _recentWrites.TryRemove(fullPath, out _);
            _writeGenerations.TryRemove(fullPath, out _);
            _consumedWriteGenerations.TryRemove(fullPath, out _);
        }

        return false;
    }

    /// <summary>
    /// Consumes one same-process watcher echo. A later change to the same path,
    /// even inside the TTL, is treated as a genuine external edit.
    /// </summary>
    public bool TryConsumeOwnProcessWrite(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (!_recentWrites.TryGetValue(fullPath, out var when)) return false;
        if (DateTime.UtcNow - when > _ttl)
        {
            _recentWrites.TryRemove(fullPath, out _);
            _writeGenerations.TryRemove(fullPath, out _);
            _consumedWriteGenerations.TryRemove(fullPath, out _);
            return false;
        }

        if (!_writeGenerations.TryGetValue(fullPath, out var generation))
            return false;
        if (_consumedWriteGenerations.TryGetValue(fullPath, out var consumed)
            && consumed == generation)
        {
            return false;
        }

        _consumedWriteGenerations[fullPath] = generation;
        return true;
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

    private string? ReadMarkerValue(string fullPath)
    {
        if (_markerFilePath is null)
            return null;
        lock (_fileLock)
        {
            var entries = ReadEntries();
            return entries.TryGetValue(fullPath, out var value) ? value : null;
        }
    }

    private void RestoreRegistration(
        string fullPath,
        long generation,
        DateTime timestamp,
        RegistrationSnapshot previous)
    {
        lock (_registrationLock)
        {
            _activeWriteRegistrations.TryRemove(generation, out _);
            _cancelledWriteGenerations[generation] = 0;
            if (!_writeGenerations.TryGetValue(fullPath, out var currentGeneration)
                || currentGeneration != generation)
            {
                if (!_activeWriteRegistrations.ContainsKey(currentGeneration))
                {
                    var abandoned = new List<long> { generation };
                    var ancestor = previous;
                    while (ancestor.Generation is { } predecessor
                           && _cancelledWriteGenerations.ContainsKey(predecessor)
                           && _registrationSnapshots.TryGetValue(
                               predecessor,
                               out var predecessorSnapshot))
                    {
                        abandoned.Add(predecessor);
                        ancestor = predecessorSnapshot;
                    }
                    ForgetRegistrations(abandoned);
                }
                return;
            }

            var restored = previous;
            var traversed = new List<long> { generation };
            while (restored.Generation is { } predecessor
                   && _cancelledWriteGenerations.ContainsKey(predecessor)
                   && _registrationSnapshots.TryGetValue(
                       predecessor,
                       out var predecessorSnapshot))
            {
                traversed.Add(predecessor);
                restored = predecessorSnapshot;
            }

            RestoreEntry(_recentWrites, fullPath, restored.When);
            RestoreEntry(_writeGenerations, fullPath, restored.Generation);
            RestoreEntry(
                _consumedWriteGenerations,
                fullPath,
                restored.ConsumedGeneration);
            if (_markerFilePath is null)
            {
                ForgetRegistrations(traversed);
                return;
            }

            lock (_fileLock)
            {
                var entries = ReadEntries();
                var tentativeValue = timestamp.ToString("O", CultureInfo.InvariantCulture);
                if (!entries.TryGetValue(fullPath, out var currentValue)
                    || !string.Equals(
                        currentValue,
                        tentativeValue,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (restored.MarkerValue is null)
                    entries.Remove(fullPath);
                else
                    entries[fullPath] = restored.MarkerValue;
                WriteAtomically(entries);
            }
            ForgetRegistrations(traversed);
        }
    }

    private void CommitRegistration(long generation)
    {
        _activeWriteRegistrations.TryRemove(generation, out _);
        if (_registrationSnapshots.TryRemove(generation, out var snapshot))
        {
            while (snapshot.Generation is { } predecessor
                   && _cancelledWriteGenerations.TryRemove(predecessor, out _)
                   && _registrationSnapshots.TryRemove(
                       predecessor,
                       out var predecessorSnapshot))
            {
                snapshot = predecessorSnapshot;
            }
        }
        _cancelledWriteGenerations.TryRemove(generation, out _);
    }

    private void ForgetRegistrations(IEnumerable<long> generations)
    {
        foreach (var generation in generations)
        {
            _registrationSnapshots.TryRemove(generation, out _);
            _cancelledWriteGenerations.TryRemove(generation, out _);
            _activeWriteRegistrations.TryRemove(generation, out _);
        }
    }

    private static void RestoreEntry<T>(
        ConcurrentDictionary<string, T> entries,
        string path,
        T? value)
        where T : struct
    {
        if (value is { } restored)
            entries[path] = restored;
        else
            entries.TryRemove(path, out _);
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

    internal sealed record RegistrationSnapshot(
        DateTime? When,
        long? Generation,
        long? ConsumedGeneration,
        string? MarkerValue);

    public sealed class SelfWriteRegistration : IDisposable
    {
        internal static SelfWriteRegistration Empty { get; } = new();

        private readonly SelfWriteCoordinator? _coordinator;
        private readonly string _path = string.Empty;
        private readonly long _generation;
        private readonly DateTime _timestamp;
        private readonly RegistrationSnapshot? _previous;
        private int _state;

        private SelfWriteRegistration()
        {
            _state = 1;
        }

        internal SelfWriteRegistration(
            SelfWriteCoordinator coordinator,
            string path,
            long generation,
            DateTime timestamp,
            RegistrationSnapshot previous)
        {
            _coordinator = coordinator;
            _path = path;
            _generation = generation;
            _timestamp = timestamp;
            _previous = previous;
        }

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                _coordinator?.CommitRegistration(_generation);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0
                || _coordinator is null
                || _previous is null)
            {
                return;
            }

            _coordinator.RestoreRegistration(
                _path,
                _generation,
                _timestamp,
                _previous);
        }
    }
}
