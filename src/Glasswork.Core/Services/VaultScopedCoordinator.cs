using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        var mutex = AcquireProcessMutex(vaultPath);
        try
        {
            gate.EnterReadLock();
            return new Releaser(gate, mutex, write: false);
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public static IDisposable EnterExclusive(string vaultPath)
    {
        var gate = Locks.GetOrAdd(Path.GetFullPath(vaultPath), _ => new ReaderWriterLockSlim());
        var mutex = AcquireProcessMutex(vaultPath);
        try
        {
            gate.EnterWriteLock();
            return new Releaser(gate, mutex, write: true);
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private static Mutex AcquireProcessMutex(string vaultPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(vaultPath))));
        var mutex = new Mutex(false, $"Local\\GlassworkVault-{key}");
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Ownership transfers to this process after an abandoned writer exits.
        }
        return mutex;
    }

    private sealed class Releaser(ReaderWriterLockSlim gate, Mutex mutex, bool write) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            if (write) gate.ExitWriteLock();
            else gate.ExitReadLock();
        }
    }
}
