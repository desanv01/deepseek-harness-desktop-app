using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;

namespace DShNative;

/** CLI flags (see README). */
public sealed class Options
{
    public string Address { get; private set; } = "127.0.0.1";

    /** 0 means "let dsh pick a free port"; the real port comes from its ready line. */
    public int Port { get; private set; }

    /** Working directory of the managed server, or null when it must be resolved. */
    public string? ProjectDir { get; private set; }

    /** DSH_HOME for the managed server, or null when it must be resolved. */
    public string? DshHome { get; private set; }

    /** Opt-in npm update; off by default so a launch never mutates a working install. */
    public bool Update { get; private set; }
    public bool NoWindow { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Stop { get; private set; }
    public bool CheckHarness { get; private set; }

    /** --updates: open the updates window as soon as the app window is up. */
    public bool OpenUpdates { get; private set; }

    /** --install-update: download and verify the newest release, then exit. */
    public bool InstallUpdate { get; private set; }

    /** --repair-harness: install or repair the global harness CLI, then exit. */
    public bool RepairHarness { get; private set; }

    /** --install-plugin: install the bundled harness plugin into the home, then exit. */
    public bool InstallPlugin { get; private set; }

    /** --install-log-bridge: install the bundled log bridge into the home, then exit. */
    public bool InstallLogBridge { get; private set; }

    /** --import-web-home: bring a `dsh web` home into this app's home, then exit. */
    public bool ImportWebHome { get; private set; }

    /** --skip-web-home: record that the web home should be left alone, then exit. */
    public bool SkipWebHome { get; private set; }

    /** --web-home: read the web home from here instead of the user profile. */
    public string? WebHome { get; private set; }

    /** --bridge-selftest: exercise the page bridge protocol without a browser. */
    public bool BridgeSelfTest { get; private set; }

    /** --safe-mode: boot with the base bundles only, leaving added plugins aside. */
    public bool SafeMode { get; private set; }

    /** --exit-safe-mode: put back the bundles a safe-mode boot set aside. */
    public bool ExitSafeMode { get; private set; }

    /** --add-plugin <spec>: install a harness plugin into the home, then exit. */
    public string? AddPlugin { get; private set; }

    /** --remove-plugin <name>: remove one, then exit. */
    public string? RemovePlugin { get; private set; }

    /** --plugin-list: print the plugins this home runs, then exit. */
    public bool PluginList { get; private set; }

    /** --enable-plugin / --disable-plugin <name>: switch one through the patch layer. */
    public string? EnablePlugin { get; private set; }
    public string? DisablePlugin { get; private set; }

    /** --apply-now: with --install-update, hand over to the helper instead of stopping at staging. */
    public bool ApplyNow { get; private set; }

    /** --check-updates: report app + harness update state and exit. */
    public bool CheckUpdates { get; private set; }

    /** --no-update-check: this launch makes no update requests at all. */
    public bool NoUpdateCheck { get; private set; }

    /** Release feed to read instead of the GitHub API (file:// or a URL). */
    public string? UpdateFeedUrl { get; private set; }

    /** --dsh-cli: the harness entry point to use instead of searching for it. */
    public string? DshCli { get; private set; }
    public int ReadyTimeoutSec { get; private set; } = 240;

    /** True when the flag was named on the command line, so settings must not override it. */
    public bool ProjectSpecified { get; private set; }
    public bool HomeSpecified { get; private set; }
    public bool PortSpecified { get; private set; }
    public bool UpdateSpecified { get; private set; }

    /** True when the feed or the check policy was named on the command line. */
    public bool UpdateCheckSpecified { get; private set; }

    /** Whether the managed server outlives the window (settings can turn it off). */
    public bool KeepServerRunning { get; private set; } = true;

    /** True when the flag was named on the command line. */
    public bool KeepServerSpecified { get; private set; }

    /** Whether a launch asks the release feed for a newer build (settings can turn it off). */
    public bool CheckForUpdatesOnLaunch { get; private set; } = true;

    /** The release feed and launch policy, in the shape the window wants them. */
    public UpdatePolicy UpdatePolicy => new(CheckForUpdatesOnLaunch && !NoUpdateCheck, UpdateFeedUrl);

    /** The home to use after settings fall back to the app default. */
    public string ResolveHome() => DshHome ?? AppPaths.DefaultHome;

    /** Pop a message box? No in headless modes. */
    public bool ShowDialogs => !NoWindow && !SelfTest;

    public string Url
    {
        get
        {
            var host = Address.Contains(':') && !Address.StartsWith("[")
                ? "[" + Address + "]"
                : Address;
            return $"http://{host}:{Port}";
        }
    }

    /** Human-readable endpoint for messages; the port may still be auto. */
    public string TargetLabel => $"{Address}:{(Port == 0 ? "auto" : Port.ToString(CultureInfo.InvariantCulture))}";

    public static Options Parse(string[] args)
    {
        var o = new Options();        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port":
                case "-port":
                    o.Port = ReadInt(args, ref i, "port", 0, 65_535);
                    o.PortSpecified = true;
                    break;
                case "--address":
                case "-address":
                    o.Address = ReadAddress(args, ref i);
                    break;
                case "--project":
                case "-project":
                    o.ProjectDir = ReadExistingDirectory(args, ref i, "project");
                    o.ProjectSpecified = true;
                    break;
                case "--dsh-home":
                case "-dsh-home":
                case "--home":
                    o.DshHome = ReadHome(args, ref i);
                    o.HomeSpecified = true;
                    break;
                case "--dsh-cli":
                case "-dsh-cli":
                    o.DshCli = ReadDshCli(args, ref i);
                    break;
                case "--ready-timeout":
                case "-ready-timeout":
                    o.ReadyTimeoutSec = ReadInt(args, ref i, "ready-timeout", 10, 3_600);
                    break;
                case "--update":
                case "-update":
                    o.Update = true;
                    o.UpdateSpecified = true;
                    break;
                case "--no-update":
                case "-no-update":
                    o.Update = false;
                    o.UpdateSpecified = true;
                    break;
                case "--no-window":
                case "-no-window":
                    o.NoWindow = true;
                    break;
                case "--self-test":
                case "-self-test":
                    o.SelfTest = true;
                    break;
                case "--check-harness":
                case "-check-harness":
                    o.CheckHarness = true;
                    break;
                case "--check-updates":
                case "-check-updates":
                    o.CheckUpdates = true;
                    break;
                case "--updates":
                case "-updates":
                    o.OpenUpdates = true;
                    break;
                case "--install-update":
                case "-install-update":
                    o.InstallUpdate = true;
                    break;
                case "--apply-now":
                case "-apply-now":
                    o.ApplyNow = true;
                    break;
                case "--repair-harness":
                case "-repair-harness":
                    o.RepairHarness = true;
                    break;
                case "--install-plugin":
                case "-install-plugin":
                    o.InstallPlugin = true;
                    break;
                case "--install-log-bridge":
                case "-install-log-bridge":
                    o.InstallLogBridge = true;
                    break;
                case "--import-web-home":
                case "-import-web-home":
                    o.ImportWebHome = true;
                    break;
                case "--skip-web-home":
                case "-skip-web-home":
                    o.SkipWebHome = true;
                    break;
                case "--web-home":
                case "-web-home":
                    o.WebHome = ReadValue(args, ref i, "web-home");
                    break;
                case "--bridge-selftest":
                case "-bridge-selftest":
                    o.BridgeSelfTest = true;
                    break;
                case "--safe-mode":
                case "-safe-mode":
                    o.SafeMode = true;
                    break;
                case "--exit-safe-mode":
                case "-exit-safe-mode":
                    o.ExitSafeMode = true;
                    break;
                case "--add-plugin":
                case "-add-plugin":
                    o.AddPlugin = ReadValue(args, ref i, "add-plugin");
                    break;
                case "--remove-plugin":
                case "-remove-plugin":
                    o.RemovePlugin = ReadValue(args, ref i, "remove-plugin");
                    break;
                case "--plugin-list":
                case "-plugin-list":
                    o.PluginList = true;
                    break;
                case "--enable-plugin":
                case "-enable-plugin":
                    o.EnablePlugin = ReadValue(args, ref i, "enable-plugin");
                    break;
                case "--disable-plugin":
                case "-disable-plugin":
                    o.DisablePlugin = ReadValue(args, ref i, "disable-plugin");
                    break;
                case "--keep-alive":
                case "-keep-alive":
                    o.KeepServerRunning = true;
                    o.KeepServerSpecified = true;
                    break;
                case "--no-keep-alive":
                case "-no-keep-alive":
                case "--stop-server-on-exit":
                    o.KeepServerRunning = false;
                    o.KeepServerSpecified = true;
                    break;
                case "--no-update-check":
                case "-no-update-check":
                    o.NoUpdateCheck = true;
                    o.UpdateCheckSpecified = true;
                    break;
                case "--update-feed":
                case "-update-feed":
                    o.UpdateFeedUrl = ReadFeed(args, ref i);
                    o.UpdateCheckSpecified = true;
                    break;
                case "--stop":
                case "-stop":
                    o.Stop = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use the documented flags in the desktop launcher.");
            }
        }
        Current = o;
        return o;
    }

    /**
     * The options this process was started with. Tools.Discover reads it so
     * --dsh-cli is honoured everywhere the CLI is looked up, without threading
     * the options through every window.
     */
    public static Options? Current { get; private set; }

    /** Applies remembered settings only where the command line stayed silent. */
    internal void ApplySettings(AppSettings settings)
    {
        if (!ProjectSpecified && !string.IsNullOrWhiteSpace(settings.ProjectDir)) ProjectDir = settings.ProjectDir;
        if (!HomeSpecified && !string.IsNullOrWhiteSpace(settings.DshHome)) DshHome = settings.DshHome;
        if (!PortSpecified) Port = settings.Port;
        if (!UpdateSpecified) Update = settings.Update;
        CheckForUpdatesOnLaunch = settings.CheckForUpdates;
        if (!KeepServerSpecified) KeepServerRunning = settings.KeepServerRunning;
        if (!UpdateCheckSpecified && !string.IsNullOrWhiteSpace(settings.UpdateFeedUrl))
        {
            UpdateFeedUrl = settings.UpdateFeedUrl;
        }
    }

    /** Records a project chosen interactively, so later stages treat it as explicit. */
    internal void SetProject(string projectDir)
    {
        ProjectDir = projectDir;
        ProjectSpecified = true;
    }

    /** A free-text value, used by the plugin commands. */
    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"--{name} requires a value");
        var value = args[++index].Trim().Trim('"');
        if (value.Length == 0)
            throw new ArgumentException($"--{name} requires a value");
        return value;
    }

    private static int ReadInt(string[] args, ref int index, string name, int min, int max)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"--{name} requires a value ({min}–{max})");
        var value = args[++index];
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            throw new ArgumentException($"--{name} must be an integer from {min} to {max}; received '{value}'");
        }
        return parsed;
    }

    private static string ReadAddress(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException("--address requires a host name or IP address");
        var value = args[++index].Trim().Trim('[', ']');
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('/'))
            throw new ArgumentException("--address must be a host name or IP address without whitespace or a path");
        if (IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) != UriHostNameType.Unknown)
            return value;
        throw new ArgumentException($"--address is not a valid host name or IP address: '{value}'");
    }

    private static string ReadExistingDirectory(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"--{name} requires a directory path");
        var value = args[++index].Trim().Trim('"');
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"--{name} is not a valid path: '{value}' ({ex.Message})");
        }
        if (!Directory.Exists(full))
            throw new ArgumentException($"--{name} directory does not exist: {full}");
        return full;
    }

    private static string ReadHome(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException("--dsh-home requires a directory path");
        var value = args[++index].Trim().Trim('"');
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"--dsh-home is not a valid path: '{value}' ({ex.Message})");
        }
        if (File.Exists(full))
            throw new ArgumentException($"--dsh-home must be a directory, not a file: {full}");
        return full;
    }

    /**
     * The release feed to read instead of the GitHub API: an https URL, a
     * file:// URL, or the path of a local release document.
     */
    private static string ReadFeed(string[] args, ref int index)    {
        if (index + 1 >= args.Length)
            throw new ArgumentException("--update-feed requires a URL or the path of a release JSON file");
        var value = args[++index].Trim().Trim('"');
        if (value.Length == 0)
            throw new ArgumentException("--update-feed requires a URL or the path of a release JSON file");
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
                throw new ArgumentException($"--update-feed is not a valid URL: '{value}'");
            return value;
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"--update-feed is not a valid path: '{value}' ({ex.Message})");
        }
        if (!File.Exists(full))
            throw new ArgumentException($"--update-feed file does not exist: {full}");
        return full;
    }

    /** --dsh-cli: the JavaScript entry point of an installed harness CLI. */
    private static string ReadDshCli(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException("--dsh-cli requires the path of the harness entry point (bin.js)");
        var value = args[++index].Trim().Trim('"');
        if (value.Length == 0)
            throw new ArgumentException("--dsh-cli requires the path of the harness entry point (bin.js)");

        try
        {
            // The path is accepted even when nothing is there: "you told me where
            // it is and it is missing" is a state worth reporting (and repairing),
            // not an argument error.
            return Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"--dsh-cli is not a valid path: '{value}' ({ex.Message})");
        }
    }
}
