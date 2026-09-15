using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace DShNative;

/** One file published with a release. */
public sealed record ReleaseAsset(string Name, string Url, long Size);

/** A published desktop-app release. */
public sealed record AppRelease(
    string Tag,
    string Version,
    string Name,
    string Notes,
    string HtmlUrl,
    DateTimeOffset? Published,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /** The published asset whose name ends with suffix, if there is one. */
    public ReleaseAsset? Asset(string suffix)
        => Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public string Describe()
    {
        var date = Published?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"{Tag}{(string.IsNullOrWhiteSpace(Name) ? "" : " - " + Name)}{(date == null ? "" : $" ({date})")}";
    }

    /**
     * This release as a GitHub-shaped feed document. The check cache stores
     * this, so a cached answer parses through exactly the same reader as a live
     * one - including a release that was synthesized from a tag alone.
     */
    public string ToFeedJson() => JsonSerializer.Serialize(new
    {
        tag_name = Tag,
        name = Name,
        body = Notes,
        html_url = HtmlUrl,
        published_at = Published?.ToString("o", CultureInfo.InvariantCulture),
        prerelease = Prerelease,
        assets = Assets.Select(a => new
        {
            name = a.Name,
            browser_download_url = a.Url,
            size = a.Size,
        }).ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true });
}

/** Result of one app update check; never throws, failures land in Error. */
public sealed record AppUpdateInfo(
    string Installed,
    AppRelease? Latest,
    bool UpdateAvailable,
    string? Error,
    DateTimeOffset CheckedAtUtc,
    bool FromCache)
{
    public string StatusLine
    {
        get
        {
            if (Error != null) return $"app: update check unavailable ({Error})";
            if (Latest == null) return $"app v{Installed}: no published release found";
            if (!UpdateAvailable) return $"app v{Installed} (up to date)";
            return $"app v{Installed} -> {Latest.Version} available";
        }
    }
}

/**
 * Asks GitHub for the newest published build of this app and compares it with
 * the running one.
 *
 * The result is cached for a few hours: an unauthenticated GitHub API client is
 * limited to 60 requests per hour per address, and an app that is launched and
 * closed all day must not burn that budget. A launch therefore answers from the
 * cache whenever it can, and the manual "check now" always asks the network.
 *
 * The feed can be redirected (settings "UpdateFeedUrl" or --update-feed) to a
 * file:// path, which is how the update flow is tested without a network.
 */
public static class AppUpdate
{
    /** How long a check result is reused before the network is asked again. */
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /** Ceiling for a release feed document. */
    private const long MaxFeedBytes = 4L * 1024 * 1024;

    /**
     * One check at a time per process. The window and the updates window both
     * ask on startup, and without this they spend two requests and race over the
     * cache file (which showed up as "the process cannot access the file").
     */
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FeedUrl(string? overrideUrl = null)
        => string.IsNullOrWhiteSpace(overrideUrl) ? AppInfo.LatestReleaseApiUrl : overrideUrl.Trim();

    /**
     * Checks for a newer build. force: ignore the cache and ask the feed.
     * Returns a result even when the check fails, so callers can report it.
     */
    public static async Task<AppUpdateInfo> CheckAsync(
        bool force,
        string? feedOverride = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(force, feedOverride, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<AppUpdateInfo> CheckCoreAsync(
        bool force,
        string? feedOverride,
        CancellationToken ct)
    {
        var installed = AppInfo.Version;
        var customFeed = !string.IsNullOrWhiteSpace(feedOverride);
        var feed = FeedUrl(feedOverride);
        var now = DateTimeOffset.UtcNow;

        if (!force)
        {
            var cached = ReadCache();
            if (cached != null
                && string.Equals(cached.Source, feed, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.InstalledVersion, installed, StringComparison.OrdinalIgnoreCase)
                && now - cached.CheckedAtUtc < CacheTtl)
            {
                var cachedRelease = ParseRelease(cached.Payload);
                Log.Info($"app update check answered from the cache ({Describe(cachedRelease, installed)})");
                return Build(installed, cachedRelease, null, cached.CheckedAtUtc, fromCache: true);
            }
        }

        AppRelease? release;
        string? failure;

        if (customFeed)
        {
            release = await ReadFeedAsync(feed, ct).ConfigureAwait(false);
            failure = release == null
                ? UpdateHttp.IsLocal(feed)
                    ? "the local update feed could not be read"
                    : "the release feed could not be read"
                : null;
        }
        else
        {
            var resolved = await ResolveLatestReleaseAsync(ct).ConfigureAwait(false);
            release = resolved.Release;
            failure = resolved.Failure;
        }

        if (release == null)
        {
            Log.Warn($"app update check failed: {failure} ({feed})");
            return Build(installed, null, failure ?? "no published release was found", now, fromCache: false);
        }

        WriteCache(new UpdateCacheEntry
        {
            CheckedAtUtc = now,
            InstalledVersion = installed,
            Source = feed,
            Payload = release.ToFeedJson(),
        });

        var info = Build(installed, release, null, now, fromCache: false);
        Log.Info($"app update check via {UpdateHttp.LastTransport}: {info.StatusLine} ({release.Describe()})");
        return info;
    }

    private static async Task<AppRelease?> ReadFeedAsync(string feed, CancellationToken ct)
    {
        var payload = await UpdateHttp.GetStringAsync(feed, ct).ConfigureAwait(false);
        if (payload == null) return null;
        var release = ParseRelease(payload);
        if (release == null) Log.Warn("app update check failed: the release feed could not be parsed");
        return release;
    }

    /**
     * The newest published release of this app.
     *
     * The redirect endpoint is asked first because it is not rate limited; the
     * API is then consulted to enrich the entry with release notes and the real
     * asset list. When the anonymous API limit is used up - which it is on a
     * busy address, 60 requests per hour - the check still detects the new tag
     * and the assets are derived from the naming convention the release
     * workflow publishes.
     */
    private static async Task<(AppRelease? Release, string? Failure)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        var location = await UpdateHttp.ResolveRedirectAsync(AppInfo.LatestReleasePageUrl, ct).ConfigureAwait(false);
        var tag = TagFromLocation(location);
        if (tag != null) Log.Info($"github.com reports the newest release as {tag}");

        AppRelease? fromApi = null;
        var payload = await UpdateHttp.GetStringAsync(AppInfo.LatestReleaseApiUrl, ct).ConfigureAwait(false);
        if (payload != null) fromApi = ParseRelease(payload);
        else Log.Info("the release api could not be read; using the redirect target and the published asset names");

        if (fromApi != null && tag == null) return (fromApi, null);
        if (fromApi != null && string.Equals(fromApi.Tag, tag, StringComparison.OrdinalIgnoreCase)) return (fromApi, null);
        if (tag != null) return (Synthesize(tag), null);

        return (null, "GitHub could not be reached");
    }

    /** The tag out of a ".../releases/tag/<tag>" redirect target. */
    public static string? TagFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var value = location.Trim().TrimEnd('/');
        if (value.Length == 0) return null;

        var marker = value.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
        var tag = marker >= 0
            ? value[(marker + "/tag/".Length)..]
            : value[(value.LastIndexOf('/') + 1)..];
        if (tag.Length == 0 || tag.Equals("latest", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.UnescapeDataString(tag);
    }

    /**
     * A release entry built from the tag alone, for when the API cannot be
     * asked. Asset names and URLs follow the release workflow's convention, so
     * a 404 is the only difference from a real API answer.
     */
    public static AppRelease Synthesize(string tag)
    {
        var version = TagToVersion(tag);
        var exe = AppInfo.AssetName(version, ".exe");
        var zip = AppInfo.AssetName(version, ".zip");
        var assets = new List<ReleaseAsset>
        {
            new(exe, AppInfo.AssetUrl(tag, exe), 0),
            new(zip, AppInfo.AssetUrl(tag, zip), 0),
            new("SHA256SUMS", AppInfo.AssetUrl(tag, "SHA256SUMS"), 0),
        };
        return new AppRelease(
            tag, version, "", "", AppInfo.TagPageUrl(tag), null, false, assets);
    }

    private static AppUpdateInfo Build(
        string installed, AppRelease? latest, string? error, DateTimeOffset checkedAt, bool fromCache)
    {
        var available = error == null
                        && latest != null
                        && IsNewer(latest.Version, installed);
        return new AppUpdateInfo(installed, latest, available, error, checkedAt, fromCache);
    }

    private static string Describe(AppRelease? release, string installed)
        => release == null ? $"installed {installed}, no release read" : $"installed {installed}, latest {release.Tag}";

    /**
     * Reads one release out of a GitHub release document. Both the single
     * release object (releases/latest) and an array of releases are accepted;
     * drafts are ignored and the newest remaining entry wins.
     */
    public static AppRelease? ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                AppRelease? best = null;
                foreach (var element in root.EnumerateArray())
                {
                    var candidate = Read(element);
                    if (candidate == null || candidate.Prerelease) continue;
                    if (best == null || IsNewer(candidate.Version, best.Version)) best = candidate;
                }
                return best;
            }
            if (root.ValueKind == JsonValueKind.Object) return Read(root);
        }
        catch (Exception ex)
        {
            Log.Warn("could not parse the release feed: " + ex.Message);
        }
        return null;
    }

    private static AppRelease? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;

        var tag = Text(element, "tag_name") ?? Text(element, "name");
        if (string.IsNullOrWhiteSpace(tag)) return null;

        DateTimeOffset? published = null;
        var publishedText = Text(element, "published_at") ?? Text(element, "created_at");
        if (!string.IsNullOrWhiteSpace(publishedText)
            && DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            published = parsed;
        }

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetList.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url") ?? Text(asset, "url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                long size = 0;
                if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number)
                {
                    sizeElement.TryGetInt64(out size);
                }
                assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        var prerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        return new AppRelease(
            tag.Trim(),
            TagToVersion(tag),
            Text(element, "name") ?? "",
            Text(element, "body") ?? "",
            Text(element, "html_url") ?? AppInfo.ReleasesPageUrl,
            published,
            prerelease,
            assets);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /** "v2026.09.20" -> "2026.09.20"; anything else is returned without a leading v. */
    public static string TagToVersion(string tag)
    {
        var value = (tag ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        return value;
    }

    /**
     * True when candidate is strictly newer than current. Release versions are
     * dates (yyyy.MM.dd), so they compare as dates; anything that is not a date
     * - a tag from before this scheme, or a future semver tag - falls back to
     * the prerelease-aware comparison used for the harness.
     */
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        if (TryParseDate(candidate, out var a) && TryParseDate(current, out var b)) return a > b;
        return HarnessUpdate.IsNewer(candidate, current);
    }

    /** Parses a yyyy.MM.dd release version, ignoring a trailing suffix. */
    private static bool TryParseDate(string version, out DateOnly date)
    {
        date = default;
        var value = TagToVersion(version);
        var dash = value.IndexOf('-');
        if (dash > 0) value = value[..dash];
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)) return false;
        if (year < 2000 || year > 9999 || month is < 1 or > 12 || day < 1) return false;
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static UpdateCacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.UpdateCheckCacheFile)) return null;
            var entry = JsonSerializer.Deserialize<UpdateCacheEntry>(File.ReadAllText(AppPaths.UpdateCheckCacheFile));
            if (entry == null || string.IsNullOrWhiteSpace(entry.Payload)) return null;
            return entry;
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the cached update check: " + ex.Message);
            return null;
        }
    }

    private static void WriteCache(UpdateCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            // Unique per write: two checks in one process must not share the file.
            var temp = AppPaths.UpdateCheckCacheFile + "." + Environment.ProcessId + "."
                       + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
            File.Move(temp, AppPaths.UpdateCheckCacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("could not cache the update check: " + ex.Message);
        }
    }

    /** Forgets the cached result, so the next check asks the feed again. */
    public static void ClearCache()
    {
        try { if (File.Exists(AppPaths.UpdateCheckCacheFile)) File.Delete(AppPaths.UpdateCheckCacheFile); }
        catch { }
    }
}

/** One cached feed answer. */
internal sealed class UpdateCacheEntry
{
    public DateTimeOffset CheckedAtUtc { get; set; }
    public string InstalledVersion { get; set; } = "";
    public string Source { get; set; } = "";
    public string Payload { get; set; } = "";
}

/**
 * --check-updates: report the desktop-app and harness update state and exit.
 * Exit codes: 0 everything current, 10 an update is available, 1 nothing could
 * be checked - the same convention as --check-harness and dsh-update-check.ps1.
 */
public static class UpdateCli
{
    public static int Run(Options o)
    {
        Console.WriteLine("== DeepSeek Harness desktop update check ==");

        var app = AppUpdate.CheckAsync(force: true, feedOverride: o.UpdateFeedUrl).GetAwaiter().GetResult();
        Console.WriteLine($"app installed  : {app.Installed}");
        Console.WriteLine($"app published  : {app.Latest?.Version ?? "n/a"}"
                          + (app.Latest == null ? "" : $" ({app.Latest.Tag}{(app.Latest.Published == null ? "" : ", published " + app.Latest.Published.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))})"));
        Console.WriteLine($"app status     : {(app.Error != null ? "check failed - " + app.Error : app.UpdateAvailable ? "update available" : "up to date")}");
        Console.WriteLine($"app feed       : {AppUpdate.FeedUrl(o.UpdateFeedUrl)}"
                          + (app.Error == null ? $" (via {UpdateHttp.LastTransport}{(app.FromCache ? ", cached" : "")})" : ""));
        if (app.UpdateAvailable && app.Latest != null)
        {
            Console.WriteLine($"app notes      : {FirstLine(app.Latest.Notes)}");
            Console.WriteLine($"app page       : {app.Latest.HtmlUrl}");
        }

        var tools = Tools.Discover();
        var harness = HarnessUpdate.QueryAsync(tools.DshVersion).GetAwaiter().GetResult();
        Console.WriteLine($"harness installed : {tools.DshVersion ?? "not installed"}");
        Console.WriteLine($"harness latest    : {harness?.Latest ?? "n/a"}");
        Console.WriteLine($"harness alpha     : {harness?.Alpha ?? "n/a"}");
        Console.WriteLine($"harness status    : {DescribeHarness(harness)}");
        Console.WriteLine("== done ==");

        var anyUpdate = app.UpdateAvailable || harness?.Available != null;
        var anyChecked = app.Error == null || harness != null;
        if (!anyChecked) return 1;
        return anyUpdate ? 10 : 0;
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? "").ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        return string.IsNullOrWhiteSpace(line) ? "(no release notes)" : line.Trim().TrimEnd('*', '#').Trim();
    }

    /** "up to date", "0.1.5-rc.1 available", or an honest "not installed". */
    private static string DescribeHarness(HarnessVersions? harness)
    {
        if (harness == null) return "check failed";
        if (harness.Available == null) return harness.InstalledKnown ? "up to date" : "not installed";
        return harness.InstalledKnown ? harness.Available + " available" : $"not installed; {harness.Available} can be installed";
    }

    /**
     * --install-update: download, verify and stage the newest release without a
     * window, so the whole update path can be exercised by a script. With
     * --apply-now it also hands over to the helper, which swaps the files once
     * this process is gone and relaunches the new build.
     */
    public static int RunInstall(Options o)
    {
        Console.WriteLine("== DeepSeek Harness desktop update install ==");
        var info = AppUpdate.CheckAsync(force: true, feedOverride: o.UpdateFeedUrl).GetAwaiter().GetResult();
        Console.WriteLine($"installed : {info.Installed}");

        if (info.Error != null)
        {
            Console.Error.WriteLine("update check failed: " + info.Error);
            return 1;
        }
        if (info.Latest == null)
        {
            Console.Error.WriteLine("no published release was found");
            return 1;
        }
        if (!info.UpdateAvailable)
        {
            Console.WriteLine($"{info.Latest.Version} is the newest published build; nothing to install");
            return 0;
        }

        Console.WriteLine($"release   : {info.Latest.Tag} - {info.Latest.Describe()}");
        Console.WriteLine($"asset     : {UpdateInstaller.ChooseAsset(info.Latest)?.Name ?? "(none published)"}");
        Console.WriteLine("downloading ...");

        var progress = new Progress<UpdateProgress>(p =>
        {
            if (p.Total > 0)
            {
                Console.WriteLine($"  {p.Phase}: {p.Received}/{p.Total} bytes ({p.Percent}%)");
            }
            else
            {
                Console.WriteLine($"  {p.Phase}: {p.Received} bytes");
            }
        });

        var (staged, error) = UpdateInstaller
            .StageAsync(info.Latest, progress, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (staged == null)
        {
            Console.Error.WriteLine("install failed: " + error);
            return 1;
        }

        Console.WriteLine($"staged    : {staged.FilePath}");
        Console.WriteLine($"bytes     : {staged.Bytes}");
        Console.WriteLine($"sha256    : {staged.Sha256}");
        Console.WriteLine($"verified  : {(staged.HasChecksum ? "yes, against SHA256SUMS" : "no checksum was published")}");

        if (!o.ApplyNow)
        {
            Console.WriteLine("== staged; restart the app (or pass --apply-now) to apply it ==");
            return 0;
        }

        var applyError = UpdateInstaller.BeginApply(staged, relaunch: true);
        if (applyError != null)
        {
            Console.Error.WriteLine("could not start the update helper: " + applyError);
            return 1;
        }

        Console.WriteLine("== the update helper is waiting for this process to exit; the app restarts on the new build ==");
        return 0;
    }
}
