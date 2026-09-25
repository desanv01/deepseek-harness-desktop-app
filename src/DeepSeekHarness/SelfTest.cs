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
        Line("plugin spec : " + SpecAnchoringChecks());
        Line("env guard   : " + EnvironmentGuardCheck(t));
        Line("profile lock: " + ProfileLockChecks());
        Line("== done ==");
        return 0;
    }

    /**
     * The profile writer lock. Two things must hold: a nested acquisition on
     * this thread succeeds (the writers call each other, so a non-reentrant lock
     * would deadlock the app), and two homes never share one lock.
     */
    internal static string ProfileLockChecks()
    {
        var failures = new System.Collections.Generic.List<string>();
        const string homeA = @"C:\home-a";
        const string homeB = @"C:\home-b";

        if (AppPaths.ProfileWriterMutexName(homeA) == AppPaths.ProfileWriterMutexName(homeB))
        {
            failures.Add("two homes share one lock name");
        }
        if (AppPaths.ProfileWriterMutexName(homeA) == AppPaths.HomeMutexName(homeA))
        {
            failures.Add("the profile lock and the home lock are the same mutex");
        }

        using (var first = ManagedLock.TryAcquireProfileWriter(homeA))
        {
            if (!first.Owner) failures.Add("the first acquisition did not take the lock");
            using (var nested = ManagedLock.TryAcquireProfileWriter(homeA))
            {
                if (!nested.Owner) failures.Add("a nested acquisition deadlocked");
            }
        }

        // Released: a later acquisition must succeed straight away.
        using (var again = ManagedLock.TryAcquireProfileWriter(homeA, TimeSpan.FromMilliseconds(200)))
        {
            if (!again.Owner) failures.Add("the lock stayed held after disposal");
        }

        return failures.Count == 0 ? "ok" : "FAILED (" + string.Join("; ", failures) + ")";
    }

    /**
     * The child's view of the code-resolution variables. They are stripped so an
     * inherited one cannot change which plugin a name resolves to or what runs
     * before the harness does; this asks a real child rather than trusting the
     * dictionary we built.
     */
    internal static string EnvironmentGuardCheck(Tools t)
    {
        if (string.IsNullOrEmpty(t.Node)) return "skipped (no node)";
        try
        {
            // Under the app's own data root rather than the log directory: this
            // is a transient probe, not a diagnostic worth keeping.
            AppPaths.Ensure();
            var probe = Path.Combine(AppPaths.Root, "env-guard.json");
            if (File.Exists(probe)) File.Delete(probe);
            // Quoted so the arguments survive being joined into one node
            // command line; the script reports what the child can actually see.
            var script =
                "require('fs').writeFileSync(process.argv[1], JSON.stringify({" +
                "o: process.env.NODE_OPTIONS ?? null," +
                "p: process.env.NODE_PATH ?? null," +
                "h: process.env.DSH_HOME ?? null," +
                "b: process.env.DSH_KEEP_ME ?? null }))";
            var child = HarnessEnvironment.Hardened("guard-home",
                new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DSH_KEEP_ME"] = "sentinel",
                });
            var code = Proc.Run(t.Node!, new[] { "-e", script, probe }, null, null, 30_000,
                default, child, workingDirectory: Path.GetDirectoryName(probe));
            if (code != 0)
            {
                return $"FAILED (the child exited {code}; inherited NODE_OPTIONS="
                       + (Environment.GetEnvironmentVariable("NODE_OPTIONS") ?? "unset") + ")";
            }
            if (!File.Exists(probe)) return "FAILED (the child wrote no report)";

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(probe));
            var root = document.RootElement;
            var problems = new System.Collections.Generic.List<string>();

            // "Not inherited" means the child sees no usable value. It reads as
            // an empty string or as absent depending on the platform, and an
            // empty NODE_OPTIONS is harmless; a VALUE is what must never survive,
            // because that is what can inject --require or add a resolve path.
            void CheckStripped(string property, string name)
            {
                var element = root.GetProperty(property);
                var value = element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : null;
                if (!string.IsNullOrEmpty(value)) problems.Add($"{name} leaked ({value})");
            }

            CheckStripped("o", "NODE_OPTIONS");
            CheckStripped("p", "NODE_PATH");
            if (root.GetProperty("h").GetString() != "guard-home") problems.Add("DSH_HOME missing");
            if (root.GetProperty("b").GetString() != "sentinel") problems.Add("unrelated variable dropped");
            return problems.Count == 0 ? "ok" : "FAILED (" + string.Join("; ", problems) + ")";
        }
        catch (Exception ex)
        {
            return "FAILED (" + ex.Message + ")";
        }
    }

    /**
     * The relative-spec rule. The CLI anchors a relative plugin path on the
     * directory it was invoked from, so the app has to hand it an absolute one -
     * otherwise `.` would mean the profile directory inside pnpm.
     */
    internal static string SpecAnchoringChecks()
    {
        const string baseDir = @"C:\work\checkout";
        var failures = new System.Collections.Generic.List<string>();

        void Check(string name, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failures.Add($"{name}: expected '{expected}', got '{actual}'");
            }
        }

        Check("bare dot", PluginManager.AnchorSpec(".", baseDir), @"C:\work\checkout");
        Check("parent", PluginManager.AnchorSpec("../sibling", baseDir), @"C:\work\sibling");
        Check("bare path stays bare", PluginManager.AnchorSpec("./plugin", baseDir),
            @"C:\work\checkout\plugin");
        Check("file: keeps its prefix", PluginManager.AnchorSpec("file:./plugin", baseDir),
            @"file:C:\work\checkout\plugin");
        Check("link: keeps its prefix", PluginManager.AnchorSpec("link:../plugin", baseDir),
            @"link:C:\work\plugin");
        // Everything that is not a filesystem path must pass through untouched.
        Check("registry name", PluginManager.AnchorSpec("dsh-plugin-x", baseDir), "dsh-plugin-x");
        Check("scoped name", PluginManager.AnchorSpec("@scope/plugin", baseDir), "@scope/plugin");
        Check("git spec", PluginManager.AnchorSpec("github:user/repo", baseDir), "github:user/repo");
        Check("absolute path", PluginManager.AnchorSpec(@"C:\elsewhere\plugin", baseDir),
            @"C:\elsewhere\plugin");
        Check("versioned name", PluginManager.AnchorSpec("dsh-plugin-x@1.2.3", baseDir), "dsh-plugin-x@1.2.3");

        return failures.Count == 0 ? "ok" : "FAILED (" + string.Join("; ", failures) + ")";
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
