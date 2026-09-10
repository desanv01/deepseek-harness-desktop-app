using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DShNative;

/** User settings persisted under %LOCALAPPDATA%\DeepSeekHarness\settings.json. */
public sealed class AppSettings
{
    private const int MaxRecentProjects = 8;

    /** Last project opened; used when no --project is given. */
    public string? ProjectDir { get; set; }

    /** Preferred DSH_HOME; used when no --dsh-home is given. */
    public string? DshHome { get; set; }

    /** Pinned port, or 0 for "let the OS pick". */
    public int Port { get; set; }

    /** Whether the npm update runs on launch. */
    public bool Update { get; set; }

    /** Most recently opened projects, newest first. */
    public List<string> RecentProjects { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (value != null)
                {
                    value.RecentProjects ??= new List<string>();
                    value.RecentProjects.RemoveAll(string.IsNullOrWhiteSpace);
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    /** Moves a project to the front of the recent list, capped and de-duplicated. */
    public void Remember(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir)) return;
        RecentProjects.RemoveAll(p => string.Equals(p, projectDir, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectDir);
        if (RecentProjects.Count > MaxRecentProjects)
            RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = AppPaths.SettingsFile + "." + Environment.ProcessId + ".tmp";
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("could not save settings: " + ex.Message);
        }
    }
}
