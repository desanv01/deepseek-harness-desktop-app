using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DShNative;

/** What the last successful boot looked like, so a change can be named later. */
public sealed class BootState
{
    public string? HarnessVersion { get; set; }
    public DateTime LastGoodBootUtc { get; set; }
    public string? ProjectDir { get; set; }
    public List<string> Bundles { get; set; } = new();

    /**
     * A harness version that was installed but has not yet booted. The next
     * boot is what proves the plugin tree still loads on it, so until that
     * happens the app can name the change if the boot fails.
     */
    public string? PendingHarnessVersion { get; set; }
    public DateTime? PendingSinceUtc { get; set; }

    public static BootState Load()
    {
        try
        {
            if (File.Exists(AppPaths.BootStateFile))
            {
                var state = JsonSerializer.Deserialize<BootState>(File.ReadAllText(AppPaths.BootStateFile));
                if (state != null)
                {
                    state.Bundles ??= new List<string>();
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the boot state: " + ex.Message);
        }
        return new BootState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = AppPaths.BootStateFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, AppPaths.BootStateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("could not write the boot state: " + ex.Message);
        }
    }
}

/**
 * Recovery for a harness home that cannot boot.
 *
 * The profile is composed from every bundle in `dsh.profile.bundles`, and the
 * loader aborts the whole tree when one entry fails - a plugin written for a
 * different harness version does exactly that, and it takes the app down with
 * it. The failure is loud in the server's own output
 * (`failed to apply loader entry <id>: ...`), so the app reads it, disables that
 * row through the profile's patch layer, and tries once more. The user gets a
 * running app and a named culprit instead of a window that never opens.
 */
public static class SafeMode
{
    /**
     * The loader's own words, in both shapes 0.1.5 produces:
     *
     *   failed to import loader entry thrower (dsh-plugin-thrower): <reason>
     *   failed to apply loader entry <id>: <reason>
     *
     * The import form is the useful one: it names the row AND the package. The
     * apply form is wrapped around the whole include, so an entry called
     * "include" is the wrapper, never the culprit.
     */
    private static readonly Regex ImportFailedEntry = new(
        @"failed to import loader entry\s+(?<id>[^\s(]+)\s*(?:\((?<pkg>[^)\r\n]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ApplyFailedEntry = new(
        @"failed to apply loader entry\s+(?<id>[^\s:]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record Recovery(bool Recovered, string? DisabledRow, string? Bundle, string? Detail);

    /** The row and package a failed boot names, if it names them. */
    public static (string? RowId, string? Package) FailedEntry(string? logText)
    {
        if (string.IsNullOrWhiteSpace(logText)) return (null, null);

        var import = ImportFailedEntry.Match(logText);
        if (import.Success)
        {
            var package = import.Groups["pkg"].Success ? import.Groups["pkg"].Value.Trim() : null;
            return (import.Groups["id"].Value, string.IsNullOrWhiteSpace(package) ? null : package);
        }

        var apply = ApplyFailedEntry.Match(logText);
        if (apply.Success)
        {
            var id = apply.Groups["id"].Value;
            return id.Equals("include", StringComparison.OrdinalIgnoreCase) ? (null, null) : (id, null);
        }

        return (null, null);
    }

    /** The bundle that mounts a given row id, for a message that names a package. */
    public static string? BundleForRow(string home, string rowId)
    {
        foreach (var bundle in HarnessProfile.ReadBundles(home))
        {
            var packageDir = Path.Combine(HarnessProfile.ModulesDir(home), bundle);
            if (HarnessProfile.ReadPatchRowIds(packageDir).Contains(rowId, StringComparer.OrdinalIgnoreCase))
            {
                return bundle;
            }
        }
        return null;
    }

    /** The tail of a failed server's output, which is where the loader explains itself. */
    public static string ReadFailureText(ServerLease? lease = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (var path in new[] { ServerManager.LastOutLog, ServerManager.LastErrLog })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                var content = File.ReadAllText(path);
                text.AppendLine(content.Length > 8000 ? content[^8000..] : content);
            }
            catch (Exception ex)
            {
                Log.Warn($"could not read {path}: {ex.Message}");
            }
        }
        return text.ToString();
    }

    /**
     * Disables whatever a failed boot named: every row the offending package
     * mounts, or the named row when the package is not one of this home's
     * bundles. Returns what happened, including when there was nothing to act on.
     */
    public static Recovery DisableFailedPlugin(string home, string failureText)
    {
        var (rowId, package) = FailedEntry(failureText);
        if (rowId == null && package == null)
        {
            return new Recovery(false, null, null, "the failure did not name a plugin row");
        }

        // Prefer the package: disabling every row it mounts is what actually
        // gets the profile past the plugin, not just the one row it choked on.
        var rows = new List<string>();
        if (package != null)
        {
            var packageDir = Path.Combine(HarnessProfile.ModulesDir(home), package);
            rows.AddRange(HarnessProfile.ReadPatchRowIds(packageDir));
        }
        if (rows.Count == 0 && rowId != null) rows.Add(rowId);
        if (rows.Count == 0)
        {
            return new Recovery(false, rowId, package, $"nothing to disable for {package ?? rowId}");
        }

        foreach (var row in rows)
        {
            var error = HarnessProfile.SetRowDisabled(home, row, disabled: true);
            if (error != null) return new Recovery(false, row, package, error);
        }

        var bundle = package ?? BundleForRow(home, rows[0]);
        var detail = bundle == null
            ? $"disabled profile row {string.Join(", ", rows)}"
            : $"disabled {bundle} (row {string.Join(", ", rows)})";
        Log.Warn("safe mode: " + detail);
        return new Recovery(true, rows[0], bundle, detail);
    }

    /**
     * --safe-mode: boot with the base bundles only, keeping the previous bundle
     * list beside the manifest so nothing is lost. This is the escape hatch for
     * a home whose profile cannot be trusted at all.
     */
    public static string? Enter(string home)
    {
        try
        {
            var manifest = HarnessProfile.ManifestPath(home);
            if (!File.Exists(manifest)) return "there is no profile to trim";

            var backup = manifest + ".safe-mode.bak";
            File.Copy(manifest, backup, overwrite: true);

            foreach (var bundle in HarnessProfile.ReadBundles(home))
            {
                if (HarnessProfile.BaseBundles.Contains(bundle, StringComparer.OrdinalIgnoreCase)) continue;
                var error = HarnessProfile.RemoveBundle(home, bundle);
                if (error != null) return error;
                Log.Warn($"safe mode: {bundle} removed from the bundle stack (manifest kept at {backup})");
            }
            return null;
        }
        catch (Exception ex)
        {
            return "safe mode could not trim the profile: " + ex.Message;
        }
    }

    /** Records a boot that worked, so a later failure can be compared with it. */
    public static void RememberGoodBoot(string home, Tools tools)
    {
        try
        {
            var state = BootState.Load();
            state.HarnessVersion = tools.DshVersion;
            state.LastGoodBootUtc = DateTime.UtcNow;
            state.ProjectDir = Options.Current?.ProjectDir;
            state.Bundles = HarnessProfile.ReadBundles(home);
            // This boot is the verification: nothing is pending any more.
            state.PendingHarnessVersion = null;
            state.PendingSinceUtc = null;
            state.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("could not record the boot state: " + ex.Message);
        }
    }

    /**
     * Notes that a harness version was installed and has not booted yet. A
     * harness update is the one change that can break a plugin the app did not
     * touch, so the next boot has to be attributable to it.
     */
    public static void NoteHarnessUpdate(Tools tools, string? installedVersion)
    {
        try
        {
            var state = BootState.Load();
            state.PendingHarnessVersion = installedVersion;
            state.PendingSinceUtc = DateTime.UtcNow;
            state.Save();
            Log.Info($"harness {installedVersion ?? "?"} is installed; the next boot verifies the plugin tree "
                     + $"(known good: {tools.DshVersion ?? "unknown"})");
        }
        catch (Exception ex)
        {
            Log.Warn("could not record the pending harness verification: " + ex.Message);
        }
    }

    /** The pending harness version, when one is waiting to be verified. */
    public static string? PendingHarnessVersion()
    {
        var state = BootState.Load();
        return state.PendingHarnessVersion;
    }

    /** A sentence naming what changed since the last good boot, when anything did. */
    public static string? DescribeChange(string home, Tools tools)
    {
        var state = BootState.Load();
        var lines = new List<string>();

        if (state.LastGoodBootUtc == default)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(state.HarnessVersion)
            && !string.IsNullOrEmpty(tools.DshVersion)
            && !string.Equals(state.HarnessVersion, tools.DshVersion, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"the harness was updated from {state.HarnessVersion} to {tools.DshVersion}");
        }
        else if (!string.IsNullOrEmpty(state.PendingHarnessVersion)
                 && !string.Equals(state.PendingHarnessVersion, tools.DshVersion, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"harness {state.PendingHarnessVersion} was installed, and this is the first boot since");
        }

        var bundles = HarnessProfile.ReadBundles(home);
        var added = bundles.Where(b => !state.Bundles.Contains(b, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count > 0)
        {
            lines.Add("added since the last good boot: " + string.Join(", ", added));
        }

        return lines.Count == 0 ? null : string.Join("; ", lines);
    }
}
