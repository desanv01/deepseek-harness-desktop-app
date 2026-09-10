using System;
using System.IO;
using System.Threading;

namespace DShNative;

public sealed class UpdateResult
{
    public bool Usable { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }
}

/** One serialized npm update of the global dsh installation. */
public static class Updater
{
    /** Updates to the latest npm release; used by the launch-time --update path. */
    public static UpdateResult Run(Tools t, Options o, bool existingInstallUsable, CancellationToken ct = default)
    {
        Log.Info($"checking for the latest @deepseek-ai/dsh on npm for {o.Url} ...");
        return RunCore(t, "@latest", existingInstallUsable, ct);
    }

    /** Installs one explicit version; used by the in-app harness update action. */
    public static UpdateResult Install(Tools t, string version, CancellationToken ct = default)
        => RunCore(t, "@" + version, existingInstallUsable: true, ct);

    private static UpdateResult RunCore(Tools t, string versionSpec, bool existingInstallUsable, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t.Node) || string.IsNullOrEmpty(t.NpmCli))
        {
            const string message = "node or npm was not found; automatic repair/update is unavailable";
            Log.Warn(message);
            return new UpdateResult { Usable = existingInstallUsable, ExitCode = -996, Error = message };
        }

        using var mutex = new Mutex(initiallyOwned: false, AppPaths.UpdateMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(60));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
                Log.Warn("recovered an abandoned npm-update mutex");
            }
            if (!ownsMutex)
            {
                const string busyMessage = "another launcher is updating @deepseek-ai/dsh; retry after it finishes";
                Log.Warn(busyMessage);
                return new UpdateResult { Usable = existingInstallUsable, ExitCode = -995, Error = busyMessage };
            }

            var node = t.Node;
            var npm = t.NpmCli;
            if (node == null || npm == null)
            {
                const string missingMessage = "node or npm was not found; automatic repair/update is unavailable";
                return new UpdateResult { Usable = existingInstallUsable, ExitCode = -996, Error = missingMessage };
            }

            var outFile = AppPaths.NewLogPath("npm-update", ".out.log");
            var errFile = AppPaths.NewLogPath("npm-update", ".err.log");
            var packageSpec = "@deepseek-ai/dsh" + versionSpec;
            Log.Info($"installing {packageSpec} ...");
            var code = Proc.Run(node, new[]
            {
                npm, "install", "-g", packageSpec,
                "--no-audit", "--no-fund", "--loglevel=error"
            }, outFile, errFile, 300_000, ct);

            var refreshed = Tools.Discover();
            var usable = Tools.VerifyDsh(refreshed);
            if (code == 0 && usable)
            {
                Log.Info("npm auto-update finished and the dsh CLI validated successfully");
                return new UpdateResult { Usable = true, ExitCode = code };
            }

            var tail = "";
            try
            {
                if (File.Exists(errFile))
                {
                    var txt = File.ReadAllText(errFile);
                    tail = txt.Length > 300 ? txt[^300..] : txt;
                }
            }
            catch { }

            if (usable)
            {
                Log.Warn($"npm auto-update failed (exit {code}) but the installed dsh still validates: {tail.ReplaceLineEndings(" ")}");
                return new UpdateResult { Usable = true, ExitCode = code, Error = tail };
            }

            var message = $"npm auto-update left @deepseek-ai/dsh unusable (exit {code}). {tail.ReplaceLineEndings(" ")}".Trim();
            Log.Error(message);
            return new UpdateResult { Usable = false, ExitCode = code, Error = message };
        }
        finally
        {
            if (ownsMutex)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}
