using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DShNative;

/** Where the harness CLI stands. */
public enum DshState
{
    /** Nothing that looks like @deepseek-ai/dsh was found. */
    Missing,

    /** The package is installed but cannot run - an interrupted npm install leaves this behind. */
    Broken,

    /** An entry point was found; VerifyDsh decides whether it actually works. */
    Found,
}

/**
 * Finds node, npm and the global @deepseek-ai/dsh entry.
 *
 * The CLI is located the way a shell would locate it, not by guessing one
 * hard-coded path: the dsh shim on PATH is read for the JavaScript file it
 * runs, and the package directory is inspected through its own package.json
 * "bin" field. A custom npm prefix, a pnpm-style global store, or a package
 * layout change therefore still resolves, and "installed but incomplete" is
 * reported as such instead of as "not installed".
 */
public sealed class Tools
{
    public string? Node { get; init; }
    public string? NpmCli { get; init; }

    /** The JavaScript entry point dsh would run, when one was found. */
    public string? DshCli { get; init; }
    public string? DshVersion { get; init; }

    public DshState DshState { get; init; } = DshState.Missing;

    /** The installed package directory, when one was found, usable or not. */
    public string? DshPackageDir { get; init; }

    /** Why the installed package cannot run, when it cannot. */
    public string? DshProblem { get; init; }

    /** What the search looked at, for --self-test and for error dialogs. */
    public IReadOnlyList<string> SearchLog { get; init; } = Array.Empty<string>();

    /** Nothing usable: either absent, or present and incomplete. */
    public bool DshMissing => DshState != DshState.Found;

    public bool DshBroken => DshState == DshState.Broken;

    /** Entry points tried inside a package whose package.json does not say. */
    private static readonly string[] EntryCandidates =
    {
        @"lib\bin.js", @"lib\bin.mjs", @"lib\index.js", @"dist\bin.js", @"bin\dsh.js", @"bin.js", "index.js",
    };

    private static readonly string[] ShimNames = { "dsh.cmd", "dsh.ps1", "dsh" };

    /**
     * Finds everything the app needs. --dsh-cli wins over the search, and the
     * search itself follows the shim npm wrote, so a custom prefix or a
     * different package layout still resolves.
     */
    public static Tools Discover(string? explicitCli = null)
    {
        explicitCli ??= Options.Current?.DshCli;
        var log = new List<string>();
        string? node = null, npmCli = null;
        var dirs = SearchDirs();

        foreach (var dir in dirs)
        {
            if (node == null && File.Exists(Path.Combine(dir, "node.exe")))
                node = Path.Combine(dir, "node.exe");

            if (npmCli == null && ShimsExist(dir, "npm"))
            {
                var candidate = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(candidate)) npmCli = candidate;
            }
        }

        Note(log, $"{dirs.Count} directories searched (PATH plus the npm global folder)");

        var entry = "";
        string? packageDir = null;
        string? problem = null;
        var state = DshState.Missing;

        if (!string.IsNullOrWhiteSpace(explicitCli))
        {
            var given = explicitCli.Trim().Trim('"');
            if (File.Exists(given))
            {
                entry = Path.GetFullPath(given);
                packageDir = PackageDirOf(entry);
                state = DshState.Found;
                Note(log, $"--dsh-cli: using {entry}");
            }
            else
            {
                problem = $"--dsh-cli named {given}, which does not exist";
                Note(log, problem);
                state = DshState.Broken;
            }
        }
        else
        {
            var hit = FindDsh(dirs, log);
            if (hit.Entry != null)
            {
                entry = hit.Entry;
                packageDir = hit.PackageDir;
                state = DshState.Found;
            }
            else
            {
                // The global prefix can be somewhere the PATH never mentions, so
                // ask npm for it before giving up.
                var prefix = AskNpmForGlobalPrefix(node, npmCli, log);
                if (prefix != null
                    && Directory.Exists(prefix)
                    && !dirs.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    var retry = FindDsh(new[] { prefix }, log);
                    if (retry.Entry != null) hit = retry;
                }

                if (hit.Entry != null)
                {
                    entry = hit.Entry;
                    packageDir = hit.PackageDir;
                    state = DshState.Found;
                }
                else if (hit.PackageDir != null)
                {
                    packageDir = hit.PackageDir;
                    problem = hit.Problem;
                    state = DshState.Broken;
                }
            }
        }

        var version = state == DshState.Found ? ReadVersion(packageDir ?? PackageDirOf(entry)) : null;
        if (state == DshState.Found) Note(log, $"dsh entry: {entry}");

        return new Tools
        {
            Node = node,
            NpmCli = npmCli,
            DshCli = state == DshState.Found ? entry : null,
            DshVersion = version,
            DshState = state,
            DshPackageDir = packageDir,
            DshProblem = problem,
            SearchLog = log,
        };
    }

    /** Adds one line to the search log, once, and keeps the log readable. */
    private static void Note(List<string> log, string message)
    {
        if (log.Contains(message) || log.Count >= 12) return;
        log.Add(message);
    }

    /** Everything a shell would look at, plus where npm usually puts globals. */
    private static List<string> SearchDirs()
    {
        var dirs = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            dirs.Add(dir);
        }

        // npm's global bin normally sits outside PATH for a service-launched app
        var appDataNpm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        if (!dirs.Contains(appDataNpm, StringComparer.OrdinalIgnoreCase)) dirs.Add(appDataNpm);
        return dirs;
    }

    private sealed record DshHit(string? Entry, string? PackageDir, string? Problem);

    /**
     * Looks for dsh in the given directories: first the shim (which names the
     * real entry point), then the package itself.
     */
    private static DshHit FindDsh(IEnumerable<string> dirs, List<string> log)
    {
        string? packageDir = null;
        string? problem = null;

        foreach (var dir in dirs)
        {
            foreach (var shim in ShimNames)
            {
                var shimPath = Path.Combine(dir, shim);
                if (!File.Exists(shimPath)) continue;

                var target = ReadShimTarget(shimPath, dir);
                if (target == null)
                {
                    Note(log, $"{shimPath}: no JavaScript entry point could be read from the shim");
                    continue;
                }
                if (File.Exists(target))
                {
                    Note(log, $"{shimPath} runs {target}");
                    return new DshHit(target, PackageDirOf(target), null);
                }

                packageDir ??= PackageDirOf(target);
                problem ??= $"{shimPath} runs {target}, which does not exist";
                Note(log, problem);
            }

            var candidateDir = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh");
            if (!Directory.Exists(candidateDir)) continue;

            packageDir ??= candidateDir;
            var entry = ResolvePackageEntry(candidateDir, log);
            if (entry != null) return new DshHit(entry, candidateDir, null);
            problem ??= $"{candidateDir} has no entry point "
                        + "(no \"bin\" in package.json and none of the usual files)";
        }

        return new DshHit(null, packageDir, problem);
    }

    /**
     * Reads the JavaScript file a shim runs. npm writes the same path into every
     * shim flavour, behind a placeholder for the shim's own directory:
     *   "%dp0%\node_modules\@deepseek-ai\dsh\lib\bin.js"   (cmd)
     *   "$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" (sh)
     */
    private static string? ReadShimTarget(string shimPath, string dir)
    {
        try
        {
            var text = File.ReadAllText(shimPath);
            foreach (Match match in Regex.Matches(text, @"[^""'\s;)]*@deepseek-ai[\\/]dsh[\\/][^""'\s;)]*\.js"))
            {
                var candidate = match.Value
                    .Replace("%~dp0%", dir + "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("%dp0%", dir + "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("$basedir/", dir + "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("$basedir\\", dir + "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("${basedir}/", dir + "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace('/', '\\');

                if (candidate.StartsWith(".\\", StringComparison.Ordinal)) candidate = candidate[2..];
                if (candidate.StartsWith("\\", StringComparison.Ordinal)) candidate = dir + candidate;

                try
                {
                    return Path.GetFullPath(candidate, dir);
                }
                catch
                {
                    // an unparsable shim line is not fatal; the next one may work
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {shimPath}: {ex.Message}");
        }
        return null;
    }

    /** The entry point a package declares, or one of the conventional ones. */
    private static string? ResolvePackageEntry(string packageDir, List<string> log)
    {
        var manifest = Path.Combine(packageDir, "package.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("bin", out var bin))
                {
                    var relative = bin.ValueKind switch
                    {
                        JsonValueKind.String => bin.GetString(),
                        JsonValueKind.Object when bin.TryGetProperty("dsh", out var named) => named.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(relative))
                    {
                        var declared = Path.GetFullPath(Path.Combine(packageDir, relative));
                        if (File.Exists(declared)) return declared;
                        Note(log, $"{manifest} declares bin {relative}, which does not exist");
                    }
                }
            }
            catch (Exception ex)
            {
                Note(log, $"{manifest} could not be read: {ex.Message}");
            }
        }

        foreach (var relative in EntryCandidates)
        {
            var candidate = Path.Combine(packageDir, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /** Walks up from an entry point to the package directory that owns it. */
    public static string? PackageDirOf(string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(entryPath));
            for (var depth = 0; depth < 5 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (File.Exists(Path.Combine(dir, "package.json"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // a path this broken is not a package directory
        }
        return null;
    }

    /** The version out of the installed package's own manifest. */
    private static string? ReadVersion(string? packageDir)
    {
        if (string.IsNullOrWhiteSpace(packageDir)) return null;
        try
        {
            var manifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(manifest)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /** The npm global prefix, asked of npm itself. Slow, so only as a fallback. */
    private static string? AskNpmForGlobalPrefix(string? node, string? npmCli, List<string> log)
    {
        if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(npmCli)) return null;

        var outFile = AppPaths.NewLogPath("npm-prefix", ".out.log");
        var errFile = AppPaths.NewLogPath("npm-prefix", ".err.log");
        var code = Proc.Run(node, new[] { npmCli, "prefix", "-g" }, outFile, errFile, 30_000);
        if (code != 0)
        {
            Note(log, $"npm prefix -g failed (exit {code})");
            return null;
        }

        try
        {
            var prefix = File.ReadAllText(outFile).Trim().Trim('"');
            if (prefix.Length == 0) return null;
            Note(log, $"npm prefix -g reports {prefix}");
            return prefix;
        }
        catch
        {
            return null;
        }
    }

    /**
     * Fast liveness probe: runs the entry point with `--version`, which loads
     * the CLI's own modules and nothing else. Measured at 0.2s against 7-8s for
     * the `web --help` verification below, so the launch path uses this and
     * keeps the deep check for diagnosis.
     *
     * It still catches the common breakage - a half-installed package fails to
     * resolve lib/bin.js and exits non-zero immediately.
     */
    public static bool ProbeCli(Tools t, out string? error)
    {
        error = null;
        var node = t.Node;
        var dsh = t.DshCli;
        if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(dsh))
        {
            error = "no usable CLI entry point was found";
            return false;
        }

        var outFile = AppPaths.NewLogPath("dsh-probe", ".out.log");
        var errFile = AppPaths.NewLogPath("dsh-probe", ".err.log");
        var code = Proc.Run(node, new[] { dsh, "--version" }, outFile, errFile, 20_000);
        if (code == 0) return true;

        error = $"the CLI exited {code} when asked for its version";
        Log.Warn($"{error}; probe logs: {errFile}");
        return false;
    }

    /**
     * Exercises the CLI's web help path without binding a fixed port. This
     * catches a partially replaced global package that still has a dsh shim and
     * package.json but cannot load its runtime closure.
     *
     * It costs 7-8 seconds, so it is diagnosis (and --self-test), not the gate
     * the launch path waits on: the server's own ready line is the real proof
     * that the CLI works.
     */
    public static bool VerifyDsh(Tools t)
    {
        var node = t.Node;
        var dsh = t.DshCli;
        if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(dsh)) return false;

        var outFile = AppPaths.NewLogPath("dsh-verify", ".out.log");
        var errFile = AppPaths.NewLogPath("dsh-verify", ".err.log");
        var code = Proc.Run(
            node,
            new[] { dsh, "web", "--help", "--no-open", "--host", "127.0.0.1", "--port", "0" },
            outFile,
            errFile,
            45_000);
        if (code == 0) return true;

        Log.Warn($"dsh CLI validation failed (exit {code}); verification logs: {errFile}");
        return false;
    }

    private static bool ShimsExist(string dir, string name)
    {
        foreach (var ext in new[] { ".cmd", ".ps1", ".exe", "" })
        {
            if (File.Exists(Path.Combine(dir, name + ext))) return true;
        }
        return false;
    }
}
