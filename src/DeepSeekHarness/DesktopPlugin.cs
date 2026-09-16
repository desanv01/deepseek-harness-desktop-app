using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DShNative;

/**
 * The updates plugin the app ships: it is what puts the Updates section inside
 * the harness UI and the entry beside Settings, instead of the app injecting DOM
 * of its own.
 *
 * The plugin's files are embedded in the executable and extracted into the app's
 * data directory, then copied into the harness home's profile - no package
 * manager, no network, no symlink back into the app. A home that already runs it
 * is left alone unless the bundled version changed.
 */
public static class DesktopPlugin
{
    public const string PackageName = "dsh-plugin-desktop-updates";

    /** Files the plugin package consists of; each is an embedded resource. */
    private static readonly string[] Files = { "package.json", "cordis.patch.yml", "index.js", "client.js" };

    /** What happened the last time Ensure ran, for logs and for --install-plugin. */
    public sealed record Result(
        bool Installed, bool Updated, bool AlreadyCurrent, string? Version, string? Error, string? KeptNewer = null)
    {
        public string Describe() => Error != null
            ? "failed: " + Error
            : KeptNewer != null
                ? $"kept {KeptNewer} (newer than the bundled {Version})"
                : AlreadyCurrent
                    ? $"already current ({Version})"
                    : Updated ? $"updated to {Version}" : $"installed {Version}";
    }

    /** The version of the plugin embedded in this build. */
    public static string BundledVersion()
    {
        try
        {
            var text = ReadResource("package.json");
            if (text != null)
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("version", out var version))
                {
                    return version.GetString() ?? "0.0.0";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the bundled plugin version: " + ex.Message);
        }
        return "0.0.0";
    }

    /**
     * Makes sure the given harness home runs the bundled plugin.
     *
     * A newer plugin than the bundled one is left alone unless `force` is set:
     * the plugin is also installable from npm, and a boot that silently replaced
     * a newer install with the copy in the executable would undo that on every
     * launch. The explicit --install-plugin command forces it, because restoring
     * the bundled copy is what someone running it is asking for.
     *
     * Returns null on success, or a message describing what went wrong.
     */
    public static string? Ensure(string home, Tools tools, out Result result, bool force = false)
    {
        result = new Result(false, false, false, null, null);
        try
        {
            var initError = HarnessProfile.Ensure(home, tools);
            if (initError != null)
            {
                result = result with { Error = initError };
                return initError;
            }

            var version = BundledVersion();
            var stage = Path.Combine(AppPaths.PluginsDir, PackageName + "-" + version);
            var extractError = Extract(stage);
            if (extractError != null)
            {
                result = result with { Version = version, Error = extractError };
                return extractError;
            }

            var modules = HarnessProfile.ModulesDir(home);
            var installedVersion = HarnessProfile.ReadPackageVersion(Path.Combine(modules, PackageName));
            // `force` bypasses the newer-installed rule, not the version check:
            // a forced run over the same version is still a no-op.
            if (IsNewer(installedVersion, version) && !force)
            {
                Log.Info($"the home runs {PackageName} {installedVersion}, newer than the bundled {version}; leaving it");
                result = new Result(false, false, false, version, null, installedVersion);
                return null;
            }

            if (!string.Equals(installedVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                var error = HarnessProfile.InstallPlugin(home, stage, PackageName, version);
                if (error != null)
                {
                    result = result with { Version = version, Error = error };
                    return error;
                }
                result = new Result(true, installedVersion != null, false, version, null);
                return null;
            }

            // Same version: only the bundle row can still be missing, which is
            // what a hand-edited profile looks like.
            var bundleError = HarnessProfile.AddBundle(home, PackageName);
            if (bundleError != null)
            {
                result = result with { Version = version, Error = bundleError };
                return bundleError;
            }

            result = new Result(false, false, true, version, null);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("could not install the desktop plugin: " + ex);
            result = result with { Error = ex.Message };
            return ex.Message;
        }
    }

    /**
     * Whether an installed version is ahead of the bundled one.
     *
     * Versions here are `major.minor.patch`, optionally with a prerelease tail.
     * Anything that does not parse is not treated as newer: a version this does
     * not understand is not a reason to keep a copy the app cannot vouch for.
     */
    private static bool IsNewer(string? installed, string bundled)
    {
        var left = ParseVersion(installed);
        var right = ParseVersion(bundled);
        return left != null && right != null && left > right;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    /** Writes the embedded plugin files into the app's data directory. */
    private static string? Extract(string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            foreach (var file in Files)
            {
                var text = ReadResource(file);
                if (text == null) return $"the bundled plugin is missing {file}";
                File.WriteAllText(Path.Combine(target, file), text);
            }
            return null;
        }
        catch (Exception ex)
        {
            return "the bundled plugin could not be written: " + ex.Message;
        }
    }

    private static string? ReadResource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = "DShNative.plugin." + fileName;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            // A missing resource is a build mistake, not a runtime condition.
            Log.Warn($"embedded resource {name} is missing; available: "
                     + string.Join(", ", assembly.GetManifestResourceNames().Where(n => n.Contains("plugin"))));
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /** The plugin files as they are embedded, for diagnostics. */
    public static IReadOnlyList<string> BundledFiles => Files;
}
