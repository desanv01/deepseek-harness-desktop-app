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
    public int ReadyTimeoutSec { get; private set; } = 240;

    /** True when the flag was named on the command line, so settings must not override it. */
    public bool ProjectSpecified { get; private set; }
    public bool HomeSpecified { get; private set; }
    public bool PortSpecified { get; private set; }
    public bool UpdateSpecified { get; private set; }

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
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
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
                case "--stop":
                case "-stop":
                    o.Stop = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use the documented flags in the desktop launcher.");
            }
        }
        return o;
    }

    /** Applies remembered settings only where the command line stayed silent. */
    internal void ApplySettings(AppSettings settings)
    {
        if (!ProjectSpecified && !string.IsNullOrWhiteSpace(settings.ProjectDir)) ProjectDir = settings.ProjectDir;
        if (!HomeSpecified && !string.IsNullOrWhiteSpace(settings.DshHome)) DshHome = settings.DshHome;
        if (!PortSpecified) Port = settings.Port;
        if (!UpdateSpecified) Update = settings.Update;
    }

    /** Records a project chosen interactively, so later stages treat it as explicit. */
    internal void SetProject(string projectDir)
    {
        ProjectDir = projectDir;
        ProjectSpecified = true;
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
}
