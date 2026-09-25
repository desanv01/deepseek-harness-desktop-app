using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DShNative;

/**
 * The record that this home is booting trimmed, and what to put back.
 *
 * A whole-manifest backup (`package.json.safe-mode.bak`) is enough to describe
 * the state at the moment safe mode was entered, but it is not enough to restore
 * from later: anything that touches the profile while safe mode is active - a
 * plugin removal, a failed install, a harness upgrade reconciling bundles -
 * makes the copy describe a world that no longer exists. Restoring it would then
 * silently discard whatever changed.
 *
 * So the marker records the intent instead of a snapshot: the ordered list the
 * profile had, and the subset this entry removed. Leaving safe mode puts back
 * only what is still missing, which leaves every later change intact.
 *
 * It sits beside the profile manifest rather than under the app's data root
 * because it describes that profile; moving a home takes its safe-mode state
 * with it.
 */
public sealed class SafeModeState
{
    public DateTime EnteredAtUtc { get; set; }

    /** The bundle stack as it was before the trim, in order. */
    public List<string> Before { get; set; } = new();

    /** The subset this entry actually removed. */
    public List<string> Removed { get; set; } = new();

    public const string FileName = ".safe-mode.json";

    public static string FileFor(string home) => Path.Combine(HarnessProfile.Dir(home), FileName);

    public static SafeModeState? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<SafeModeState>(File.ReadAllText(path));
            if (state == null) return null;
            state.Before ??= new List<string>();
            state.Removed ??= new List<string>();
            return state;
        }
        catch (Exception ex)
        {
            // A marker that cannot be read is treated as absent: the caller then
            // reports "not in safe mode" instead of restoring a guess.
            Log.Warn($"could not read the safe-mode marker {path}: {ex.Message}");
            return null;
        }
    }

    public static void Write(string path, IReadOnlyList<string> before, IReadOnlyList<string> removed)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new SafeModeState
            {
                EnteredAtUtc = DateTime.UtcNow,
                Before = new List<string>(before),
                Removed = new List<string>(removed),
            };
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Safe mode still booted; only the ability to leave it in one step is
            // lost, and the manifest backup remains for a manual restore.
            Log.Warn($"could not write the safe-mode marker {path}: {ex.Message}");
        }
    }

    /**
     * Clears the marker and the manifest backup once the profile is whole again.
     * A failure here leaves a stale marker, which the next exit reports as
     * "already held every bundle" rather than restoring anything twice.
     */
    public static void Delete(string path, string home)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn("could not clear the safe-mode marker: " + ex.Message); }

        try
        {
            var backup = HarnessProfile.ManifestPath(home) + ".safe-mode.bak";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception ex) { Log.Warn("could not clear the safe-mode manifest backup: " + ex.Message); }
    }
}
