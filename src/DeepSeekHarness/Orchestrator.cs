using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DShNative;

/**
 * Boots one harness server for the resolved DSH_HOME and keeps it owned for
 * the whole window lifetime. A second launch of the same home hands focus to
 * the running window instead of starting a rival server.
 */
public static class Orchestrator
{
    private enum Mode { Cancelled, Attach, Owned, Focused }

    private sealed class Outcome : IDisposable
    {
        public Mode Mode;
        public int ExitCode;
        public string? Error;
        public ManagedLock? Guard;
        public ServerLease? Lease;
        public string? PageUrl;

        public void Dispose()
        {
            Guard?.Dispose();
            Guard = null;
        }
    }

    public static int Run(Options o)
    {
        var settings = AppSettings.Load();
        o.ApplySettings(settings);
        return o.NoWindow ? RunHeadless(o, settings) : RunGui(o, settings);
    }

    // --no-window: boot test, no UI at all
    private static int RunHeadless(Options o, AppSettings settings)
    {
        var project = ResolveProject(o, settings, interactive: false);
        if (project == null) return 0;
        o.SetProject(project);

        var r = Boot(o, status: null, CancellationToken.None);
        using (r)
        {
            if (r.ExitCode != 0)
            {
                if (r.Error != null) Ui.Error(o, r.Error);
                return r.ExitCode;
            }
            if (r.Mode == Mode.Owned && r.Lease != null)
            {
                Log.Info("=== READY (boot test) ===");
                ServerManager.Stop(r.Lease);
                Log.Info("=== boot test complete (server stopped) ===");
            }
            return 0;
        }
    }

    // Normal launch: splash window with progress while the server boots.
    private static int RunGui(Options o, AppSettings settings)
    {
        var project = ResolveProject(o, settings, interactive: true);
        if (project == null)
        {
            Log.Info("no project selected; exiting");
            return 0;
        }
        o.SetProject(project);
        RememberSettings(o, settings, project);

        using var cts = new CancellationTokenSource();
        using var splash = new SplashForm(o.TargetLabel, () => { try { cts.Cancel(); } catch { } });

        splash.Show();
        var task = Task.Run(() => Boot(o, splash.SetStatus, cts.Token));

        // Pump the splash: it is a normal window (move, minimize, close all work)
        while (!splash.IsDisposed && !task.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
        if (!splash.IsDisposed)
        {
            splash.Finish();
            splash.Close();
        }

        Outcome r;
        try
        {
            r = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("unexpected boot error: " + ex);
            Ui.Error(o, "Unexpected error:\n" + ex.Message);
            return 1;
        }

        using (r)
        {
            if (r.ExitCode != 0)
            {
                if (r.Error != null) Ui.Error(o, r.Error);
                return r.ExitCode;
            }
            if (r.Mode is Mode.Cancelled or Mode.Focused) return 0;

            RunWindow(o, r);
            return 0;
        }
    }

    /** Persists the resolved choices and moves the project to the front of the recents. */
    private static void RememberSettings(Options o, AppSettings settings, string project)
    {
        settings.ProjectDir = project;
        settings.DshHome = o.ResolveHome();
        settings.Port = o.Port;
        settings.Update = o.Update;
        settings.Remember(project);
        settings.Save();
    }

    /**
     * The project to open: the explicit flag, then the remembered one, then an
     * interactive picker. Headless modes fall back to the current directory.
     */
    private static string? ResolveProject(Options o, AppSettings settings, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(o.ProjectDir) && Directory.Exists(o.ProjectDir)) return o.ProjectDir;
        if (!interactive)
        {
            var fallback = Environment.CurrentDirectory;
            Log.Info("no project resolved; using the current directory " + fallback);
            return fallback;
        }
        Log.Info("no project resolved yet; showing the project picker");
        var picked = ProjectPickerForm.Pick(null, settings);
        if (picked == null) return null;
        Log.Info("project selected: " + picked);
        return picked;
    }

    private static void RunWindow(Options o, Outcome outcome)
    {
        var pageUrl = outcome.PageUrl ?? o.Url;
        var project = o.ProjectDir ?? Environment.CurrentDirectory;
        Action? onFocus = null;
        using var signal = FocusSignal.Create(o.ResolveHome(), () => onFocus?.Invoke());

        try
        {
            using var form = new MainForm(pageUrl, AppPaths.WebView2Data, project);
            onFocus = form.FocusFromSignal;
            signal?.Start();
            Application.Run(form);
        }
        finally
        {
            if (outcome.Mode == Mode.Owned && outcome.Lease != null)
            {
                Log.Info("window closed; stopping the managed server");
                ServerManager.Stop(outcome.Lease);
            }
        }
        Log.Info("=== exit ===");
    }

    /**
     * The actual boot, with progress + cancellation. Runs on a background
     * task so the splash can stay responsive.
     */
    private static Outcome Boot(Options o, Action<string>? status, CancellationToken ct)
    {
        void Say(string msg) { status?.Invoke(msg); Log.Info(msg); }
        var home = o.ResolveHome();

        ManagedLock? guard = null;
        ServerLease? lease = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            // One server owns one DSH_HOME. Two servers writing the same
            // session and storage files would corrupt them.
            guard = ManagedLock.TryAcquireHome(home);
            if (!guard.Owner) return ForeignOwner(o, home, guard, status);

            // The previous owner may have crashed after writing a lease; its
            // job object already killed the child, so the record is stale.
            ServerManager.RemoveStaleLease(home);

            var tools = Tools.Discover();
            var toolFailure = PrepareTools(o, ref tools, status, ct, guard);
            if (toolFailure != null) return toolFailure;

            Say($"Starting the DeepSeek Harness server for {o.ProjectDir} ...");
            lease = ServerManager.Start(tools, o, status, ct);
            if (lease == null)
            {
                return Fail(22,
                    $"Failed to start the dsh web server.\nServer logs: {AppPaths.LogsDir}",
                    guard);
            }

            Say("Verifying the harness endpoint ...");
            var probe = NetProbe.Probe(lease.Url);
            if (probe.Status != EndpointStatus.DshReady)
            {
                var port = lease.Port;
                ServerManager.Stop(lease);
                lease = null;
                return Fail(21,
                    $"The server started on port {port} but did not answer as a verified DeepSeek Harness page.\n"
                    + $"Server logs: {AppPaths.LogsDir}\n\n{probe.Detail}",
                    guard);
            }

            Say("Server is ready");
            return new Outcome { Mode = Mode.Owned, Lease = lease, Guard = guard, PageUrl = lease.Url };
        }
        catch (OperationCanceledException)
        {
            // Splash closed: stop the server we started, leave everything else alone
            if (lease != null) ServerManager.Stop(lease);
            return new Outcome { Mode = Mode.Cancelled, Guard = guard };
        }
        catch (Exception ex)
        {
            Log.Error("owned flow error: " + ex);
            if (lease != null) ServerManager.Stop(lease);
            return Fail(23, "Error while booting the server:\n" + ex.Message, guard);
        }
    }

    /** --stop: kill the server this app started for the selected home. */
    public static int RunStop(Options o)
    {
        var home = o.ResolveHome();
        if (ServerManager.StopByHome(o) > 0)
        {
            Log.Info($"stopped the managed server for home {home}");
            return 0;
        }
        Log.Warn($"no managed server found for home {home} (lease absent, stale, or process identity changed)");
        return 1;
    }

    /** A live owner for this home: hand it focus, or attach when it cannot answer. */
    private static Outcome ForeignOwner(Options o, string home, ManagedLock guard, Action<string>? status)
    {
        var adopted = ServerManager.TryAdoptHome(o);
        if (adopted == null)
        {
            return Fail(40,
                $"Another DeepSeek Harness desktop instance owns the home\n{home}\n"
                + "but no verified server answered for it. Close that window (or run --stop) and retry.\n\n"
                + $"Logs: {AppPaths.LogsDir}",
                guard);
        }

        if (FocusSignal.Signal(home))
        {
            Log.Info("another window owns this home; brought it forward and exiting");
            return new Outcome { Mode = Mode.Focused, Guard = guard };
        }

        status?.Invoke("Another instance owns this DeepSeek Harness home; attaching to its server ...");
        Log.Info($"attached to the running server on {adopted.Address}:{adopted.Port}");
        return new Outcome { Mode = Mode.Attach, Guard = guard, Lease = adopted, PageUrl = adopted.Url };
    }

    /** Validates the CLI and optionally updates it; null when the boot may continue. */
    private static Outcome? PrepareTools(Options o, ref Tools tools, Action<string>? status, CancellationToken ct, ManagedLock guard)
    {
        if (tools.Node == null)
        {
            return Fail(10,
                "Node.js was not found on PATH.\nInstall it from https://nodejs.org and try again.",
                guard);
        }
        if (tools.DshMissing)
        {
            return Fail(11,
                "@deepseek-ai/dsh is not installed globally.\nRun:  npm install -g @deepseek-ai/dsh\n"
                + "Then launch this app again.",
                guard);
        }

        var usable = Tools.VerifyDsh(tools);
        if (!usable)
        {
            return Fail(12,
                "The installed @deepseek-ai/dsh CLI is incomplete or cannot load its runtime closure.\n"
                + "Repair it with:  npm install -g @deepseek-ai/dsh@latest\nThen retry.\n\n"
                + $"Logs: {AppPaths.LogsDir}",
                guard);
        }
        if (!o.Update) return null;

        status?.Invoke("Updating @deepseek-ai/dsh to the latest npm release ...");
        Log.Info("Updating @deepseek-ai/dsh to the latest npm release ...");
        var update = Updater.Run(tools, o, usable, ct);
        ct.ThrowIfCancellationRequested();
        if (!update.Usable)
        {
            return Fail(13,
                (update.Error ?? "The global dsh update failed")
                + "\n\nRepair it manually with:  npm install -g @deepseek-ai/dsh@latest\n"
                + $"Logs: {AppPaths.LogsDir}",
                guard);
        }

        tools = Tools.Discover();
        if (tools.DshMissing || !Tools.VerifyDsh(tools))
        {
            return Fail(14,
                "The dsh CLI is not usable after the update.\n"
                + "Run  npm install -g @deepseek-ai/dsh@latest  and retry.\n\n"
                + $"Logs: {AppPaths.LogsDir}",
                guard);
        }
        return null;
    }

    private static Outcome Fail(int code, string message, ManagedLock? guard)
        => new Outcome { ExitCode = code, Error = message, Guard = guard };
}
