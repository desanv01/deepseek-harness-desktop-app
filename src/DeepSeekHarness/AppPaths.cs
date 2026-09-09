using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DShNative;

/** App-data layout under %LOCALAPPDATA%\DeepSeekHarness. */
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness");

    public static string LogsDir => Path.Combine(Root, "logs");
    public static string WebView2Data => Path.Combine(Root, "webview2");

    /** The dedicated harness home this app owns unless --dsh-home overrides it. */
    public static string DefaultHome => Path.Combine(Root, "home");

    /** Stable, non-secret identity for one DSH_HOME directory. */
    public static string HomeKey(string home)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeHome(home)))).ToLowerInvariant();
    }

    public static string HomeMutexName(string home)
        => "Local\\DeepSeekHarness-home-" + HomeKey(home);

    /** One server per home: the owner publishes its endpoint here for attachers. */
    public static string HomeLeaseFile(string home)
        => Path.Combine(Root, "instance-" + HomeKey(home) + ".json");

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

    /** Stable, non-secret identity for one bind endpoint. */
    public static string EndpointKey(string address, int port)
    {
        var normalized = address.Trim().ToLowerInvariant() + ":" + port;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string EndpointMutexName(string address, int port)
        => "Local\\DeepSeekHarness-endpoint-" + EndpointKey(address, port);

    public static string UpdateMutexName => "Local\\DeepSeekHarness-npm-update";

    public static string EndpointStateFile(string address, int port)
        => Path.Combine(Root, "server-" + EndpointKey(address, port) + ".json");

    /** A unique log path prevents concurrent launchers from sharing a file handle. */
    public static string NewLogPath(string prefix, string suffix = ".log")
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(LogsDir, $"{prefix}-{stamp}-{Environment.ProcessId}-{Guid.NewGuid():N}{suffix}");
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }
}
