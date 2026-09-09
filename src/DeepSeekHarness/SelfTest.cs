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
        Line("dsh cli     : " + (t.DshCli ?? "NOT FOUND"));
        Line("dsh version : " + (t.DshVersion ?? "n/a"));
        Line("dsh health  : " + (Tools.VerifyDsh(t) ? "CLI VALID" : "CLI UNUSABLE"));
        Line("project     : " + o.ProjectDir);
        Line("DSH_HOME    : " + o.DshHome);
        Line("endpoint    : " + o.TargetLabel + (o.Port == 0 ? " (OS picks a free port)" : ""));
        Line("webview2    : " + webView2);

        var lease = ServerManager.TryAdoptHome(o);
        Line("live server : " + (lease == null
            ? "none owned by this app"
            : $"pid {lease.Pid} on {lease.Address}:{lease.Port} (project {lease.ProjectDir})"));

        Line("logs dir    : " + AppPaths.LogsDir);
        Line("== done ==");
        return 0;
    }
}
