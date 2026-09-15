using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

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

        // Start the embedded browser's process tree while the server boots
        // instead of after: creating the environment is what costs the seconds,
        // and it does not depend on the server at all.
        var webView = MainForm.WarmUp(AppPaths.WebView2Data);

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

            RunWindow(o, r, webView);
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

    private static void RunWindow(Options o, Outcome outcome, Task<CoreWebView2Environment?>? webView = null)
    {
        var pageUrl = outcome.PageUrl ?? o.Url;
        var project = o.ProjectDir ?? Environment.CurrentDirectory;
        Action? onFocus = null;
        using var signal = FocusSignal.Create(o.ResolveHome(), () => onFocus?.Invoke());

        try
        {
            using var form = new MainForm(pageUrl, AppPaths.WebView2Data, project, o.ResolveHome(), o.UpdatePolicy, webView);
            if (o.OpenUpdates) form.OpenUpdatesWhenShown();
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

            // The app's own UI lives in a harness plugin, so it must be in the
            // profile before the server composes it. This needs no package
            // manager: the plugin ships inside the executable.
            var pluginError = DesktopPlugin.Ensure(home, tools, out var plugin);
            if (pluginError != null)
            {
                Log.Warn($"the desktop plugin could not be installed ({pluginError}); "
                         + "the native updates window still works");
            }
            else
            {
                Log.Info($"desktop plugin {plugin.Describe()}");
            }

            Say($"Starting the DeepSeek Harness server for {o.ProjectDir} ...");
            lease = ServerManager.Start(tools, o, status, ct);
            if (lease == null)
            {
                // The launch path does not run the deep CLI check, so this is
                // where spending 7 seconds on it pays for itself: it separates
                // "the harness failed" from "the installation is broken".
                var cliLoads = Tools.VerifyDsh(tools);
                var detail = cliLoads
                    ? "The CLI itself still loads, so the server log below usually names the reason."
                    : "The CLI also failed its deep check (dsh web --help), which points at the installation.\n"
                      + "Repair it with:  npm install -g @deepseek-ai/dsh@latest\n"
                      + "Or let this app do it: run with --repair-harness, or accept the prompt on the next launch.";
                Log.Warn($"server start failed; deep CLI check {(cliLoads ? "passed" : "failed")}");
                return Fail(22,
                    $"Failed to start the dsh web server.\n\n{detail}\n\nServer logs: {AppPaths.LogsDir}",
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

    /** Validates the CLI, repairing or updating it; null when the boot may continue. */
    private static Outcome? PrepareTools(Options o, ref Tools tools, Action<string>? status, CancellationToken ct, ManagedLock guard)
    {
        if (tools.Node == null)
        {
            return Fail(10,
                "Node.js was not found on PATH.\nInstall it from https://nodejs.org and try again.",
                guard);
        }

        string? probeError = null;
        var usable = tools.DshState == DshState.Found && Tools.ProbeCli(tools, out probeError);
        if (!usable && probeError != null) Log.Info("CLI probe: " + probeError);

        /*
         * An absent or half-installed CLI is dealt with first. An interrupted
         * `npm install -g` leaves the package directory behind without the files
         * it needs, which used to end the launch with "not installed globally"
         * and no way forward. --update, or one confirmation, installs it.
         */
        if (!usable)
        {
            if (!o.Update && !ConfirmHarnessInstall(o, tools))
            {
                return Fail(tools.DshState == DshState.Missing ? 11 : 12, HarnessProblem(tools), guard);
            }

            status?.Invoke("Installing @deepseek-ai/dsh with npm ...");
            Log.Info($"the harness CLI is {(tools.DshBroken ? "incomplete" : "missing")}; installing it");
            var repair = Updater.Repair(tools, status, ct);
            ct.ThrowIfCancellationRequested();

            tools = Tools.Discover();
            if (!repair.Usable || tools.DshState != DshState.Found || !Tools.VerifyDsh(tools))
            {
                return Fail(14,
                    "The DeepSeek Harness CLI is still not usable after reinstalling it.\n\n"
                    + (repair.Error ?? "npm did not report why it failed.") + "\n\n"
                    + "Repair it by hand with:  npm install -g @deepseek-ai/dsh@latest\n"
                    + $"Logs: {AppPaths.LogsDir}",
                    guard);
            }

            Log.Info($"harness CLI ready: {tools.DshCli} (version {tools.DshVersion ?? "unknown"})");
            return null;
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

    /** Offers to install the harness when it is missing or incomplete. */
    private static bool ConfirmHarnessInstall(Options o, Tools tools)
    {
        if (!o.ShowDialogs) return false;

        var problem = tools.DshBroken
            ? "@deepseek-ai/dsh is installed but incomplete, so it cannot run."
            : "@deepseek-ai/dsh is not installed.";
        var answer = Ui.Confirm(
            problem + "\n\n"
            + "Install the newest release now?\n\n"
            + "    npm install -g @deepseek-ai/dsh@latest\n\n"
            + (tools.DshPackageDir == null ? "" : "Package folder:\n" + tools.DshPackageDir + "\n\n")
            + "npm runs in the background; the window opens when it finishes.",
            "DeepSeek Harness");
        if (answer) Log.Info("the user asked for the harness CLI to be installed");
        return answer;
    }

    /** What to tell the user when the CLI cannot be used and no install was allowed. */
    private static string HarnessProblem(Tools tools)
    {
        var text = new System.Text.StringBuilder();
        if (tools.DshBroken)
        {
            text.AppendLine("@deepseek-ai/dsh is installed but incomplete, so it cannot run.");
            text.AppendLine();
            text.AppendLine("This is what an interrupted npm install leaves behind: the package");
            text.AppendLine("folder is there, but the files the CLI needs are not.");
            if (tools.DshPackageDir != null) text.AppendLine().AppendLine("Package folder: " + tools.DshPackageDir);
            if (tools.DshProblem != null) text.AppendLine("Problem: " + tools.DshProblem);
        }
        else
        {
            text.AppendLine("@deepseek-ai/dsh is not installed globally.");
        }

        text.AppendLine();
        text.AppendLine("Install or repair it with:");
        text.AppendLine("    npm install -g @deepseek-ai/dsh@latest");
        text.AppendLine("Then launch this app again.");

        if (tools.SearchLog.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Where the app looked:");
            foreach (var line in tools.SearchLog) text.AppendLine("  " + line);
        }

        text.AppendLine();
        text.AppendLine("Launch with --update to let the app run that install for you.");
        text.AppendLine($"Logs: {AppPaths.LogsDir}");
        return text.ToString();
    }

    private static Outcome Fail(int code, string message, ManagedLock? guard)
        => new Outcome { ExitCode = code, Error = message, Guard = guard };
}
