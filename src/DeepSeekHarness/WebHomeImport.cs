using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DShNative;

/**
 * Brings an existing harness home created by the web version into this app's own
 * home, once.
 *
 * `dsh web` keeps everything in `~/.dsh`; this app keeps its own home under the
 * app data root. Somebody who has been using the web UI therefore starts with an
 * empty desktop app - no sessions, no settings, no credentials, no presets.
 *
 * Four rules, each of which the implementation below is shaped by:
 *
 *   - **Copy, never share.** The desktop home is never pointed at `~/.dsh`. Two
 *     writers over one session log is the failure this avoids.
 *   - **The web home is read-only.** Nothing here writes to it, and a partial
 *     failure leaves it untouched.
 *   - **All or nothing.** The copy lands in a sibling temporary directory and is
 *     promoted by a single rename, so an interrupted import cannot leave a
 *     half-populated home that the app would then treat as real.
 *   - **Ask once.** The answer is recorded, so a later launch does not ask again
 *     or silently re-import over work done since.
 *
 * The plugin tree is deliberately not copied wholesale: `node_modules` is a
 * package manager's output, tied to the exact tree that produced it. What is
 * copied is each community plugin's declaration, so the desktop home reinstalls
 * them its own way rather than inheriting a tree it did not build.
 */
public static class WebHomeImport
{
    /** The decision marker, beside the home it describes. */
    public const string DecisionFile = ".web-import-decision.json";
    private const string TempSuffix = ".import-tmp";

    /** Home-level files worth carrying over. */
    private static readonly string[] HomeFiles = { "settings.yaml", ".credentials.yaml" };

    /** Home-level directories worth carrying over. */
    private static readonly string[] HomeDirectories =
    {
        "sessions", "storages", ".agent-presets", "skills", "attachments", "plugins",
    };

    /** Profile-level files: the profile's own declaration, not its packages. */
    private static readonly string[] ProfileFiles =
    {
        "package.json", "cordis.patch.yml", ".npmrc", "pnpm-workspace.yaml",
    };

    /** Packages that belong to the shared tree, never treated as community plugins. */
    private static readonly HashSet<string> NotCommunityPlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "dshmarket", "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
    };

    private const string ProfileName = "web";

    /**
     * Where the web version keeps its home.
     *
     * `SpecialFolder.UserProfile` is used rather than the `USERPROFILE`
     * environment variable, because the former is the documented way to ask and
     * the latter is only one of the inputs to it - setting the variable does not
     * move this answer. Callers that need a different location pass one; the
     * command line does that with --web-home.
     */
    public static string DefaultWebHome() => DefaultWebHome(null);

    public static string DefaultWebHome(string? overridden)
    {
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".dsh")
            : Path.Combine(home, ".dsh");
    }

    public static string DecisionPath(string home) => Path.Combine(home, DecisionFile);

    /** What the web home holds, for the question the user is asked. */
    public sealed record Preview(bool Exists, int Sessions, int Presets, int Plugins, bool HasCredentials)
    {
        public string Describe()
        {
            if (!Exists) return "no web home was found";
            var parts = new List<string>();
            parts.Add(Sessions == 1 ? "1 session" : Sessions + " sessions");
            if (Presets > 0) parts.Add(Presets == 1 ? "1 preset" : Presets + " presets");
            if (Plugins > 0) parts.Add(Plugins == 1 ? "1 plugin" : Plugins + " plugins");
            if (HasCredentials) parts.Add("stored credentials");
            return string.Join(", ", parts);
        }
    }

    /** Reads what a web home holds. Never writes, never throws. */
    public static Preview Inspect(string webHome)
    {
        try
        {
            if (!Directory.Exists(webHome)) return new Preview(false, 0, 0, 0, false);
            return new Preview(
                true,
                CountEntries(Path.Combine(webHome, "sessions")),
                CountEntries(Path.Combine(webHome, ".agent-presets")),
                CountEntries(Path.Combine(webHome, "plugins")),
                File.Exists(Path.Combine(webHome, ".credentials.yaml")));
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the web home: " + ex.Message);
            return new Preview(false, 0, 0, 0, false);
        }
    }

    /**
     * Whether the desktop home is still unused enough to import into. Credentials
     * or a generation tree mean somebody has already run the desktop app here, and
     * copying over that would be destructive.
     */
    public static bool DesktopHomeIsUnused(string home)
    {
        try
        {
            if (File.Exists(Path.Combine(home, ".credentials.yaml"))) return false;
            var generations = Path.Combine(HarnessProfile.Dir(home), ".generations");
            if (Directory.Exists(generations) && Directory.EnumerateFileSystemEntries(generations).Any()) return false;
            return true;
        }
        catch (Exception ex)
        {
            // Unreadable is treated as used: refusing to import is the safe way
            // to be wrong here.
            Log.Warn("could not tell whether the desktop home is unused: " + ex.Message);
            return false;
        }
    }

    /** The recorded answer, or null when the user has not been asked. */
    public static string? Decision(string home)
    {
        try
        {
            var path = DecisionPath(home);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var decision = document.RootElement.TryGetProperty("decision", out var d) ? d.GetString() : null;
            return decision is "imported" or "skipped" ? decision : null;
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the web-import decision: " + ex.Message);
            return null;
        }
    }

    /** Whether the app should offer the import on this launch. */
    public static bool ShouldOffer(string home, string webHome)
        => Decision(home) == null
           && Inspect(webHome).Exists
           && DesktopHomeIsUnused(home);

    /** Records that the user said no, so the offer is not repeated. */
    public static string? Skip(string home, string webHome)
        => WriteDecision(home, "skipped", webHome, null);

    /**
     * Copies the web home into the desktop home and records the decision. The
     * copy is staged and promoted by one rename; on any failure the staging
     * directory is removed and the desktop home is left as it was.
     */
    public static string? Import(string home, string webHome, out List<string> copied)
    {
        copied = new List<string>();
        string? staging = null;
        try
        {
            if (!Directory.Exists(webHome)) return "there is no web home to import from";
            if (!DesktopHomeIsUnused(home))
            {
                return "this home already has credentials or plugins, so importing into it would "
                       + "overwrite work done here";
            }

            staging = home + TempSuffix;
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            CopyPayload(webHome, staging, copied);

            // Promote: a single rename, so the home is never half-populated.
            // CreateDirectory is not enough on its own - the desktop home may
            // already exist as an empty shell, in which case the payload moves in.
            if (Directory.Exists(home))
            {
                MergeInto(staging, home, copied);
                Directory.Delete(staging, recursive: true);
            }
            else
            {
                Directory.Move(staging, home);
            }
            staging = null;

            var error = WriteDecision(home, "imported", webHome, copied.Count);
            if (error != null) return error;
            return null;
        }
        catch (Exception ex)
        {
            return "the web home could not be imported: " + ex.Message;
        }
        finally
        {
            try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }

    private static void CopyPayload(string source, string destination, List<string> copied)
    {
        foreach (var name in HomeFiles)
        {
            CopyFileIfPresent(Path.Combine(source, name), Path.Combine(destination, name), copied);
        }
        foreach (var name in HomeDirectories)
        {
            CopyDirectoryIfPresent(Path.Combine(source, name), Path.Combine(destination, name), copied);
        }

        var sourceProfile = Path.Combine(source, "profiles", ProfileName);
        var destinationProfile = Path.Combine(destination, "profiles", ProfileName);
        if (!Directory.Exists(sourceProfile)) return;

        foreach (var name in ProfileFiles)
        {
            CopyFileIfPresent(Path.Combine(sourceProfile, name), Path.Combine(destinationProfile, name), copied);
        }

        // Community plugins by declaration only. Which ones they are comes from
        // the profile manifest, so anything not named there is left behind.
        foreach (var plugin in CommunityPlugins(sourceProfile))
        {
            var from = Path.Combine(sourceProfile, "node_modules", plugin, "package.json");
            var to = Path.Combine(destinationProfile, "node_modules", plugin, "package.json");
            CopyFileIfPresent(from, to, copied);
        }
    }

    /** The community plugins a web profile declares, minus the shared tree. */
    private static IEnumerable<string> CommunityPlugins(string profileDir)
    {
        var manifest = Path.Combine(profileDir, "package.json");
        if (!File.Exists(manifest)) yield break;
        List<string> names;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            names = new List<string>();
            if (document.RootElement.TryGetProperty("dependencies", out var deps)
                && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in deps.EnumerateObject()) names.Add(property.Name);
            }
            if (document.RootElement.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var bundle in bundles.EnumerateArray())
                {
                    var value = bundle.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && !names.Contains(value!)) names.Add(value!);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the web profile manifest: " + ex.Message);
            yield break;
        }

        foreach (var name in names)
        {
            if (NotCommunityPlugins.Contains(name)) continue;
            // A filesystem spec is a path, not a package name to look up.
            if (name.Contains(':') || name.Contains('/') || name.StartsWith(".")) continue;
            yield return name;
        }
    }

    private static void CopyFileIfPresent(string from, string to, List<string> copied)
    {
        try
        {
            if (!File.Exists(from)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            copied.Add(Path.GetFileName(from));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not copy {from}: {ex.Message}");
        }
    }

    private static void CopyDirectoryIfPresent(string from, string to, List<string> copied)
    {
        if (!Directory.Exists(from)) return;
        try
        {
            CopyTree(from, to);
            copied.Add(Path.GetFileName(from) + "/");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not copy {from}: {ex.Message}");
        }
    }

    /** Recursive copy that refuses to leave the destination. */
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            // A symlink or junction is not followed: a shared tree is not this
            // home's to duplicate, and following one can escape the destination.
            var attributes = File.GetAttributes(directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    /** Moves a staged tree into an existing home without replacing it wholesale. */
    private static void MergeInto(string staging, string home, List<string> copied)
    {
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            var target = Path.Combine(home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }

    private static int CountEntries(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFileSystemEntries(directory).Count(e => !Path.GetFileName(e).StartsWith('.'))
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? WriteDecision(string home, string decision, string source, int? copied)
    {
        try
        {
            Directory.CreateDirectory(home);
            var payload = new
            {
                decision,
                source,
                decidedAtUtc = DateTime.UtcNow,
                copied,
            };
            var path = DecisionPath(home);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            // The copy succeeded; only the record of it failed.
            Log.Warn("could not record the web-import decision: " + ex.Message);
            return "the import succeeded but its decision could not be recorded: " + ex.Message;
        }
    }

    /**
     * --import-web-home / --skip-web-home: the command line form, for scripting
     * and for the smoke suite. Exit codes: 0 done, 1 the import failed,
     * 2 nothing to import.
     */
    public static int RunCli(Options o, bool skip)
    {
        var home = o.ResolveHome();
        var webHome = DefaultWebHome(o.WebHome);
        Console.WriteLine("== DeepSeek Harness web home import ==");
        Console.WriteLine($"web home    : {webHome}");
        Console.WriteLine($"desktop home: {home}");

        var preview = Inspect(webHome);
        Console.WriteLine($"found       : {preview.Describe()}");
        Console.WriteLine($"recorded    : {Decision(home) ?? "(nothing yet)"}");

        if (skip)
        {
            if (!preview.Exists) return 2;
            var skipError = Skip(home, webHome);
            Console.WriteLine(skipError ?? "decision : skipped");
            return skipError == null ? 0 : 1;
        }

        if (!preview.Exists) return 2;
        if (!DesktopHomeIsUnused(home))
        {
            Console.Error.WriteLine("failed : this home already has credentials or plugins; "
                                    + "importing into it would overwrite work done here");
            return 1;
        }

        var error = Import(home, webHome, out var copied);
        if (error != null)
        {
            Console.Error.WriteLine("failed : " + error);
            return 1;
        }
        Console.WriteLine($"copied      : {copied.Count} item(s)");
        foreach (var item in copied.Take(20)) Console.WriteLine("  " + item);
        if (copied.Count > 20) Console.WriteLine($"  ... and {copied.Count - 20} more");
        Console.WriteLine("== the web home was imported ==");
        return 0;
    }
}
