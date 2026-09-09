using System;

namespace DShNative;

/** --stop: kill the server this app started for the selected home. */
public static class StopOnly
{
    public static int Run(Options o)
    {
        var killed = ServerManager.StopByHome(o);
        if (killed > 0)
        {
            Log.Info($"stopped the managed server for home {o.DshHome}");
            return 0;
        }
        Log.Warn($"no managed server found for home {o.DshHome} (lease absent, stale, or process identity changed)");
        return 1;
    }
}
