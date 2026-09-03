using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Glasswork.Core.Services;

/// <summary>
/// JSON-file backed implementation of <see cref="IUiStateService"/>.
/// Default location is <c>%LocalAppData%\Glasswork\ui-state.json</c>.
/// Uses merge-on-save to avoid cross-process clobber: each process tracks only
/// the keys it mutated, then merges those changes on top of the current disk state.
/// The merge-read-then-write section is additionally guarded by a named,
/// cross-process <see cref="Mutex"/> keyed by the file's full path: without it,
/// two processes racing to save around the same instant can each read a disk
/// snapshot that predates the other's write and silently drop it (a classic
/// lost-update), even though neither process's own dirty keys collide.
/// </summary>
public sealed class JsonFileUiStateService : IUiStateService
{
    private static readonly ConcurrentDictionary<string, Mutex> FileMutexes = new(StringComparer.Ordinal);

    private readonly string _filePath;
    private readonly Dictionary<string, JsonElement> _state;
    private readonly HashSet<string> _dirtyKeys = new();
    private readonly HashSet<string> _deletedKeys = new();
    private readonly object _lock = new();

    public JsonFileUiStateService(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _state = Load(_filePath);
    }

    public static string DefaultFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Glasswork",
            "ui-state.json");

    public T? Get<T>(string key)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(key, out var element)) return default;
            try { return element.Deserialize<T>(); }
            catch (JsonException) { return default; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _state[key] = JsonSerializer.SerializeToElement(value);
            _dirtyKeys.Add(key);
            _deletedKeys.Remove(key); // If we set it, it's not deleted
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            _state.Remove(key);
            _deletedKeys.Add(key);
            _dirtyKeys.Remove(key); // If we deleted it, it's not dirty (no value to merge)
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var mutex = GetFileMutex(_filePath);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException)
                {
                    // A prior holder (e.g. a killed canvas host process) exited
                    // without releasing it. Ownership still transfers to us —
                    // proceed; we always re-read disk state fresh below.
                    acquired = true;
                }

                // Merge-on-save: re-read current disk state, apply our changes on top
                var diskState = Load(_filePath);

                // Apply this instance's dirty keys
                foreach (var key in _dirtyKeys)
                {
                    if (_state.TryGetValue(key, out var value))
                    {
                        diskState[key] = value;
                    }
                }

                // Apply this instance's deletions
                foreach (var key in _deletedKeys)
                {
                    diskState.Remove(key);
                }

                // Write merged state atomically
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(diskState, new JsonSerializerOptions { WriteIndented = true });
                // Unique per call (not just per-instance) so two writers whose
                // Mutex acquisitions land back-to-back never contend for the
                // same temp path — Windows can briefly keep a just-closed file
                // "in use" (AV/indexing), which a shared ".tmp" name would hit.
                var tmp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_filePath)) File.Replace(tmp, _filePath, null);
                else File.Move(tmp, _filePath);

                // Clear dirty tracking after successful save
                _dirtyKeys.Clear();
                _deletedKeys.Clear();
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Resolves the named cross-process <see cref="Mutex"/> guarding
    /// <paramref name="filePath"/>'s merge-on-save section. Named by a hash of
    /// the full, case-normalized path rather than the raw path so it stays
    /// within named-object length/character limits regardless of the
    /// underlying path's length or casing.
    /// </summary>
    private static Mutex GetFileMutex(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var normalized = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var name = "Glasswork.UiState." + hash;
        return FileMutexes.GetOrAdd(name, n => new Mutex(initiallyOwned: false, name: n));
    }

    public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes)
    {
        var live = new HashSet<string>(liveSuffixes, StringComparer.Ordinal);
        lock (_lock)
        {
            var toRemove = new List<string>();
            foreach (var key in _state.Keys)
            {
                if (!key.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                var suffix = key.Substring(keyPrefix.Length);
                if (!live.Contains(suffix)) toRemove.Add(key);
            }
            foreach (var k in toRemove)
            {
                _state.Remove(k);
                _deletedKeys.Add(k);
                _dirtyKeys.Remove(k);
            }
        }
    }

    /// <summary>
    /// Removes every key for which <paramref name="shouldRemove"/> returns true.
    /// Generic key-level prune used by startup GC (e.g. stale dismissals). Mutates
    /// the in-memory store only; call <see cref="Save"/> to persist.
    /// </summary>
    public void RemoveKeysWhere(Func<string, bool> shouldRemove)
    {
        ArgumentNullException.ThrowIfNull(shouldRemove);
        lock (_lock)
        {
            var toRemove = new List<string>();
            foreach (var key in _state.Keys)
            {
                if (shouldRemove(key)) toRemove.Add(key);
            }
            foreach (var k in toRemove)
            {
                _state.Remove(k);
                _deletedKeys.Add(k);
                _dirtyKeys.Remove(k);
            }
        }
    }

    private static Dictionary<string, JsonElement> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new Dictionary<string, JsonElement>();
        try
        {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, JsonElement>();
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return dict ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception)
        {
            // Corrupt file — start fresh rather than crash on launch.
            return new Dictionary<string, JsonElement>();
        }
    }
}
