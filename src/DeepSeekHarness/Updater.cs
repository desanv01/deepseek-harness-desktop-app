using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace DShNative;

public sealed class UpdateResult
{
    public bool Usable { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }
}

/**
 * One serialized npm update of the global dsh installation.
 *
 * `npm install -g` on Windows is not transactional: an interrupted or failed
 * run can leave the package directory without the files the CLI needs, which is
 * exactly how a machine ends up with a dsh shim that cannot start. So the
 * installed package is moved aside before npm touches it, and put back when the
 * result does not validate.
 */
public static class Updater
{
    /** Updates to the latest npm release; used by the launch-time --update path. */
    public static UpdateResult Run(Tools t, Options o, bool existingInstallUsable, CancellationToken ct = default)
    {
        Log.Info($"checking for the latest @deepseek-ai/dsh on npm for {o.Url} ...");
        return RunCore(t, "@latest", existingInstallUsable, ct, status: null);
    }

    /** Installs one explicit version; used by the in-app harness update action. */
    public static UpdateResult Install(Tools t, string version, CancellationToken ct = default)
        => RunCore(t, "@" + version, existingInstallUsable: true, ct, status: null);

    /**
     * Puts a working CLI back. A package an earlier attempt moved aside is
     * restored when it still validates - that is faster than npm and works
     * offline - otherwise the newest release is installed.
     */
    public static UpdateResult Repair(Tools t, Action<string>? status = null, CancellationToken ct = default)
    {
        var restored = TryRestoreBackup(t, status);
        if (restored is { Usable: true }) return restored;
        return RunCore(t, "@latest", existingInstallUsable: false, ct, status);
    }

    private static UpdateResult RunCore(
        Tools t,
        string versionSpec,
        bool existingInstallUsable,
        CancellationToken ct,
        Action<string>? status)
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

            // Keep the working install until the replacement has proven itself.
            var backup = existingInstallUsable ? MovePackageAside(t, status) : null;
            PruneBackups(t);

            var outFile = AppPaths.NewLogPath("npm-update", ".out.log");
            var errFile = AppPaths.NewLogPath("npm-update", ".err.log");
            var packageSpec = "@deepseek-ai/dsh" + versionSpec;
            Log.Info($"installing {packageSpec} ...");
            status?.Invoke($"Installing {packageSpec} with npm ...");
            var code = Proc.Run(node, new[]
            {
                npm, "install", "-g", packageSpec,
                "--no-audit", "--no-fund", "--loglevel=error"
            }, outFile, errFile, 300_000, ct);

            var refreshed = Tools.Discover();
            var usable = refreshed.DshState == DshState.Found && Tools.VerifyDsh(refreshed);
            if (code == 0 && usable)
            {
                DeleteBackup(backup);
                Log.Info("npm install finished and the dsh CLI validated successfully");
                // A new harness can be incompatible with a plugin the loader
                // applies at boot, so the next start is what verifies it - and it
                // is recorded as such, so a failure there can be attributed.
                SafeMode.NoteHarnessUpdate(refreshed, refreshed.DshVersion);
                return new UpdateResult { Usable = true, ExitCode = code };
            }

            var tail = ReadTail(errFile);

            if (usable)
            {
                // npm complained, but the CLI on disk works: keep it.
                DeleteBackup(backup);
                Log.Warn($"npm install failed (exit {code}) but the installed dsh still validates: {tail}");
                return new UpdateResult { Usable = true, ExitCode = code, Error = tail };
            }

            // The install left nothing usable. Put the previous one back, so a
            // failed update does not cost the user a working harness.
            var rollback = RestoreBackup(t, backup, status);

            var message = $"npm install left @deepseek-ai/dsh unusable (exit {code}). {tail}".Trim();
            if (rollback)
            {
                message += " The previous installation was restored.";
                Log.Error(message);
                return new UpdateResult { Usable = true, ExitCode = code, Error = message };
            }

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

    // ---- keeping a working install safe --------------------------------------

    /** Moves the installed package aside; null when there is nothing to keep. */
    private static string? MovePackageAside(Tools t, Action<string>? status)
    {
        var packageDir = t.DshPackageDir;
        if (string.IsNullOrEmpty(packageDir) || !Directory.Exists(packageDir)) return null;

        var backup = packageDir + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            status?.Invoke("Keeping the current installation while it is replaced ...");
            Directory.Move(packageDir, backup);
            Log.Info($"kept the installed harness at {backup} until the new one validates");
            return backup;
        }
        catch (Exception ex)
        {
            // A running dsh can hold the files; installing over them still works,
            // it just loses the safety net.
            Log.Warn($"could not move the installed harness aside ({ex.Message}); installing over it");
            return null;
        }
    }

    /** Puts a package moved aside back, deleting whatever npm left behind. */
    private static bool RestoreBackup(Tools t, string? backup, Action<string>? status)
    {
        if (string.IsNullOrEmpty(backup) || !Directory.Exists(backup)) return false;

        var packageDir = t.DshPackageDir
                         ?? Path.Combine(Path.GetDirectoryName(backup)!, Path.GetFileName(backup).Split(".backup-")[0]);
        try
        {
            status?.Invoke("Restoring the previous harness installation ...");
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
            Directory.Move(backup, packageDir);
        }
        catch (Exception ex)
        {
            Log.Error($"could not restore {backup}: {ex.Message} - it is still on disk");
            return false;
        }

        var refreshed = Tools.Discover();
        var usable = refreshed.DshState == DshState.Found && Tools.VerifyDsh(refreshed);
        Log.Info(usable
            ? $"the previous harness installation was restored from {backup} and validates"
            : $"the previous harness installation was restored from {backup} but did not validate");
        return usable;
    }

    /** Restores the newest package an earlier attempt left aside, if it works. */
    private static UpdateResult? TryRestoreBackup(Tools t, Action<string>? status)
    {
        var packageDir = t.DshPackageDir;
        if (string.IsNullOrEmpty(packageDir)) return null;

        var parent = Path.GetDirectoryName(packageDir);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;

        var pattern = Path.GetFileName(packageDir) + ".backup-*";
        var backups = Directory.GetDirectories(parent, pattern);
        if (backups.Length == 0) return null;

        Array.Sort(backups, (a, b) => string.CompareOrdinal(b, a));
        Log.Info($"a kept copy of the harness is available at {backups[0]}");
        var restored = RestoreBackup(t, backups[0], status);
        return restored
            ? new UpdateResult { Usable = true, ExitCode = 0 }
            : null;
    }

    /** Removes kept copies that are too old to be useful. */
    private static void PruneBackups(Tools t, int keepDays = 7)
    {
        var packageDir = t.DshPackageDir;
        var parent = string.IsNullOrEmpty(packageDir) ? null : Path.GetDirectoryName(packageDir);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(parent, "dsh.backup-*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow.AddDays(-keepDays)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    Log.Info($"removed the stale harness copy {dir}");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not prune old harness copies: " + ex.Message);
        }
    }

    private static void DeleteBackup(string? backup)
    {
        if (string.IsNullOrEmpty(backup)) return;
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Log.Info($"removed the kept copy {backup}");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remove {backup}: {ex.Message}");
        }
    }

    private static string ReadTail(string errFile)
    {
        try
        {
            if (!File.Exists(errFile)) return "";
            var text = File.ReadAllText(errFile).ReplaceLineEndings(" ").Trim();
            return text.Length > 300 ? text[^300..] : text;
        }
        catch
        {
            return "";
        }
    }
}

/**
 * --repair-harness: put a working global CLI back and say what happened.
 * Exit codes: 0 usable afterwards, 1 still broken, 10 node or npm missing.
 */
public static class HarnessCli
{
    public static int RunRepair()
    {
        var tools = Tools.Discover();
        Console.WriteLine("== DeepSeek Harness CLI repair ==");
        Console.WriteLine($"entry before : {tools.DshCli ?? "none"}");
        Console.WriteLine($"state before : {Describe(tools)}");
        if (tools.DshPackageDir != null) Console.WriteLine($"package      : {tools.DshPackageDir}");
        if (tools.DshProblem != null) Console.WriteLine($"problem      : {tools.DshProblem}");

        if (tools.Node == null || tools.NpmCli == null)
        {
            Console.Error.WriteLine("node or npm was not found on PATH; nothing can be installed");
            return 10;
        }

        var result = Updater.Repair(tools, message => Console.WriteLine("  " + message));
        if (result.Error != null) Console.WriteLine("note         : " + result.Error);

        var after = Tools.Discover();
        var usable = after.DshState == DshState.Found && Tools.VerifyDsh(after);
        Console.WriteLine($"entry after  : {after.DshCli ?? "none"}");
        Console.WriteLine($"version      : {after.DshVersion ?? "unknown"}");
        Console.WriteLine($"state after  : {Describe(after)}");
        Console.WriteLine(usable ? "== the harness CLI works ==" : "== the harness CLI is still not usable ==");
        return usable ? 0 : 1;
    }

    private static string Describe(Tools t) => t.DshState switch
    {
        DshState.Found => "usable",
        DshState.Broken => "installed but incomplete",
        _ => "not installed",
    };
}
