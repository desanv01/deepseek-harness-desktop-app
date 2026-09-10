using System;
using System.Threading;

namespace DShNative;

/** Per-endpoint named mutex: whoever holds it may update/start the server. */
public sealed class ManagedLock : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _disposed;
    public bool Owner { get; }

    private ManagedLock(Mutex? mutex, bool owner)
    {
        _mutex = mutex;
        Owner = owner;
    }

    public static ManagedLock TryAcquireHome(string home)
        => TryAcquireNamed(AppPaths.HomeMutexName(home), "home " + home);

    private static ManagedLock TryAcquireNamed(string name, string label)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            var owner = false;
            try
            {
                owner = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The OS has already granted ownership after the previous
                // launcher died. There is no PID-reuse window to interpret.
                owner = true;
                Log.Warn($"recovered an abandoned lock for {label}");
            }
            return new ManagedLock(mutex, owner);
        }
        catch (Exception ex)
        {
            mutex?.Dispose();
            // A lock failure must never become an ownership claim. The caller
            // will use the safe wait/attach path instead.
            Log.Warn("could not acquire the lock for " + label + ": " + ex.Message);
            return new ManagedLock(null, owner: false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mutex == null) return;
        if (Owner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
