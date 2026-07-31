using System.Collections.Concurrent;
using System.Threading;

namespace Glasswork.Core.Services;

/// <summary>
/// Coordinates managed readers and writers that share one Vault.
/// </summary>
public static class VaultScopedCoordinator
{
    private static readonly ConcurrentDictionary<string, ReaderWriterLockSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable EnterShared(string vaultPath)
    {
        var gate = Locks.GetOrAdd(Path.GetFullPath(vaultPath), _ => new ReaderWriterLockSlim());
        gate.EnterReadLock();
        return new Releaser(gate, write: false);
    }

    public static IDisposable EnterExclusive(string vaultPath)
    {
        var gate = Locks.GetOrAdd(Path.GetFullPath(vaultPath), _ => new ReaderWriterLockSlim());
        gate.EnterWriteLock();
        return new Releaser(gate, write: true);
    }

    private sealed class Releaser(ReaderWriterLockSlim gate, bool write) : IDisposable
    {
        public void Dispose()
        {
            if (write) gate.ExitWriteLock();
            else gate.ExitReadLock();
        }
    }
}
