using System;
using System.Reflection;

namespace DShNative;

/**
 * Facts about this build: which release it is, and where its releases live.
 * The version is the release date in yyyy.MM.dd form (see the csproj), which
 * is also the release tag - so "is there a newer build?" is a date comparison.
 */
public static class AppInfo
{
    /** GitHub repository that publishes this app's releases. */
    public const string Repo = "desanv01/deepseek-harness-desktop-app";

    /** Installed version, for example "2026.09.15". */
    public static string Version { get; } = ReadVersion();

    /** The matching release tag, for example "v2026.09.15". */
    public static string Tag => "v" + Version;

    /** The newest published release (prereleases excluded by GitHub). */
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /**
     * github.com/<repo>/releases/latest redirects to the newest tag. It costs
     * nothing against the anonymous API limit, which the API URL above does, so
     * detection uses this and only enriches with the API when that is allowed.
     */
    public const string LatestReleasePageUrl = "https://github.com/" + Repo + "/releases/latest";

    /** Any published release: the "all releases" page. */
    public const string ReleasesPageUrl = "https://github.com/" + Repo + "/releases";

    public static string AssetPrefix => AssetName(Version, "");

    /** Asset file name published for one release version. */
    public static string AssetName(string version, string extension)
        => $"DeepSeekHarness-win-x64-{version}{extension}";

    /** Download URL of one asset in one release. */
    public static string AssetUrl(string tag, string assetName)
        => $"https://github.com/{Repo}/releases/download/{tag}/{assetName}";

    /** The human-readable page of one release. */
    public static string TagPageUrl(string tag) => $"https://github.com/{Repo}/releases/tag/{tag}";

    /**
     * The SDK writes 2026.09.15+<commit> into the informational version; the
     * part before '+' is the release version. Falls back to the assembly
     * version so a hand-built binary still reports something usable.
     */
    private static string ReadVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                var value = (plus > 0 ? informational[..plus] : informational).Trim();
                if (value.Length > 0) return value;
            }

            var version = assembly.GetName().Version;
            if (version != null)
            {
                return $"{version.Major:0000}.{version.Minor:00}.{version.Build:00}";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the app version: " + ex.Message);
        }
        return "0.0.0";
    }
}
