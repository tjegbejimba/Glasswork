using System.Collections.Generic;

namespace Glasswork.Core.Services;

/// <summary>
/// Auto-saving decorator for <see cref="IUiStateService"/>. Wraps an inner service and
/// automatically schedules a debounced <see cref="Save"/> after every <see cref="Set"/> or
/// <see cref="Remove"/>. Callers cannot forget to persist. Exposes <see cref="Flush"/> to
/// force an immediate synchronous save (for shutdown). See ADR 0014.
/// </summary>
public sealed class AutoSavingUiStateService : IUiStateService
{
    private readonly IUiStateService _inner;
    private readonly IDebouncer _debouncer;

    public AutoSavingUiStateService(IUiStateService inner, IDebouncer debouncer)
    {
        _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
        _debouncer = debouncer ?? throw new System.ArgumentNullException(nameof(debouncer));
    }

    public T? Get<T>(string key) => _inner.Get<T>(key);

    public void Set<T>(string key, T value)
    {
        _inner.Set(key, value);
        _debouncer.Trigger();
    }

    public void Remove(string key)
    {
        _inner.Remove(key);
        _debouncer.Trigger();
    }

    public void Save() => _inner.Save();

    public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes) =>
        _inner.RemoveKeysNotIn(keyPrefix, liveSuffixes);

    /// <summary>
    /// Forces an immediate synchronous save, cancelling any pending debounced save.
    /// Call on shutdown to close the rapid-exit data-loss window.
    /// </summary>
    public void Flush()
    {
        _inner.Save();
    }
}

/// <summary>
/// Abstraction for <see cref="Debouncer"/> to support testable deterministic fakes.
/// </summary>
public interface IDebouncer
{
    void Trigger();
}
