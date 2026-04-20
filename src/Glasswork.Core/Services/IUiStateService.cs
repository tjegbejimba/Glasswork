using System.Collections.Generic;

namespace Glasswork.Core.Services;

/// <summary>
/// Persistent app-local key/value store for UI state that must NOT live in the vault
/// (e.g. which task cards the user has manually collapsed). See ADR 0001.
/// </summary>
public interface IUiStateService
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
    void Save();
    void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes);
}
