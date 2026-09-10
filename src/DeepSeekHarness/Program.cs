using System;
using System.Windows.Forms;

namespace DShNative;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
            if (opts.SelfTest) return SelfTest.Run(opts);
            if (opts.Stop) return StopOnly.Run(opts);
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
