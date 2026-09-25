using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/** One owned dsh web child, identified by process start time and its printed URL. */
public sealed class ServerLease
{
    public int Pid { get; }
    public DateTime StartTimeUtc { get; }
    public string Address { get; }
    public int Port { get; }

    /** The authenticated URL dsh printed, including its ?token= query. */
    public string Url { get; }
    public string Token { get; }
    public string ProjectDir { get; }
    public string Home { get; }
    public string OutLog { get; }
    public string ErrLog { get; }
    internal JobObject? Job { get; }

    internal ServerLease(
        int pid,
        DateTime startTimeUtc,
        string address,
        int port,
        string url,
        string token,
        string projectDir,
        string home,
        string outLog,
        string errLog,
        JobObject? job)
    {
        Pid = pid;
        StartTimeUtc = startTimeUtc;
        Address = address;
        Port = port;
        Url = url;
        Token = token;
        ProjectDir = projectDir;
        Home = home;
        OutLog = outLog;
        ErrLog = errLog;
        Job = job;
    }
}

internal sealed class PersistedServerLease
{
    public int Pid { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string Home { get; set; } = "";
}

/**
 * Owns the dsh web child for one DSH_HOME. dsh is started with --port 0, so
 * the OS picks a free port and this class learns the real endpoint from the
 * ready line dsh prints:
 *   dsh web: http://127.0.0.1:4567/?token=<token>
 * That line is the harness's own readiness signal, which removes both the
 * port-conflict and the "is this really dsh?" guesses.
 */
public static class ServerManager
{
    private static readonly Regex ReadyLine = new(@"^dsh web:\s*(?<url>http://\S+)", RegexOptions.Compiled);

    /** Why the last start failed, and where its output went, for diagnosis. */
    public static string? LastFailure { get; private set; }
    public static string? LastOutLog { get; private set; }
    public static string? LastErrLog { get; private set; }

    /** Spawns `dsh web --no-open --port 0` and waits for its ready line. */
    public static ServerLease? Start(Tools t, Options o, Action<string>? status, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t.Node) || string.IsNullOrEmpty(t.DshCli)) return null;
        var node = t.Node;
        var dsh = t.DshCli;

        var outLog = AppPaths.NewLogPath("server", ".out.log");
        var errLog = AppPaths.NewLogPath("server", ".err.log");
        LastFailure = null;
        LastOutLog = outLog;
        LastErrLog = errLog;
        var home = o.ResolveHome();
        var project = o.ProjectDir ?? Environment.CurrentDirectory;
        try { Directory.CreateDirectory(home); }
        catch (Exception ex) { Log.Warn("could not create the DSH_HOME directory: " + ex.Message); }

        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Kill-on-close is the shutdown guarantee, so keep-alive mode turns it
        // off deliberately: the server must outlive this process.
        var job = JobObject.Create(killOnClose: !o.KeepServerRunning);
        if (o.KeepServerRunning)
        {
            Log.Info("keep-alive is on: the harness will keep running after this window closes");
        }
        var args = new[] { dsh, "web", "--no-open", "--host", o.Address, "--port", o.Port.ToString() };
        // The harness runs from the project so that the project is what scopes
        // the workspace and the session's recorded cwd; DSH_HOME is explicit so
        // plugin resolution never depends on where that is.
        var environment = HarnessEnvironment.Hardened(home);
        var spawnOptions = new SpawnOptions(
            WorkingDirectory: project,
            Environment: environment,
            OnStdoutLine: line =>
            {
                var match = ReadyLine.Match(line);
                if (match.Success) ready.TrySetResult(match.Groups["url"].Value);
            },
            OnStarted: process =>
            {
                if (job != null && !job.Assign(process))
                {
                    // Without the job the app cannot guarantee cleanup, but the
                    // child is already running: keep going and rely on the
                    // explicit stop path rather than leaking a second server.
                    job.Dispose();
                    job = null;
                }
            });

        Log.Info($"starting managed server: node {t.DshCli} web --no-open --host {o.Address} --port {o.Port} "
                 + $"(project {project}, DSH_HOME {home})");
        var spawned = Proc.Spawn(node, args, outLog, errLog, spawnOptions);
        if (spawned == null)
        {
            job?.Dispose();
            Log.Error("failed to start the managed server");
            return null;
        }

        status?.Invoke("Waiting for the harness to report its address ...");
        var deadline = DateTime.UtcNow.AddSeconds(o.ReadyTimeoutSec);
        while (!ready.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) break;
            if (!Proc.IsAlive(spawned.Pid))
            {
                LastFailure = "the managed server exited before it printed a ready line";
                Log.Error(LastFailure);
                break;
            }
            ready.Task.Wait(200);
        }

        if (ct.IsCancellationRequested)
        {
            StopProcess(spawned, job);
            return null;
        }
        if (!ready.Task.IsCompleted)
        {
            LastFailure = $"the managed server did not print a ready line within {o.ReadyTimeoutSec}s";
            Log.Error($"{LastFailure}; logs: {outLog}");
            StopProcess(spawned, job);
            return null;
        }

        var url = ready.Task.Result;
        if (!TryParseEndpoint(url, out var port, out var token))
        {
            Log.Error($"the server ready line did not contain a usable endpoint: {Redact.Line(url)}");
            StopProcess(spawned, job);
            return null;
        }

        var lease = new ServerLease(
            spawned.Pid, spawned.StartTimeUtc, o.Address, port, url, token,
            project, home, outLog, errLog, job);
        WriteLease(lease);
        Log.Info($"managed server pid {lease.Pid} is ready at {lease.Address}:{lease.Port} (logs: {outLog})");
        return lease;
    }

    /** Reads a live, verified lease for this home written by another instance. */
    public static ServerLease? TryAdoptHome(Options o)
    {
        var stored = ReadLease(AppPaths.HomeLeaseFile(o.ResolveHome()));
        if (stored == null) return null;
        if (!Proc.IsAlive(stored.Pid))
        {
            Log.Info("the recorded server for this home is no longer running; ignoring its lease");
            return null;
        }
        if (!TryParseEndpoint(stored.Url, out var port, out var token)) return null;
        var probe = NetProbe.Probe(stored.Url);
        if (probe.Status != EndpointStatus.DshReady)
        {
            Log.Warn($"the recorded server for this home is not a verified harness endpoint ({probe.Status})");
            return null;
        }
        return new ServerLease(
            stored.Pid, stored.StartTimeUtc, stored.Address, port, stored.Url, token,
            stored.ProjectDir, stored.Home, "", "", null);
    }

    /** Deletes a lease whose process is gone. */
    public static void RemoveStaleLease(string home)
    {
        var path = AppPaths.HomeLeaseFile(home);
        var stored = ReadLease(path);
        if (stored == null) return;
        if (Proc.IsAlive(stored.Pid)) return;
        Log.Info("removing a stale server lease for this home");
        RemoveLease(path);
    }

    /** Stops the exact owned child; never touches a reused PID. */
    public static void Stop(ServerLease lease)
    {
        if (lease.Pid > 0)
        {
            if (Proc.KillTreeIfStartTime(lease.Pid, lease.StartTimeUtc))
                Log.Info($"stopped managed server pid {lease.Pid} on {lease.Address}:{lease.Port}");
            else
                Log.Warn($"managed server lease no longer matches pid {lease.Pid}; refused to kill a reused process");
        }
        try { lease.Job?.Dispose(); } catch { }
        RemoveLeaseIfMatches(lease);
    }

    /** --stop: kill only the exact lease recorded for the requested home. */
    public static int StopByHome(Options o)
    {
        var path = AppPaths.HomeLeaseFile(o.ResolveHome());
        try
        {
            var stored = ReadLease(path);
            if (stored == null) return 0;
            if (stored.Pid <= 0)
            {
                RemoveLease(path);
                return 0;
            }

            if (Proc.KillTreeIfStartTime(stored.Pid, stored.StartTimeUtc))
            {
                Log.Info($"--stop: killed managed server pid {stored.Pid} on {stored.Address}:{stored.Port}");
                RemoveLease(path);
                Log.Info("--stop complete");
                return 1;
            }

            Log.Warn("--stop: the recorded PID is dead or no longer belongs to this server lease; no process was killed");
            RemoveLease(path);
        }
        catch (Exception ex)
        {
            Log.Warn("--stop error: " + ex.Message);
        }
        return 0;
    }

    private static void StopProcess(SpawnedProcess spawned, JobObject? job)
    {
        Proc.KillTreeIfStartTime(spawned.Pid, spawned.StartTimeUtc);
        try { job?.Dispose(); } catch { }
    }

    private static bool TryParseEndpoint(string url, out int port, out string token)
    {
        port = 0;
        token = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Port <= 0) return false;
        port = uri.Port;
        var query = uri.Query;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            if (part[..separator].Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                token = Uri.UnescapeDataString(part[(separator + 1)..]);
                break;
            }
        }
        return token.Length > 0;
    }

    private static void WriteLease(ServerLease lease)
    {
        var path = AppPaths.HomeLeaseFile(lease.Home);
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = new PersistedServerLease
            {
                Pid = lease.Pid,
                StartTimeUtc = lease.StartTimeUtc,
                Address = lease.Address,
                Port = lease.Port,
                Url = lease.Url,
                Token = lease.Token,
                ProjectDir = lease.ProjectDir,
                Home = lease.Home,
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("could not persist the managed server lease: " + ex.Message);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static PersistedServerLease? ReadLease(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<PersistedServerLease>(File.ReadAllText(path));
            if (value == null
                || value.Pid <= 0
                || value.StartTimeUtc == DateTime.MinValue
                || string.IsNullOrWhiteSpace(value.Address)
                || string.IsNullOrWhiteSpace(value.Url)
                || value.Port is < 1 or > 65_535)
                return null;
            return value;
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the managed server lease: " + ex.Message);
            return null;
        }
    }

    private static void RemoveLeaseIfMatches(ServerLease lease)
    {
        var path = AppPaths.HomeLeaseFile(lease.Home);
        var stored = ReadLease(path);
        if (stored != null
            && stored.Pid == lease.Pid
            && stored.Port == lease.Port
            && stored.Address == lease.Address
            && stored.StartTimeUtc == lease.StartTimeUtc)
            RemoveLease(path);
    }

    private static void RemoveLease(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
