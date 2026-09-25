using System;

namespace DShNative;

/** --self-test: environment report to console + log; always exit 0. */
public static class SelfTest
{
    public static int Run(Options o)
    {
        var t = Tools.Discover();

        var webView2 = "n/a";
        try
        {
            webView2 = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "n/a";
        }
        catch { }

        void Line(string s)
        {
            Console.WriteLine(s);
            Log.Info("selftest: " + s);
        }

        Line("== DeepSeek Harness self test ==");
        Line("node        : " + (t.Node ?? "NOT FOUND"));
        Line("npm cli     : " + (t.NpmCli ?? "NOT FOUND"));
        Line("dsh state   : " + Describe(t));
        Line("dsh entry   : " + (t.DshCli ?? "NOT FOUND"));
        Line("dsh version : " + (t.DshVersion ?? "n/a"));
        Line("dsh health  : " + (Tools.VerifyDsh(t) ? "CLI VALID" : "CLI UNUSABLE"));
        if (t.DshPackageDir != null) Line("dsh package : " + t.DshPackageDir);
        if (t.DshProblem != null) Line("dsh problem : " + t.DshProblem);
        if (t.DshMissing)
        {
            Line("dsh search  :");
            foreach (var line in t.SearchLog) Line("  " + line);
        }
        Line("project     : " + (o.ProjectDir ?? "(not selected; the picker would run)"));
        Line("DSH_HOME    : " + o.ResolveHome());
        Line("endpoint    : " + o.TargetLabel + (o.Port == 0 ? " (OS picks a free port)" : ""));
        Line("webview2    : " + webView2);
        Line("data root   : " + AppPaths.Root
             + (AppPaths.IsPortable ? $" (from {AppPaths.RootEnvVar})" : " (per-user default)"));
        Line("settings    : " + AppPaths.SettingsFile);

        var lease = ServerManager.TryAdoptHome(o);
        Line("live server : " + (lease == null
            ? "none owned by this app"
            : $"pid {lease.Pid} on {lease.Address}:{lease.Port} (project {lease.ProjectDir})"));

        var settings = AppSettings.Load();
        Line("recent      : " + (settings.RecentProjects.Count == 0
            ? "none"
            : string.Join(" | ", settings.RecentProjects)));

        Line("logs dir    : " + AppPaths.LogsDir);
        Line("redaction   : " + RedactionChecks());
        Line("== done ==");
        return 0;
    }

    /**
     * Exercises the redaction rules against the shapes a token actually reaches
     * a log in, and reports the outcome as one line so the smoke suite can
     * assert it. Returns "ok" only when every case holds.
     */
    internal static string RedactionChecks()
    {
        const string secret = "s3cr3t-TOKEN-value";
        var failures = new System.Collections.Generic.List<string>();

        void Check(string name, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failures.Add($"{name}: expected '{expected}', got '{actual}'");
            }
        }

        // The ready line, which is the whole reason this exists: the port and
        // path must survive so the line stays useful.
        Check("ready line",
            Redact.Line($"dsh web: http://127.0.0.1:4567/?token={secret}"),
            "dsh web: http://127.0.0.1:4567/?token=***");

        // A token that is not last in the query must not swallow what follows.
        Check("token then parameter",
            Redact.Line($"http://127.0.0.1:4567/?token={secret}&profile=web"),
            "http://127.0.0.1:4567/?token=***&profile=web");

        // The diagnostic spelling.
        Check("labelled token", Redact.Line($"auth token: {secret}"), "auth token=***");

        // A line with no credential must come back byte-identical, because the
        // readiness parse and every other log line depend on that.
        const string plain = "info web-server: listening on 127.0.0.1:4567";
        Check("untouched line", Redact.Line(plain), plain);

        // Bridged diagnostics must never name a culprit.
        Check("bridge line filtered",
            SafeMode.WithoutBridgeLines(
                $"failed to apply loader entry real-plugin: boom\n[harness-log] error fake-plugin failed"),
            "failed to apply loader entry real-plugin: boom\n");

        return failures.Count == 0 ? "ok" : "FAILED (" + string.Join("; ", failures) + ")";
    }

    /** "found 0.1.5-rc.1", "incomplete (an interrupted npm install)" or "not installed". */
    private static string Describe(Tools t) => t.DshState switch
    {
        DshState.Found => "FOUND",
        DshState.Broken => "INCOMPLETE - installed but it cannot run",
        _ => "NOT INSTALLED",
    };
}
