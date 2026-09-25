using System;
using System.Collections.Generic;
using System.IO;

namespace DShNative;

/**
 * Checks the profile before the harness is asked to boot it.
 *
 * A profile is a bundle stack plus the packages those bundles resolve to. When
 * the two disagree the loader aborts the whole tree - it does not skip the one
 * bad row - so the window never opens and the failure looks like a harness bug
 * rather than a half-finished install.
 *
 * The disagreement this handles is a bundle that is *declared but not present*.
 * That happens for ordinary reasons: a plugin removed with a file manager, an
 * interrupted `dsh plugin add`, or a manifest that named a package the install
 * never delivered. Disabling the plugin writes a patch row and cannot clear a
 * manifest-level fault, so the only repair is to drop the declaration.
 *
 * Deliberately narrow:
 *
 *   - It removes third-party declarations only. The base bundles are the
 *     harness's own and are not this app's to remove; if one is missing the
 *     profile needs the CLI, not a guess.
 *   - It never touches package files, user patch rows, or plugin data. The
 *     package is left on disk so a reinstall is a manifest line away.
 *   - It does not execute plugin code and does not claim the profile will boot
 *     afterwards. It removes one class of certain failure and reports what it
 *     did; the boot itself remains the proof.
 */
public static class ProfilePreflight
{
    /** What a preflight pass found and changed. */
    public sealed record Result(
        IReadOnlyList<string> Dropped,
        IReadOnlyList<string> Missing,
        string? Error)
    {
        public bool Changed => Dropped.Count > 0;

        /** One line for the log and for the user, or null when nothing happened. */
        public string? Describe()
        {
            if (Error != null) return Error;
            if (Dropped.Count == 0) return null;
            return $"removed {string.Join(", ", Dropped)} from the bundle stack "
                   + "(declared but not installed; the packages were left on disk)";
        }
    }

    /**
     * Drops bundle declarations whose package is not installed. Returns what it
     * did; never throws, because a preflight that fails must not stop a boot.
     */
    public static Result Repair(string home)
    {
        var dropped = new List<string>();
        var missing = new List<string>();
        try
        {
            if (!HarnessProfile.Exists(home)) return new Result(dropped, missing, null);

            var modules = HarnessProfile.ModulesDir(home);
            foreach (var bundle in HarnessProfile.ReadBundles(home))
            {
                if (HarnessProfile.BaseBundles.Contains(bundle, StringComparer.OrdinalIgnoreCase))
                {
                    // The harness's own rows: report a missing one, never remove it.
                    if (!IsInstalled(modules, bundle)) missing.Add(bundle);
                    continue;
                }

                if (IsInstalled(modules, bundle)) continue;

                var error = HarnessProfile.RemoveBundle(home, bundle);
                if (error != null) return new Result(dropped, missing, error);
                dropped.Add(bundle);
                Log.Warn($"preflight: {bundle} is declared in the profile but not installed; "
                         + "removing the declaration so the loader does not abort on it");
            }

            return new Result(dropped, missing, null);
        }
        catch (Exception ex)
        {
            return new Result(dropped, missing, "the profile could not be checked: " + ex.Message);
        }
    }

    /**
     * A bundle is installed when its package directory carries a manifest. A
     * directory without one is a half-written package, which is not usable as a
     * bundle layer either - so it counts as missing and its declaration goes.
     */
    private static bool IsInstalled(string modulesDir, string bundle)
        => File.Exists(Path.Combine(modulesDir, bundle, "package.json"));
}
