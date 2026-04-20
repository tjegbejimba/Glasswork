using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glasswork.Core.Services;

/// <summary>
/// JSON-file backed implementation of <see cref="IUiStateService"/>.
/// Default location is <c>%LocalAppData%\Glasswork\ui-state.json</c>.
/// Not thread-safe across processes; single-instance app assumption (see App.OnLaunched).
/// </summary>
public sealed class JsonFileUiStateService : IUiStateService
{
    private readonly string _filePath;
    private readonly Dictionary<string, JsonElement> _state;
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
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            _state.Remove(key);
        }
    }

    public void Save()
    {
        Dictionary<string, JsonElement> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, JsonElement>(_state);
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        // Atomic-ish write: temp file + rename
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, null);
        else File.Move(tmp, _filePath);
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
            foreach (var k in toRemove) _state.Remove(k);
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
