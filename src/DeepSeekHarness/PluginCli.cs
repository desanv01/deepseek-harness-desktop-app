using System;

namespace DShNative;

/**
 * --add-plugin, --remove-plugin and --plugin-list: the plugin manager from a
 * script, without a window. Exit codes: 0 success, 1 the operation failed,
 * 10 node, the CLI or the pnpm runtime is missing.
 */
public static class PluginCli
{
    public static int RunList(Options o)
    {
        var home = o.ResolveHome();
        Console.WriteLine("== DeepSeek Harness plugins ==");
        Console.WriteLine($"home    : {home}");
        Console.WriteLine($"profile : {HarnessProfile.ManifestPath(home)}");

        if (!HarnessProfile.Exists(home))
        {
            Console.WriteLine("state   : no profile yet (the first boot creates one)");
            return 0;
        }

        var bundles = HarnessProfile.ReadBundles(home);
        Console.WriteLine($"bundles : {string.Join(", ", bundles)}");
        foreach (var plugin in HarnessProfile.ReadPlugins(home))
        {
            Console.WriteLine($"  {(plugin.Enabled ? "on " : "off")}  {plugin.Name}"
                              + (plugin.Version == null ? "" : " " + plugin.Version)
                              + (plugin.BuiltIn ? "  (base)" : ""));
        }

        var disabled = HarnessProfile.ReadDisabledRows(home);
        if (disabled.Count > 0)
        {
            Console.WriteLine($"disabled rows: {string.Join(", ", disabled)}");
        }
        return 0;
    }

    public static int RunAdd(Options o, string spec)
    {
        var home = o.ResolveHome();
        var tools = Tools.Discover();
        if (tools.Node == null || tools.DshCli == null)
        {
            Console.Error.WriteLine($"node or the harness CLI is missing (state: {tools.DshState})");
            return 10;
        }

        Console.WriteLine($"== adding {spec} ==");
        Console.WriteLine($"home : {home}");
        var result = PluginManager.Add(home, tools, spec);
        if (result.Output.Length > 0) Console.WriteLine(result.Output);
        Console.WriteLine($"result: {result.Describe()}");
        if (!result.Ok) return 1;

        Console.WriteLine($"bundles: {string.Join(", ", HarnessProfile.ReadBundles(home))}");
        Console.WriteLine("Restart the app (or the harness) for the plugin to load.");
        return 0;
    }

    public static int RunSetEnabled(Options o, string name, bool enabled)
    {
        var home = o.ResolveHome();
        Console.WriteLine($"== {(enabled ? "enabling" : "disabling")} {name} ==");

        var result = PluginManager.SetEnabled(home, name, enabled);
        if (result.Output.Length > 0) Console.WriteLine(result.Output);
        Console.WriteLine($"result: {result.Describe()}");
        if (!result.Ok) return 1;

        var disabled = HarnessProfile.ReadDisabledRows(home);
        Console.WriteLine(disabled.Count == 0
            ? "disabled rows: none"
            : $"disabled rows: {string.Join(", ", disabled)}");
        Console.WriteLine("Restart the app (or the harness) for the change to take effect.");
        return 0;
    }

    public static int RunRemove(Options o, string name)
    {
        var home = o.ResolveHome();
        var tools = Tools.Discover();
        if (tools.Node == null || tools.DshCli == null)
        {
            Console.Error.WriteLine($"node or the harness CLI is missing (state: {tools.DshState})");
            return 10;
        }

        Console.WriteLine($"== removing {name} ==");
        var result = PluginManager.Remove(home, tools, name);
        if (result.Output.Length > 0) Console.WriteLine(result.Output);
        Console.WriteLine($"result: {result.Describe()}");
        if (!result.Ok) return 1;

        Console.WriteLine($"bundles: {string.Join(", ", HarnessProfile.ReadBundles(home))}");
        return 0;
    }
}
