using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DShNative;

/**
 * Reports which installed plugins have a newer version published.
 *
 * The app installs, removes, enables and disables plugins, but never told anyone
 * that one had moved on - so a plugin installed once stayed at that version
 * indefinitely unless the user thought to look it up.
 *
 * Only plugins that came from a registry are checked. A plugin installed from a
 * local folder or a git spec has no published version to compare against, and
 * asking the registry about it would either fail or, worse, match an unrelated
 * package that happens to share the name. Deciding from the recorded spec is what
 * keeps this honest: "cannot tell" is reported as such rather than guessed.
 *
 * Nothing here installs anything. A newer version is a fact to show, and the
 * existing `--add-plugin` path is what acts on it.
 */
public static class PluginUpdateCheck
{
    /** One plugin's standing. */
    public sealed record PluginStanding(
        string Name,
        string? Installed,
        string? Latest,
        bool UpdateAvailable,
        string? Reason)
    {
        public string Describe()
        {
            if (Reason != null) return $"{Name} {Installed ?? "?"} - {Reason}";
            if (UpdateAvailable) return $"{Name} {Installed} -> {Latest}";
            return $"{Name} {Installed} (current)";
        }
    }

    /** The packages that are not community plugins, so never looked up. */
    private static readonly HashSet<string> NotCommunity = new(StringComparer.OrdinalIgnoreCase)
    {
        "dshmarket", "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
    };

    /**
     * Checks every installed plugin. Returns one standing per plugin, in profile
     * order. Never throws: a plugin that cannot be checked is reported as
     * unchecked, which is different from up to date.
     */
    public static async Task<List<PluginStanding>> CheckAsync(string home, IEnumerable<string>? only = null)
    {
        var standings = new List<PluginStanding>();
        try
        {
            var modules = HarnessProfile.ModulesDir(home);
            var wanted = only?.ToList();
            foreach (var bundle in HarnessProfile.ReadBundles(home))
            {
                if (NotCommunity.Contains(bundle)) continue;
                if (wanted is { Count: > 0 } && !wanted.Contains(bundle, StringComparer.OrdinalIgnoreCase)) continue;

                var installed = HarnessProfile.ReadPackageVersion(System.IO.Path.Combine(modules, bundle));
                if (installed == null)
                {
                    standings.Add(new PluginStanding(bundle, null, null, false, "not installed"));
                    continue;
                }

                var local = IsLocalSpec(home, bundle);
                if (local != null)
                {
                    standings.Add(new PluginStanding(bundle, installed, null, false, local));
                    continue;
                }

                var latest = await LatestVersionAsync(bundle).ConfigureAwait(false);
                if (latest == null)
                {
                    standings.Add(new PluginStanding(bundle, installed, null, false, "no published version was found"));
                    continue;
                }

                var newer = Compare(latest, installed) > 0;
                standings.Add(new PluginStanding(bundle, installed, latest, newer, null));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("the plugin update check failed: " + ex.Message);
        }
        return standings;
    }

    /**
     * Why a plugin has no published version to compare against, or null when it
     * came from a registry.
     */
    private static string? IsLocalSpec(string home, string bundle)
    {
        try
        {
            var manifest = System.IO.Path.Combine(HarnessProfile.Dir(home), "package.json");
            if (!System.IO.File.Exists(manifest)) return null;
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object
                || !deps.TryGetProperty(bundle, out var spec))
            {
                return null;
            }

            var value = spec.GetString() ?? "";
            // A path or a git spec has no registry version: the recorded spec is
            // the only honest answer to "what should this be?".
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("git", StringComparison.OrdinalIgnoreCase))
            {
                return "installed from a local path or git, so there is nothing to compare";
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the recorded spec for {bundle}: {ex.Message}");
            return null;
        }
    }

    /** The registry's `latest` tag for one package, or null. */
    private static async Task<string?> LatestVersionAsync(string package)
    {
        try
        {
            // The scoped name has to be escaped: a bare `/` in the path makes the
            // registry read the scope as a route segment.
            var url = "https://registry.npmjs.org/" + Uri.EscapeDataString(package);
            var payload = await UpdateHttp.GetStringAsync(url).ConfigureAwait(false);
            if (payload == null) return null;

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("dist-tags", out var tags)
                || !tags.TryGetProperty("latest", out var latest))
            {
                return null;
            }
            return latest.GetString();
        }
        catch (Exception ex)
        {
            Log.Info($"no published version for {package}: {ex.Message}");
            return null;
        }
    }

    /**
     * Compares two versions, ignoring a prerelease tail.
     *
     * A prerelease tail is dropped rather than ordered: whether `1.2.0-rc.1`
     * counts as newer than `1.2.0` depends on a reader's intent, and reporting a
     * downgrade as an update would be worse than not reporting it.
     */
    internal static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a == null || b == null) return 0;
        return a.CompareTo(b);
    }

    private static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    /**
     * --check-plugins: report each installed plugin's standing and exit.
     * 0 everything current, 10 an update is available, 1 nothing could be checked.
     * The same convention as --check-harness and --check-updates.
     */
    public static int RunCli(Options o)
    {
        var home = o.ResolveHome();
        Console.WriteLine("== DeepSeek Harness plugins ==");
        Console.WriteLine($"home  : {home}");
        Console.WriteLine($"profile: {HarnessProfile.ManifestPath(home)}");

        if (!HarnessProfile.Exists(home))
        {
            Console.WriteLine("state : there is no profile for this home");
            return 1;
        }

        var standings = CheckAsync(home).GetAwaiter().GetResult();
        if (standings.Count == 0)
        {
            Console.WriteLine("state : this profile has no plugins beyond the base bundles");
            return 0;
        }

        foreach (var standing in standings) Console.WriteLine("  " + standing.Describe());

        var updatable = standings.Where(s => s.UpdateAvailable).ToList();
        if (updatable.Count > 0)
        {
            Console.WriteLine($"update available: {string.Join(", ", updatable.Select(s => s.Name))}");
            Console.WriteLine("install one with: --add-plugin <name>@<version>");
            return 10;
        }

        var uncheckedCount = standings.Count(s => s.Reason != null);
        if (uncheckedCount == standings.Count)
        {
            Console.Error.WriteLine("nothing could be checked (no registry access, or no registry plugins)");
            return 1;
        }
        Console.WriteLine("state : every checked plugin is current");
        return 0;
    }
}
