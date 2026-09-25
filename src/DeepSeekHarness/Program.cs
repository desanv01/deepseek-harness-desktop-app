using System;
using System.Windows.Forms;

namespace DShNative;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The update helper runs before anything else exists: it is started by
        // the app that is about to exit, waits for it, swaps the executable, and
        // relaunches. It never creates a window and never parses other flags.
        if (args.Length > 0 && string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            return args.Length > 1 ? UpdateApply.Run(args[1]) : 2;
        }

        // Visual styles and DPI awareness must be decided before the first
        // window exists, so they run before argument parsing and the splash.
        try
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch (Exception ex)
        {
            Log.Warn("could not apply visual styles or DPI awareness: " + ex.Message);
        }

        var opts = new Options();
        try
        {
            AppPaths.Ensure();
            AppPaths.PruneLogs();
            opts = Options.Parse(args);
            Log.Info($"=== DeepSeek Harness desktop start === endpoint {opts.TargetLabel}, "
                     + $"project {opts.ProjectDir ?? "(unresolved)"}, DSH_HOME {opts.ResolveHome()}, "
                     + $"auto-update {(opts.Update ? "on" : "off")}, no-window {opts.NoWindow}");
            Log.Info($"app data root: {AppPaths.Root}"
                     + (AppPaths.IsPortable ? $" (from {AppPaths.RootEnvVar})" : ""));
            if (opts.SelfTest) return SelfTest.Run(opts);
            if (opts.ExitSafeMode) return SafeMode.RunCli(opts);
            if (opts.CheckHarness) return HarnessUpdate.RunCheck();
            if (opts.CheckUpdates) return UpdateCli.Run(opts);
            if (opts.CheckPlugins) return PluginUpdateCheck.RunCli(opts);
            if (opts.InstallUpdate) return UpdateCli.RunInstall(opts);
            if (opts.RepairHarness) return HarnessCli.RunRepair();
            if (opts.BridgeSelfTest) return DesktopBridge.RunSelfTest();
            if (opts.InstallPlugin) return DesktopPluginCli.Run(opts);
            if (opts.InstallLogBridge) return DesktopPluginCli.RunInstallLogBridge(opts);
            if (opts.ImportWebHome) return WebHomeImport.RunCli(opts, skip: false);
            if (opts.SkipWebHome) return WebHomeImport.RunCli(opts, skip: true);
            if (opts.AddPlugin != null) return PluginCli.RunAdd(opts, opts.AddPlugin);
            if (opts.RemovePlugin != null) return PluginCli.RunRemove(opts, opts.RemovePlugin);
            if (opts.PluginList) return PluginCli.RunList(opts);
            if (opts.EnablePlugin != null) return PluginCli.RunSetEnabled(opts, opts.EnablePlugin, enabled: true);
            if (opts.DisablePlugin != null) return PluginCli.RunSetEnabled(opts, opts.DisablePlugin, enabled: false);
            if (opts.Stop) return Orchestrator.RunStop(opts);
            return Orchestrator.Run(opts);
        }
        catch (ArgumentException ex)
        {
            Log.Error("invalid command-line arguments: " + ex.Message);
            try { Console.Error.WriteLine("DeepSeek Harness: " + ex.Message); } catch { }
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("unhandled exception: " + ex);
            Ui.Error(opts, "Unexpected error:\n" + ex.Message);
            return 1;
        }
    }
}
