using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace DShNative;

/**
 * Installing and removing harness plugins, including third-party ones.
 *
 * `dsh plugin --profile web add <spec>` forwards to pnpm inside the profile and
 * then reconciles `dsh.profile.bundles` - the same bookkeeping the CLI does for
 * a human, which is why the app drives the CLI instead of reimplementing it.
 *
 * The one thing a user's machine may not have is pnpm. This build carries it:
 * the runtime is embedded in the executable (assets\pnpm at build time) and
 * extracted into the app's data directory, or fetched with npm when the build
 * shipped without it. The CLI is then run with that runtime first on PATH.
 */
public static class PluginManager
{
    /** pnpm version the app carries. */
    public const string RuntimeVersion = "11.7.0";

    public sealed record Result(bool Ok, string? Error, string Output)
    {
        public string Describe() => Ok ? "ok" : "failed: " + (Error ?? "unknown error");
    }

    /** The directory that holds the pnpm shim, creating it when needed. */
    public static string? EnsureRuntime(out string? error)
    {
        error = null;
        var target = Path.Combine(AppPaths.Root, "tools", "pnpm-" + RuntimeVersion);
        var shim = Path.Combine(target, "pnpm.cmd");
        var entry = Path.Combine(target, "pnpm.mjs");

        try
        {
            if (File.Exists(shim) && File.Exists(entry)) return target;

            Directory.CreateDirectory(target);
            var embedded = ExtractEmbedded(target);
            if (!embedded)
            {
                var fetched = FetchWithNpm(target, out var fetchError);
                if (!fetched)
                {
                    error = fetchError ?? "the pnpm runtime could not be prepared";
                    return null;
                }
            }

            File.WriteAllText(shim,
                "@ECHO off" + Environment.NewLine
                + $"node \"{entry}\" %*" + Environment.NewLine);
            Log.Info($"pnpm runtime ready at {target}");
            return target;
        }
        catch (Exception ex)
        {
            error = "the pnpm runtime could not be prepared: " + ex.Message;
            Log.Warn(error);
            return null;
        }
    }

    /** Writes the runtime this build carries, if it carries one. */
    private static bool ExtractEmbedded(string target)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var wrote = false;
        foreach (var file in new[] { "pnpm.mjs", "worker.js" })
        {
            using var stream = assembly.GetManifestResourceStream("DShNative.pnpm." + file);
            if (stream == null) continue;
            using var output = File.Create(Path.Combine(target, file));
            stream.CopyTo(output);
            wrote = true;
        }
        if (wrote) Log.Info("extracted the pnpm runtime this build carries");
        return wrote;
    }

    /**
     * Builds that ship without the runtime (no network at build time) fetch it
     * with npm on first use. npm is already a requirement of this app.
     */
    private static bool FetchWithNpm(string target, out string? error)
    {
        error = null;
        var tools = Tools.Discover();
        if (string.IsNullOrEmpty(tools.Node) || string.IsNullOrEmpty(tools.NpmCli))
        {
            error = "npm was not found, so the pnpm runtime cannot be fetched";
            return false;
        }

        var stage = Path.Combine(target, "fetch");
        Directory.CreateDirectory(stage);
        var outFile = AppPaths.NewLogPath("pnpm-fetch", ".out.log");
        var errFile = AppPaths.NewLogPath("pnpm-fetch", ".err.log");

        Log.Info($"fetching pnpm {RuntimeVersion} with npm");
        var code = Proc.Run(tools.Node!, new[]
        {
            tools.NpmCli!, "install", "--prefix", stage, "pnpm@" + RuntimeVersion,
            "--no-save", "--no-audit", "--no-fund", "--loglevel=error",
            "--cache", Path.Combine(target, "npm-cache"),
        }, outFile, errFile, 600_000);

        var dist = Path.Combine(stage, "node_modules", "pnpm", "dist");
        if (code != 0 || !Directory.Exists(dist))
        {
            error = $"npm could not fetch pnpm {RuntimeVersion} (exit {code}); see {errFile}";
            Log.Warn(error);
            return false;
        }

        foreach (var file in new[] { "pnpm.mjs", "worker.js" })
        {
            File.Copy(Path.Combine(dist, file), Path.Combine(target, file), overwrite: true);
        }
        TryDelete(stage);
        return true;
    }

    /**
     * Runs one `dsh plugin --profile web ...` command with the bundled runtime on
     * PATH and DSH_HOME pointed at this app's home. Returns what the CLI printed.
     */
    public static Result RunCli(string home, Tools tools, string[] pluginArgs, int timeoutMs = 900_000, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tools.Node) || string.IsNullOrEmpty(tools.DshCli))
        {
            return new Result(false, "node or the harness CLI was not found", "");
        }

        var runtime = EnsureRuntime(out var runtimeError);
        if (runtime == null)
        {
            return new Result(false, runtimeError, "");
        }

        var outFile = AppPaths.NewLogPath("plugin", ".out.log");
        var errFile = AppPaths.NewLogPath("plugin", ".err.log");
        var args = new List<string> { tools.DshCli!, "plugin", "--profile", HarnessProfile.Name };
        args.AddRange(pluginArgs);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The CLI resolves the profile from DSH_HOME, so it must be told.
            ["DSH_HOME"] = home,
            // pnpm is resolved through PATH, through its .cmd shim on Windows.
            ["PATH"] = runtime + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
        };

        Log.Info($"running: dsh plugin --profile {HarnessProfile.Name} {string.Join(" ", pluginArgs)}");
        var code = Proc.Run(tools.Node!, args.ToArray(), outFile, errFile, timeoutMs, ct, environment);
        var output = ReadTail(outFile) + ReadTail(errFile);

        if (code != 0)
        {
            return new Result(false, $"pnpm exited {code}. {output}".Trim(), output);
        }
        return new Result(true, null, output);
    }

    /** Installs a package (registry name, git spec, or a local path) into the home's profile. */
    public static Result Add(string home, Tools tools, string spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec)) return new Result(false, "no package was named", "");

        var result = RunCli(home, tools, new[] { "add", spec.Trim() }, ct: ct);
        if (!result.Ok) return result;

        // pnpm can succeed while the CLI's reconciliation decides the package is
        // not a bundle layer; say so rather than letting the user wonder why
        // nothing appeared.
        var name = PackageNameOf(spec);
        if (name != null && !HarnessProfile.HasBundle(home, name))
        {
            var installed = Directory.Exists(Path.Combine(HarnessProfile.ModulesDir(home), name));
            return new Result(installed,
                installed ? null : $"{name} was not installed",
                result.Output + (installed
                    ? $"\n{name} is installed but declares no dsh.bundle, so it is not a profile layer."
                    : ""));
        }
        return result;
    }

    /** Removes a package from the profile. */
    public static Result Remove(string home, Tools tools, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return new Result(false, "no package was named", "");

        var pluginRows = HarnessProfile.ReadPatchRowIds(Path.Combine(HarnessProfile.ModulesDir(home), name));
        var result = RunCli(home, tools, new[] { "remove", name.Trim() }, ct: ct);
        if (result.Ok)
        {
            // Drop the disable markers too, or a later reinstall inherits them.
            foreach (var row in pluginRows) HarnessProfile.SetRowDisabled(home, row, disabled: false);
            return result;
        }

        // Without pnpm (offline, or a package with no dependencies) the file
        // level removal still leaves a usable profile.
        Log.Warn($"the CLI could not remove {name}; removing it from the profile directly");
        var fallback = HarnessProfile.RemovePlugin(home, name);
        return fallback == null
            ? new Result(true, null, result.Output + "\nremoved from the profile directly")
            : new Result(false, fallback, result.Output);
    }

    /**
     * Switches one installed plugin on or off through the profile's patch layer.
     * The package stays installed either way; a re-enable is instant and needs no
     * network.
     */
    public static Result SetEnabled(string home, string packageName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return new Result(false, "no plugin was named", "");
        }

        var name = packageName.Trim();
        if (!HarnessProfile.HasBundle(home, name))
        {
            return new Result(false, $"{name} is not one of this home's bundles", "");
        }
        if (HarnessProfile.BaseBundles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new Result(false, $"{name} is part of the base profile and cannot be switched off", "");
        }

        var packageDir = Path.Combine(HarnessProfile.ModulesDir(home), name);
        var rows = HarnessProfile.ReadPatchRowIds(packageDir);
        if (rows.Count == 0)
        {
            return new Result(false, $"{name} mounts no row of its own, so it cannot be switched here", "");
        }

        foreach (var row in rows)
        {
            var error = HarnessProfile.SetRowDisabled(home, row, disabled: !enabled);
            if (error != null) return new Result(false, error, "");
        }

        Log.Info($"{name} {(enabled ? "enabled" : "disabled")} in profile {HarnessProfile.Name}");
        return new Result(true, null, $"{name} {(enabled ? "enabled" : "disabled")} (rows: {string.Join(", ", rows)})");
    }

    /** The package name inside an install spec, when it is obvious. */
    public static string? PackageNameOf(string spec)
    {
        var value = (spec ?? "").Trim();
        if (value.Length == 0) return null;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(".", StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            try
            {
                var path = value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value;
                var manifest = Path.Combine(Path.GetFullPath(path), "package.json");
                if (File.Exists(manifest))
                {
                    return HarnessProfile.ReadPackageVersion(Path.GetDirectoryName(manifest)!) == null
                        ? null
                        : ReadName(manifest);
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        if (value.StartsWith("@", StringComparison.Ordinal))
        {
            var parts = value.Split('/');
            return parts.Length >= 2 ? parts[0] + "/" + parts[1].Split('@')[0] : null;
        }
        return value.Split('@')[0];
    }

    private static string? ReadName(string manifestPath)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path).ReplaceLineEndings("\n").Trim();
            return text.Length > 1200 ? text[^1200..] : text;
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
