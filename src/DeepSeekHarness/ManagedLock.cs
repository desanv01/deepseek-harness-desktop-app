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
        => AcquireNamed(AppPaths.HomeMutexName(home), "home " + home, TimeSpan.Zero);

    /**
     * Serialises the profile's read-modify-write pairs for one home.
     *
     * Waiting is bounded and a timeout is not treated as failure: the writers
     * this guards are small file rewrites, and the CLI itself touches the same
     * manifest. A lock that cannot be taken within the budget leaves the write
     * to proceed - a lost update on a plugin row is a smaller harm than refusing
     * to record what the user asked for, and the write is itself atomic.
     */
    public static ManagedLock TryAcquireProfileWriter(string home, TimeSpan? budget = null)
        => AcquireNamed(AppPaths.ProfileWriterMutexName(home), "the profile of " + home,
                        budget ?? TimeSpan.FromSeconds(5));

    private static ManagedLock AcquireNamed(string name, string label, TimeSpan budget)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            var owner = false;
            try
            {
                owner = mutex.WaitOne(budget);
                if (!owner)
                {
                    Log.Warn($"another writer held the lock for {label}; proceeding without it");
                }
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
