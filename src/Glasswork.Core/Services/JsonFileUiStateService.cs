using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glasswork.Core.Services;

/// <summary>
/// JSON-file backed implementation of <see cref="IUiStateService"/>.
/// Default location is <c>%LocalAppData%\Glasswork\ui-state.json</c>.
/// Uses merge-on-save to avoid cross-process clobber: each process tracks only
/// the keys it mutated, then merges those changes on top of the current disk state.
/// </summary>
public sealed class JsonFileUiStateService : IUiStateService
{
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
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, null);
            else File.Move(tmp, _filePath);
            
            // Clear dirty tracking after successful save
            _dirtyKeys.Clear();
            _deletedKeys.Clear();
        }
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
