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

    /**
     * Whether the harness keeps running after the window closes. On (the
     * default) makes the next launch an attach instead of a boot; off restores
     * "closing the window stops the server".
     */
    public bool KeepServerRunning { get; set; } = true;

    /** Whether a launch asks GitHub for a newer build of this app (default: yes). */
    public bool CheckForUpdates { get; set; } = true;

    /**
     * Release feed to read instead of the GitHub API. Only useful for testing
     * (a file:// fixture) or for a fork that publishes its own builds.
     */
    public string? UpdateFeedUrl { get; set; }

    /** Most recently opened projects, newest first. */
    public List<string> RecentProjects { get; set; } = new();

    /**
     * The main window's geometry from the last session, so a window the user
     * sized and placed comes back that way. Null until a window has been closed
     * at least once.
     *
     * The normal bounds are stored rather than the current ones, so a window
     * closed while maximized or minimized still remembers the size to restore to
     * when it is un-maximized.
     */
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /**
     * Whether there is a usable geometry to restore. A stored size smaller than
     * the window's own minimum is treated as absent rather than honoured, so a
     * corrupt or stale value cannot produce a window too small to use.
     */
    public bool HasWindowBounds
    {
        get
        {
            if (WindowWidth is not int width || WindowHeight is not int height) return false;
            return width >= 400 && height >= 300;
        }
    }

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
