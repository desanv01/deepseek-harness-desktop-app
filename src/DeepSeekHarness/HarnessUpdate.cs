using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DShNative;

/** Installed versus published DeepSeek Harness versions. */
public sealed record HarnessVersions(string? Installed, string? Latest, string? Alpha, string? Available)
{
    /** False when no usable CLI was found, so "available" means "can be installed". */
    public bool InstalledKnown => !string.IsNullOrWhiteSpace(Installed);

    /** One line for the tray and the CLI, honest about a missing install. */
    public string StatusLine(string channel)
    {
        if (!InstalledKnown)
        {
            return Available == null
                ? "Harness: not installed"
                : $"Harness: not installed ({Available} can be installed)";
        }
        return Available == null
            ? $"Harness v{Installed} (up to date)"
            : $"Harness v{Installed} -> {Available} available";
    }
}

/**
 * Reads the npm dist-tags for @deepseek-ai/dsh and reports whether a newer
 * release exists on the requested channel. Read-only: it never installs.
 *
 * The harness publishes prereleases every day or two, and `latest` can trail
 * `alpha` by several releases, so both are reported.
 */
public static class HarnessUpdate
{
    private const string RegistryUrl = "https://registry.npmjs.org/@deepseek-ai%2Fdsh";

    /** Channel the app follows. The plugin home needs the conservative one. */
    public const string DefaultChannel = "latest";

    public static async Task<HarnessVersions?> QueryAsync(string? installed, string channel = DefaultChannel)
    {
        try
        {
            var payload = await UpdateHttp.GetStringAsync(RegistryUrl).ConfigureAwait(false);
            if (payload == null)
            {
                Log.Warn("harness update check failed: the npm registry could not be read");
                return null;
            }

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("dist-tags", out var tags))
            {
                Log.Warn("harness update check failed: the registry response had no dist-tags");
                return null;
            }

            string? Tag(string name) => tags.TryGetProperty(name, out var value) ? value.GetString() : null;
            var latest = Tag("latest");
            var alpha = Tag("alpha");
            var candidate = string.Equals(channel, "alpha", StringComparison.OrdinalIgnoreCase) ? alpha : latest;
            var available = IsNewer(candidate, installed) ? candidate : null;
            return new HarnessVersions(installed, latest, alpha, available);
        }
        catch (Exception ex)
        {
            Log.Warn("harness update check failed: " + ex.Message);
            return null;
        }
    }

    /**
     * --check-harness: report the installed and published versions and exit.
     * Returns 0 when current, 10 when an update is available, 1 on failure -
     * the same convention as the workspace's dsh-update-check.ps1.
     */
    public static int RunCheck()
    {
        var installed = Tools.Discover().DshVersion;
        var info = QueryAsync(installed).GetAwaiter().GetResult();
        if (info == null)
        {
            Log.Error("harness update check failed: the npm registry could not be read");
            Console.WriteLine("harness update check failed (no registry access)");
            return 1;
        }

        Console.WriteLine($"installed : {info.Installed ?? "not installed"}");
        Console.WriteLine($"latest    : {info.Latest ?? "n/a"}");
        Console.WriteLine($"alpha     : {info.Alpha ?? "n/a"}");
        Console.WriteLine($"available : {info.Available ?? "none"}");
        if (!info.InstalledKnown) Console.WriteLine("state     : no usable CLI was found; run --repair-harness");
        Log.Info($"harness check: installed {info.Installed}, latest {info.Latest}, "
                 + $"alpha {info.Alpha}, available {info.Available ?? "none"}");
        return info.Available == null ? 0 : 10;
    }

    /** True when candidate is strictly newer than current. */
    public static bool IsNewer(string? candidate, string? current)    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        var a = Parse(candidate);
        var b = Parse(current);
        if (a.Major != b.Major) return a.Major > b.Major;
        if (a.Minor != b.Minor) return a.Minor > b.Minor;
        if (a.Patch != b.Patch) return a.Patch > b.Patch;
        if (a.PreRank != b.PreRank) return a.PreRank > b.PreRank;
        return a.PreNum > b.PreNum;
    }

    /**
     * Pre-release labels rank below the release they lead to, and
     * alpha < beta < rc, so 0.1.5-rc.1 outranks 0.1.5-alpha.2.
     */
    private static (int Major, int Minor, int Patch, int PreRank, int PreNum) Parse(string version)
    {
        var core = version;
        var pre = string.Empty;
        var dash = version.IndexOf('-');
        if (dash >= 0)
        {
            core = version[..dash];
            pre = version[(dash + 1)..];
        }

        var parts = core.Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            var digits = new string(parts[i].Where(char.IsDigit).ToArray());
            numbers[i] = int.TryParse(digits, out var value) ? value : 0;
        }

        var preRank = 99;   // no pre-release suffix sorts highest
        var preNum = 0;
        if (pre.Length > 0)
        {
            var label = pre.Split('.')[0];
            var match = Regex.Match(pre, @"\.(\d+)$");
            if (match.Success) preNum = int.Parse(match.Groups[1].Value);
            preRank = label.StartsWith("alpha", StringComparison.OrdinalIgnoreCase) ? 0
                    : label.StartsWith("beta", StringComparison.OrdinalIgnoreCase) ? 1
                    : label.StartsWith("rc", StringComparison.OrdinalIgnoreCase) ? 2
                    : 1;
        }

        return (numbers[0], numbers[1], numbers[2], preRank, preNum);
    }
}
