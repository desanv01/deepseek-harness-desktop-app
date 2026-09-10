using System;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/**
 * Named event used to hand focus to the instance that already owns a home.
 * The owner creates the event and waits on a background thread; a later launch
 * sets it and exits instead of opening a second window over the same server.
 */
public sealed class FocusSignal : IDisposable
{
    private readonly EventWaitHandle _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action _onFocus;
    private bool _disposed;

    private FocusSignal(EventWaitHandle handle, Action onFocus)
    {
        _handle = handle;
        _onFocus = onFocus;
    }

    /** Creates the owner-side handle; null when the OS refuses one. */
    public static FocusSignal? Create(string home, Action onFocus)
    {
        try
        {
            var handle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, AppPaths.FocusEventName(home));
            return new FocusSignal(handle, onFocus);
        }
        catch (Exception ex)
        {
            Log.Warn("could not create the focus event: " + ex.Message);
            return null;
        }
    }

    /** Starts waiting for focus requests; the callback runs off the UI thread. */
    public void Start()
    {
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _handle, _cts.Token.WaitHandle };
            while (!_cts.IsCancellationRequested)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (index != 0) return;
                try { _onFocus(); } catch (Exception ex) { Log.Warn("focus callback failed: " + ex.Message); }
            }
        });
    }

    /** Asks the owning instance to surface its window; false when nobody is listening. */
    public static bool Signal(string home)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(AppPaths.FocusEventName(home), out var handle)) return false;
            using (handle)
            {
                handle.Set();
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("could not signal the owning instance: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _handle.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
