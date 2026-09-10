using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/** Process identity captured before a detached child is handed to the drain task. */
public sealed record SpawnedProcess(int Pid, DateTime StartTimeUtc);

/** Optional wiring for a spawned child: workspace, environment, and lifecycle hooks. */
public sealed record SpawnOptions(
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    Action<string>? OnStdoutLine = null,
    Action<Process>? OnStarted = null);

/** Child-process helpers: output to files, kill whole trees. */
public static class Proc
{
    /** Runs to completion; exit code, -1 never started, -997 setup failed, -998 cancelled, -999 timed out (tree killed). */
    public static int Run(string exePath, string[] args, string? outFile, string? errFile, int timeoutMs, CancellationToken ct = default)
    {
        var psi = StartInfo(exePath, args, outFile != null, errFile != null);

        using var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start()) return -1;
        }
        catch (Exception ex)
        {
            Log.Error("process start failed: " + ex.Message);
            return -1;
        }

        FileStream? so = null;
        FileStream? se = null;
        Task? cout = null;
        Task? cerr = null;
        try
        {
            if (outFile != null)
            {
                so = OpenLog(outFile);
                cout = p.StandardOutput.BaseStream.CopyToAsync(so);
            }
            if (errFile != null)
            {
                se = OpenLog(errFile);
                cerr = p.StandardError.BaseStream.CopyToAsync(se);
            }

            var deadline = Environment.TickCount64 + timeoutMs;
            while (!p.WaitForExit(250))
            {
                if (ct.IsCancellationRequested)
                {
                    KillAndWait(p);
                    WaitQuietly(cout, cerr);
                    return -998;
                }
                if (Environment.TickCount64 >= deadline)
                {
                    KillAndWait(p);
                    WaitQuietly(cout, cerr);
                    return -999;
                }
            }
            WaitQuietly(cout, cerr);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            // The process is already live when output files are opened. Any
            // setup failure must terminate it or the caller receives a false
            // "failed to start" while an orphan keeps the port occupied.
            Log.Error("process execution failed: " + ex.Message);
            KillAndWait(p);
            WaitQuietly(cout, cerr);
            return -997;
        }
        finally
        {
            so?.Dispose();
            se?.Dispose();
        }
    }

    /** Fire-and-forget start (the server); output to files. Returns identity or null. */
    public static SpawnedProcess? Spawn(string exePath, string[] args, string outFile, string errFile)
        => Spawn(exePath, args, outFile, errFile, null);

    /** Fire-and-forget start with workspace, environment, and a stdout line hook. */
    public static SpawnedProcess? Spawn(string exePath, string[] args, string outFile, string errFile, SpawnOptions? options)
    {
        var psi = StartInfo(exePath, args, redirectOut: true, redirectErr: true);
        if (!string.IsNullOrEmpty(options?.WorkingDirectory)) psi.WorkingDirectory = options!.WorkingDirectory!;
        if (options?.Environment != null)
        {
            foreach (var entry in options.Environment) psi.Environment[entry.Key] = entry.Value;
        }

        Process? p = null;
        FileStream? so = null;
        FileStream? se = null;
        try
        {
            p = new Process { StartInfo = psi };
            if (!p.Start())
            {
                p.Dispose();
                p = null;
                return null;
            }
            var startTimeUtc = p.StartTime.ToUniversalTime();

            // The caller may need the live handle (job assignment) before any
            // output is consumed.
            try { options?.OnStarted?.Invoke(p); } catch (Exception ex) { Log.Warn("child start hook failed: " + ex.Message); }

            so = OpenLog(outFile);
            se = OpenLog(errFile);
            var pid = p.Id;
            _ = DrainAsync(p, so, se, options?.OnStdoutLine);
            p = null;
            so = null;
            se = null;
            return new SpawnedProcess(pid, startTimeUtc);
        }
        catch (Exception ex)
        {
            if (p != null) KillAndWait(p);
            so?.Dispose();
            se?.Dispose();
            Log.Error("spawn failed: " + ex.Message);
            return null;
        }
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /** Kill only when the PID still refers to the recorded process instance. */
    public static bool KillTreeIfStartTime(int pid, DateTime expectedStartTimeUtc)
    {
        if (pid <= 0 || expectedStartTimeUtc == DateTime.MinValue) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var actual = p.StartTime.ToUniversalTime();
            if ((actual - expectedStartTimeUtc).Duration() > TimeSpan.FromSeconds(2)) return false;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FileStream OpenLog(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous);
    }

    private static async Task DrainAsync(Process p, FileStream so, FileStream se, Action<string>? onStdoutLine)
    {
        try
        {
            await Task.WhenAll(
                DrainStdoutAsync(p, so, onStdoutLine),
                p.StandardError.BaseStream.CopyToAsync(se)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("server output drain failed: " + ex.Message);
        }
        finally
        {
            so.Dispose();
            se.Dispose();
            p.Dispose();
        }
    }

    /**
     * Copies stdout to the log line by line so the readiness line is observable
     * the instant dsh prints it, without waiting for the file to flush.
     */
    private static async Task DrainStdoutAsync(Process p, FileStream so, Action<string>? onStdoutLine)
    {
        using var reader = new StreamReader(p.StandardOutput.BaseStream);
        using var writer = new StreamWriter(so);
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            if (onStdoutLine != null)
            {
                try { onStdoutLine(line); } catch (Exception ex) { Log.Warn("stdout hook failed: " + ex.Message); }
            }
        }
        await writer.FlushAsync().ConfigureAwait(false);
    }

    /** One headless child-process specification; output redirection is per caller. */
    private static ProcessStartInfo StartInfo(string exePath, string[] args, bool redirectOut, bool redirectErr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOut,
            RedirectStandardError = redirectErr,
        };
        foreach (var argument in args) psi.ArgumentList.Add(argument);
        return psi;
    }

    private static void KillAndWait(Process? p)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(10_000);
        }
        catch { }
    }

    private static void WaitQuietly(Task? a, Task? b)
    {
        try { a?.Wait(5000); } catch { }
        try { b?.Wait(5000); } catch { }
    }
}
