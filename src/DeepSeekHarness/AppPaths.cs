using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DShNative;

/** App-data layout under %LOCALAPPDATA%\DeepSeekHarness, or $DSH_DESKTOP_HOME. */
public static class AppPaths
{
    /**
     * Set this to keep every file the app owns - settings, logs, leases, the
     * WebView2 profile - under one directory instead of %LOCALAPPDATA%, which
     * makes the app portable and lets tests run against a scratch root.
     */
    public const string RootEnvVar = "DSH_DESKTOP_HOME";

    private static string? _root;

    public static string Root => _root ??= ResolveRoot();

    /** True when the root came from the environment rather than %LOCALAPPDATA%. */
    public static bool IsPortable { get; private set; } = !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(RootEnvVar));

    /** True when the chosen root was unusable and %TEMP% had to be used instead. */
    public static bool IsTemporary { get; private set; }

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable(RootEnvVar);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                var full = Path.GetFullPath(custom.Trim().Trim('"'));
                // Keep a drive or share root intact ("C:\" must not become "C:").
                var basePath = Path.GetPathRoot(full) ?? string.Empty;
                return full.Length > basePath.Length
                    ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : full;
            }
            catch
            {
                // an unusable override falls back to the per-user default
            }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness");
    }

    public static string LogsDir => Path.Combine(Root, "logs");
    public static string WebView2Data => Path.Combine(Root, "webview2");

    /** Staged desktop-app updates: one folder per release tag. */
    public static string UpdatesDir => Path.Combine(Root, "updates");

    /** The desktop app's own DSH plugins, extracted from the executable. */
    public static string PluginsDir => Path.Combine(Root, "plugins");

    /** What the last successful boot looked like. */
    public static string BootStateFile => Path.Combine(Root, "boot-state.json");

    /** Cached answer of the last GitHub release check. */
    public static string UpdateCheckCacheFile => Path.Combine(Root, "update-check.json");

    /** The staged update a helper is about to swap in. */
    public static string PendingUpdateFile => Path.Combine(UpdatesDir, "pending.json");

    /** Where the staging area for one release tag lives. */
    public static string UpdateStageDir(string tag)
    {
        var safe = new string((tag ?? "release")
            .Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            .ToArray());
        return Path.Combine(UpdatesDir, safe.Length == 0 ? "release" : safe);
    }

    /** The dedicated harness home this app owns unless --dsh-home overrides it. */
    public static string DefaultHome => Path.Combine(Root, "home");

    /** Stable, non-secret identity for one DSH_HOME directory. */
    public static string HomeKey(string home)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeHome(home)))).ToLowerInvariant();
    }

    public static string HomeMutexName(string home)
        => "Local\\DeepSeekHarness-home-" + HomeKey(home);

    /**
     * Guards the profile's own files, separately from the home lock.
     *
     * The home lock decides who may run a server; this one only serialises the
     * read-modify-write pairs on `package.json` and `cordis.patch.yml`. They are
     * different concerns: a plugin toggle happens while a server is already
     * running, and it must not have to take the home lock to write two files.
     */
    public static string ProfileWriterMutexName(string home)
        => "Local\\DeepSeekHarness-profile-" + HomeKey(home);

    /** One server per home: the owner publishes its endpoint here for attachers. */
    public static string HomeLeaseFile(string home)
        => Path.Combine(Root, "instance-" + HomeKey(home) + ".json");

    /** User settings: last project, preferred home, pinned port, recent list. */
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /** Event a second launch sets to bring the owning window forward. */
    public static string FocusEventName(string home)
        => "Local\\DeepSeekHarness-focus-" + HomeKey(home);

    /**
     * Removes old server and update logs so a long-lived installation cannot
     * accumulate files. desktop.log is excluded; Log rotates that one.
     */
    public static void PruneLogs(int keepDays = 7, int keepFiles = 60)
    {
        try
        {
            var directory = new DirectoryInfo(LogsDir);
            if (!directory.Exists) return;
            var cutoff = DateTime.UtcNow.AddDays(-keepDays);
            var files = directory.GetFiles("*.log");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                if (file.Name.Equals("desktop.log", StringComparison.OrdinalIgnoreCase)) continue;
                if (i >= keepFiles || file.LastWriteTimeUtc < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("log pruning failed: " + ex.Message);
        }
    }

    private static string NormalizeHome(string home)
    {
        try
        {
            var full = Path.GetFullPath(home);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .ToLowerInvariant();
        }
        catch
        {
            return home.Trim().ToLowerInvariant();
        }
    }

    /** Serializes npm updates across launcher instances. */
    public static string UpdateMutexName => "Local\\DeepSeekHarness-npm-update";

    /** A unique log path prevents concurrent launchers from sharing a file handle. */
    public static string NewLogPath(string prefix, string suffix = ".log")
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(LogsDir, $"{prefix}-{stamp}-{Environment.ProcessId}-{Guid.NewGuid():N}{suffix}");
    }

    /**
     * Creates the data directories. The chosen root wins; when it cannot be
     * written - the app unpacked into a read-only folder, or a DSH_DESKTOP_HOME
     * pointing somewhere protected - the session continues under %TEMP% instead
     * of refusing to start.
     */
    public static void Ensure()
    {
        if (TryCreate(Root)) return;

        var fallback = Path.Combine(Path.GetTempPath(), "DeepSeekHarness");
        if (!TryCreate(fallback))
        {
            throw new IOException($"neither {Root} nor {fallback} could be created");
        }

        _root = fallback;
        IsPortable = false;
        IsTemporary = true;
        Log.Warn($"the data root could not be written to; using {fallback} for this session");
    }

    private static bool TryCreate(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
