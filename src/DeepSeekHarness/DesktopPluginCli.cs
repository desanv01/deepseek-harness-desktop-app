using System;
using System.IO;

namespace DShNative;

/**
 * --install-plugin: install the bundled harness plugin into a home and report
 * what happened, without booting anything. Useful for provisioning, for support
 * ("is the plugin actually there?"), and for the smoke suite.
 *
 * Exit codes: 0 installed or already current, 1 the install failed,
 * 10 node or the harness CLI is missing.
 */
public static class DesktopPluginCli
{
    public static int Run(Options o)
    {
        var home = o.ResolveHome();
        Console.WriteLine("== DeepSeek Harness desktop plugin ==");
        Console.WriteLine($"plugin : {DesktopPlugin.PackageName} {DesktopPlugin.BundledVersion()}");
        Console.WriteLine($"home   : {home}");

        var tools = Tools.Discover();
        if (tools.Node == null || tools.DshCli == null)
        {
            Console.Error.WriteLine($"node or the harness CLI is missing (state: {tools.DshState}); "
                                    + "the profile cannot be initialized");
            return 10;
        }

        var error = DesktopPlugin.Ensure(home, tools, out var result, force: true);
        Console.WriteLine($"result : {result.Describe()}");
        Console.WriteLine($"profile: {HarnessProfile.ManifestPath(home)}");
        Console.WriteLine($"bundles: {string.Join(", ", HarnessProfile.ReadBundles(home))}");

        if (error != null)
        {
            Console.Error.WriteLine("failed : " + error);
            return 1;
        }

        var plugins = HarnessProfile.ReadPlugins(home);
        foreach (var plugin in plugins)
        {
            Console.WriteLine($"  {(plugin.Enabled ? "on " : "off")}  {plugin.Name}"
                              + (plugin.Version == null ? "" : " " + plugin.Version)
                              + (plugin.BuiltIn ? "  (base)" : ""));
        }
        return 0;
    }
}
