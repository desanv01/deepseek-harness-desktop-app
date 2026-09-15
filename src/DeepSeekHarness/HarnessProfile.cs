using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DShNative;

/**
 * The harness profile a home runs: its bundle stack and patch layer.
 *
 * A profile is `<DSH_HOME>/profiles/web/`: `package.json` declares
 * `dsh.profile.bundles` (the packages whose cordis.patch.yml layers compose the
 * plugin tree), `cordis.patch.yml` is the home's own patch layer, and every
 * bundle resolves from `node_modules`.
 *
 * The app manages it directly rather than shelling out for everything: reading
 * and writing these two files needs no package manager, which keeps our own
 * plugin installable offline and instantly, and leaves pnpm for third-party
 * packages that actually have dependencies to resolve.
 */
public static class HarnessProfile
{
    public const string Name = "web";

    /** Bundles a fresh profile starts with; the same list the CLI writes. */
    public static readonly string[] BaseBundles = { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" };

    public static string Dir(string home) => Path.Combine(home, "profiles", Name);
    public static string ManifestPath(string home) => Path.Combine(Dir(home), "package.json");
    public static string PatchPath(string home) => Path.Combine(Dir(home), "cordis.patch.yml");
    public static string ModulesDir(string home) => Path.Combine(Dir(home), "node_modules");

    public static bool Exists(string home) => File.Exists(ManifestPath(home));

    /** True when this build can enable/disable profile rows (it always can). */
    public static bool CanManagePlugins => true;

    /** Bundle names in profile order; empty when the profile does not exist. */
    public static List<string> ReadBundles(string home)
    {
        var bundles = new List<string>();
        var manifest = ReadManifest(home);
        var list = manifest?["dsh"]?["profile"]?["bundles"] as JsonArray;
        if (list == null) return bundles;
        foreach (var node in list)
        {
            var value = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value)) bundles.Add(value!);
        }
        return bundles;
    }

    /**
     * The plugin entries the page shows: every bundle in the stack, with the
     * version it resolves to and whether the profile patch layer disabled it.
     * The two base bundles are marked built-in and are not offered a switch.
     */
    public static List<PluginEntry> ReadPlugins(string home)
    {
        var entries = new List<PluginEntry>();
        var disabledRows = ReadDisabledRows(home);

        foreach (var bundle in ReadBundles(home))
        {
            var builtIn = BaseBundles.Contains(bundle, StringComparer.OrdinalIgnoreCase);
            var packageDir = Path.Combine(ModulesDir(home), bundle);
            var version = ReadPackageVersion(packageDir);
            var rowIds = builtIn ? new List<string>() : ReadPatchRowIds(packageDir);
            var enabled = rowIds.Count == 0 || rowIds.Any(id => !disabledRows.Contains(id));
            entries.Add(new PluginEntry(bundle, version, enabled, builtIn));
        }

        return entries;
    }

    /** The version a bundle resolves to, or null when it is not installed. */
    public static string? ReadPackageVersion(string packageDir)
    {
        try
        {
            var manifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(manifest)) return null;
            return ReadManifestFile(manifest)?["version"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /**
     * Creates the profile when the home has none, preferring the CLI's own
     * initialization (it owns the template) and falling back to writing the same
     * list ourselves.
     */
    public static string? Ensure(string home, Tools tools, int timeoutMs = 60_000)
    {
        if (Exists(home)) return null;

        Log.Info($"no profile exists for {home}; initializing {Name}");
        if (!string.IsNullOrEmpty(tools.Node) && !string.IsNullOrEmpty(tools.DshCli))
        {
            var outFile = AppPaths.NewLogPath("profile-init", ".out.log");
            var errFile = AppPaths.NewLogPath("profile-init", ".err.log");
            // The CLI initializes the profile for its own DSH_HOME, so it has to
            // be told which home this is; otherwise it writes to the default one.
            Proc.Run(
                tools.Node!,
                new[] { tools.DshCli!, "plugin", "--profile", Name, "list" },
                outFile,
                errFile,
                timeoutMs,
                ct: default,
                environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DSH_HOME"] = home });
            if (Exists(home))
            {
                Log.Info("the CLI initialized the profile");
                return null;
            }
        }

        try
        {
            Directory.CreateDirectory(Dir(home));
            var manifest = new JsonObject
            {
                ["name"] = "dsh-profile-" + Name,
                ["private"] = true,
                ["dependencies"] = new JsonObject(),
                ["dsh"] = new JsonObject
                {
                    ["profile"] = new JsonObject
                    {
                        ["bundles"] = new JsonArray(BaseBundles.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
                        ["patchReload"] = "live",
                    },
                },
            };
            File.WriteAllText(ManifestPath(home), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"wrote the profile manifest at {ManifestPath(home)}");
            return null;
        }
        catch (Exception ex)
        {
            return "the profile could not be created: " + ex.Message;
        }
    }

    /**
     * Installs a plugin package into the profile by copying it in, and makes sure
     * the bundle stack mentions it. No package manager is involved, which is why
     * this works offline and in a second.
     */
    public static string? InstallPlugin(string home, string sourceDir, string packageName, string? version = null)
    {
        if (!Directory.Exists(sourceDir)) return $"the plugin source {sourceDir} is missing";

        try
        {
            Directory.CreateDirectory(ModulesDir(home));
            var target = Path.Combine(ModulesDir(home), packageName);

            // A link left by a package manager is replaced by a real copy: the
            // app owns this plugin, so it must not depend on the source folder
            // staying where it was.
            if (Directory.Exists(target))
            {
                var attributes = File.GetAttributes(target);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(target);
                else Directory.Delete(target, recursive: true);
            }

            CopyDirectory(sourceDir, target);
            Log.Info($"installed {packageName}{(version == null ? "" : " " + version)} into {target}");

            var error = AddDependency(home, packageName);
            if (error != null) return error;
            return AddBundle(home, packageName);
        }
        catch (Exception ex)
        {
            return $"could not install {packageName}: {ex.Message}";
        }
    }

    /** True when the bundle stack already mentions this package. */
    public static bool HasBundle(string home, string bundle)
        => ReadBundles(home).Contains(bundle, StringComparer.OrdinalIgnoreCase);

    /** Adds a bundle to the stack, keeping its existing order. */
    public static string? AddBundle(string home, string bundle)
    {
        if (HasBundle(home, bundle)) return null;

        var manifest = ReadManifest(home);
        if (manifest == null) return "the profile manifest is missing";

        var profile = manifest["dsh"]?["profile"] as JsonObject;
        if (profile == null)
        {
            profile = new JsonObject();
            (manifest["dsh"] as JsonObject ?? EnsureObject(manifest, "dsh"))["profile"] = profile;
        }

        var bundles = profile["bundles"] as JsonArray;
        if (bundles == null)
        {
            bundles = new JsonArray();
            profile["bundles"] = bundles;
        }
        bundles.Add(JsonValue.Create(bundle));
        return WriteManifest(home, manifest) ? null : "the profile manifest could not be written";
    }

    /** Removes a bundle from the stack. */
    public static string? RemoveBundle(string home, string bundle)
    {
        var manifest = ReadManifest(home);
        if (manifest == null) return "the profile manifest is missing";
        if (manifest["dsh"]?["profile"]?["bundles"] is not JsonArray bundles) return null;

        var kept = bundles.Where(n => !string.Equals(n?.GetValue<string>(), bundle, StringComparison.OrdinalIgnoreCase))
            .Select(n => (JsonNode)JsonValue.Create(n!.GetValue<string>())!)
            .ToArray();
        (manifest["dsh"]!["profile"] as JsonObject)!["bundles"] = new JsonArray(kept);
        return WriteManifest(home, manifest) ? null : "the profile manifest could not be written";
    }

    /** Row ids the profile's patch layer currently disables. */
    public static HashSet<string> ReadDisabledRows(string home)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PatchPath(home))) return disabled;
            var lines = File.ReadAllLines(PatchPath(home));
            string? currentId = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("- id:", StringComparison.Ordinal))
                {
                    currentId = line["- id:".Length..].Trim().Trim('\'', '"');
                    continue;
                }
                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    currentId = line["id:".Length..].Trim().Trim('\'', '"');
                    continue;
                }
                if (currentId != null && line.StartsWith("disabled:", StringComparison.Ordinal))
                {
                    var value = line["disabled:".Length..].Trim();
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) disabled.Add(currentId);
                    currentId = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the profile patch layer: " + ex.Message);
        }
        return disabled;
    }

    /** Row ids a bundle's own patch inserts, when it has one. */
    public static List<string> ReadPatchRowIds(string packageDir)
    {
        var ids = new List<string>();
        try
        {
            var patch = Path.Combine(packageDir, "cordis.patch.yml");
            if (!File.Exists(patch)) return ids;
            foreach (var raw in File.ReadAllLines(patch))
            {
                var line = raw.Trim();
                if (!line.StartsWith("- id:", StringComparison.Ordinal)) continue;
                var value = line["- id:".Length..].Trim().Trim('\'', '"');
                if (value.Length > 0) ids.Add(value);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read a bundle patch: " + ex.Message);
        }
        return ids;
    }

    /**
     * Disables or re-enables one mounted row through the profile patch layer -
     * the documented way to take a plugin out of the tree without uninstalling
     * it. Returns null on success.
     */
    public static string? SetRowDisabled(string home, string rowId, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(rowId)) return "no plugin row was named";
        try
        {
            Directory.CreateDirectory(Dir(home));
            var lines = File.Exists(PatchPath(home))
                ? File.ReadAllLines(PatchPath(home)).ToList()
                : new List<string>
                {
                    "# Your patch layer for this dsh profile, applied after every bundle layer:",
                    "# a top-level YAML array of loader patch entries.",
                    "[]",
                };

            // drop any existing entry for this row
            var kept = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("- id:", StringComparison.Ordinal)
                    && line["- id:".Length..].Trim().Trim('\'', '"').Equals(rowId, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < lines.Count && lines[i + 1].Trim().StartsWith("disabled:", StringComparison.Ordinal))
                    {
                        i++;
                    }
                    continue;
                }
                kept.Add(lines[i]);
            }

            if (disabled)
            {
                if (kept.Count == 1 && kept[0].Trim() == "[]") kept.Clear();
                kept.Add($"- id: {rowId}");
                kept.Add("  disabled: true");
            }
            else if (kept.Count == 0)
            {
                kept.Add("[]");
            }

            File.WriteAllLines(PatchPath(home), kept);
            Log.Info($"profile row {rowId} {(disabled ? "disabled" : "enabled")} through the patch layer");
            return null;
        }
        catch (Exception ex)
        {
            return $"the profile patch layer could not be written: {ex.Message}";
        }
    }

    /** Removes a plugin package from the profile entirely. */
    public static string? RemovePlugin(string home, string packageName)
    {
        try
        {
            var target = Path.Combine(ModulesDir(home), packageName);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        catch (Exception ex)
        {
            return $"could not remove {packageName}: {ex.Message}";
        }
        RemoveDependency(home, packageName);
        return RemoveBundle(home, packageName);
    }

    // ---- manifest plumbing ---------------------------------------------------

    private static JsonObject? ReadManifest(string home)
    {
        var path = ManifestPath(home);
        if (!File.Exists(path)) return null;
        return ReadManifestFile(path);
    }

    private static JsonObject? ReadManifestFile(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {path}: {ex.Message}");
            return null;
        }
    }

    private static bool WriteManifest(string home, JsonObject manifest)
    {
        try
        {
            // Unique temp name: the CLI may hold the file open while it works.
            var temp = ManifestPath(home) + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ManifestPath(home), overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("could not write the profile manifest: " + ex.Message);
            return false;
        }
    }

    private static string? AddDependency(string home, string packageName)
    {
        var manifest = ReadManifest(home);
        if (manifest == null) return "the profile manifest is missing";
        var dependencies = EnsureObject(manifest, "dependencies");
        if (dependencies[packageName] == null)
        {
            dependencies[packageName] = "file:./node_modules/" + packageName;
            if (!WriteManifest(home, manifest)) return "the profile manifest could not be written";
        }
        return null;
    }

    private static void RemoveDependency(string home, string packageName)
    {
        var manifest = ReadManifest(home);
        if (manifest?["dependencies"] is not JsonObject dependencies) return;
        if (dependencies.Remove(packageName)) WriteManifest(home, manifest!);
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
