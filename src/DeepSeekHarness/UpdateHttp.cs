using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/** Outcome of one asset download. */
public sealed record DownloadOutcome(bool Ok, long Bytes, string? Error)
{
    public static DownloadOutcome Fail(string error) => new(false, 0, error);
}

/**
 * HTTPS for update checks and downloads.
 *
 * The in-process client is tried first. .NET on Windows uses the machine's TLS
 * stack, and on a locked-down box that stack can refuse to acquire credentials
 * at all ("SEC_E_NO_CREDENTIALS"), which would silently disable every update
 * path. The app already requires Node to run the harness, and Node ships its own
 * TLS implementation, so a failed request is retried through Node before the
 * check is reported as unreachable.
 *
 * file:// URLs and plain paths are read from disk. That keeps the whole update
 * flow testable without a network - and it is how the release fixtures are fed
 * to the check in tests.
 */
public static class UpdateHttp
{
    private const string UserAgent = "DeepSeekHarness-desktop";
    private static readonly HttpClient Http = CreateClient();
    private static readonly Lazy<string?> NodePath = new(() => Tools.Discover().Node);

    /** Transport used by the last successful request: "dotnet", "node" or "file". */
    public static string LastTransport { get; private set; } = "none";

    /** True when the URL points at this machine rather than the network. */
    public static bool IsLocal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        return !url.Contains("://", StringComparison.Ordinal) && File.Exists(url);
    }

    /** Reads a URL (or a local fixture) as text; null when it cannot be read. */
    public static async Task<string?> GetStringAsync(string url, CancellationToken ct = default)
    {
        if (IsLocal(url))
        {
            LastTransport = "file";
            try
            {
                return await File.ReadAllTextAsync(LocalPath(url), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"update feed {url} could not be read: {ex.Message}");
                return null;
            }
        }

        try
        {
            var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            LastTransport = "dotnet";
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn($"in-process request to {url} failed ({ex.Message.Trim()}); retrying through node");
        }

        return await GetStringThroughNodeAsync(url, ct).ConfigureAwait(false);
    }

    /**
     * Downloads one file to destPath, refusing anything larger than maxBytes.
     * The bytes land in "destPath.part" first, so an interrupted download can
     * never be mistaken for a complete one.
     */
    public static async Task<DownloadOutcome> DownloadAsync(
        string url,
        string destPath,
        long maxBytes,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var part = destPath + ".part";

        if (IsLocal(url))
        {
            LastTransport = "file";
            try
            {
                var source = LocalPath(url);
                var length = new FileInfo(source).Length;
                if (length > maxBytes) return DownloadOutcome.Fail($"the file is {length} bytes, over the {maxBytes} cap");
                File.Copy(source, part, overwrite: true);
                File.Move(part, destPath, overwrite: true);
                progress?.Report(length);
                return new DownloadOutcome(true, length, null);
            }
            catch (Exception ex)
            {
                TryDelete(part);
                return DownloadOutcome.Fail(ex.Message);
            }
        }

        try
        {
            var result = await DownloadInProcessAsync(url, part, destPath, maxBytes, progress, ct).ConfigureAwait(false);
            if (result.Ok) return result;
            Log.Warn($"in-process download of {url} failed ({result.Error}); retrying through node");
        }
        catch (Exception ex)
        {
            Log.Warn($"in-process download of {url} failed ({ex.Message.Trim()}); retrying through node");
        }

        return await DownloadThroughNodeAsync(url, part, destPath, maxBytes, progress, ct).ConfigureAwait(false);
    }

    private static async Task<DownloadOutcome> DownloadInProcessAsync(
        string url,
        string part,
        string destPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return DownloadOutcome.Fail($"http {(int)response.StatusCode}");
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared is > 0 && declared > maxBytes)
        {
            return DownloadOutcome.Fail($"the asset is {declared} bytes, over the {maxBytes} cap");
        }

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = new FileStream(
                part, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    target.Close();
                    TryDelete(part);
                    return DownloadOutcome.Fail($"the download passed the {maxBytes} byte cap");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                progress?.Report(total);
            }
            await target.FlushAsync(timeout.Token).ConfigureAwait(false);
            target.Close();
            File.Move(part, destPath, overwrite: true);
            LastTransport = "dotnet";
            return new DownloadOutcome(true, total, null);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return DownloadOutcome.Fail(ex.Message);
        }
    }

    /**
     * Follows a URL only far enough to read where it redirects.
     * github.com/<repo>/releases/latest answers with a redirect to the newest
     * tag, which costs nothing against the anonymous API limit - unlike the API
     * itself, which allows 60 requests per hour per address and is the reason
     * this path exists.
     */
    public static async Task<string?> ResolveRedirectAsync(string url, CancellationToken ct = default)
    {
        if (IsLocal(url)) return null;

        try
        {
            using var noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            noRedirect.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent + "/" + AppInfo.Version);

            using var response = await noRedirect
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var location = response.Headers.Location;
            if (location != null)
            {
                LastTransport = "dotnet";
                var target = location.IsAbsoluteUri
                    ? location.ToString()
                    : "https://github.com" + (location.ToString().StartsWith("/") ? "" : "/") + location;
                return target;
            }

            // Reached the page itself: there was nothing to learn from a redirect.
            if (response.IsSuccessStatusCode)
            {
                LastTransport = "dotnet";
                Log.Info($"the release page answered directly; falling back to the api for {url}");
                return null;
            }
            Log.Warn($"redirect probe of {url} answered http {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warn($"in-process redirect probe of {url} failed ({ex.Message.Trim()}); retrying through node");
        }

        var node = NodePath.Value;
        if (string.IsNullOrEmpty(node))
        {
            Log.Warn("no node executable was found; the https fallback is unavailable");
            return null;
        }

        var script =
            "(async () => { const r = await fetch(" + JsonSerializer.Serialize(url) + ", { redirect: 'manual', headers: { " +
            "'user-agent': " + JsonSerializer.Serialize(UserAgent + "/" + AppInfo.Version) + " } }); " +
            "const l = r.headers.get('location'); " +
            "if (l) { process.stdout.write(l); return; } " +
            "if (r.status >= 200 && r.status < 300) { return; } " +
            "throw new Error('http ' + r.status); })()" +
            ".catch(e => { console.error(String((e && e.message) || e)); process.exitCode = 4; });";

        var outFile = AppPaths.NewLogPath("http", ".out.log");
        var errFile = AppPaths.NewLogPath("http", ".err.log");
        var code = await Task.Run(() => Proc.Run(node, new[] { "-e", script }, outFile, errFile, 60_000, ct), ct)
            .ConfigureAwait(false);
        if (code != 0)
        {
            Log.Warn($"node redirect probe failed (exit {code}); {Tail(errFile)}");
            return null;
        }

        try
        {
            var location = (await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false)).Trim();
            if (location.Length == 0) return null;
            LastTransport = "node";
            return location;
        }
        catch (Exception ex)
        {
            Log.Warn("node redirect probe produced no readable output: " + ex.Message);
            return null;
        }
    }

    private static async Task<string?> GetStringThroughNodeAsync(string url, CancellationToken ct)
    {
        var node = NodePath.Value;
        if (string.IsNullOrEmpty(node))
        {
            Log.Warn("no node executable was found; the https fallback is unavailable");
            return null;
        }

        var script =
            "(async () => { const r = await fetch(" + JsonSerializer.Serialize(url) + ", { headers: { " +
            "'user-agent': " + JsonSerializer.Serialize(UserAgent + "/" + AppInfo.Version) + ", " +
            "'accept': 'application/vnd.github+json, application/json' } }); " +
            "if (!r.ok) { throw new Error('http ' + r.status); } " +
            "process.stdout.write(await r.text()); })()" +
            ".catch(e => { console.error(String((e && e.message) || e)); process.exitCode = 4; });";

        var outFile = AppPaths.NewLogPath("http", ".out.log");
        var errFile = AppPaths.NewLogPath("http", ".err.log");
        var code = await Task.Run(() => Proc.Run(node, new[] { "-e", script }, outFile, errFile, 60_000, ct), ct)
            .ConfigureAwait(false);
        if (code != 0)
        {
            Log.Warn($"node https fallback failed (exit {code}); {Tail(errFile)}");
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false);
            LastTransport = "node";
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("node https fallback produced no readable output: " + ex.Message);
            return null;
        }
    }

    private static async Task<DownloadOutcome> DownloadThroughNodeAsync(
        string url,
        string part,
        string destPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var node = NodePath.Value;
        if (string.IsNullOrEmpty(node))
        {
            TryDelete(part);
            return DownloadOutcome.Fail("no node executable was found; the https fallback is unavailable");
        }

        var script =
            "(async () => { const fs = require('fs'); const max = " + maxBytes + "; " +
            "const r = await fetch(" + JsonSerializer.Serialize(url) + ", { headers: { " +
            "'user-agent': " + JsonSerializer.Serialize(UserAgent + "/" + AppInfo.Version) + " } }); " +
            "if (!r.ok) { throw new Error('http ' + r.status); } " +
            "const declared = Number(r.headers.get('content-length') || 0); " +
            "if (declared > max) { throw new Error('too large: ' + declared); } " +
            "const out = fs.createWriteStream(" + JsonSerializer.Serialize(part) + "); " +
            "let total = 0; " +
            "for await (const chunk of r.body) { total += chunk.length; " +
            "if (total > max) { out.destroy(); throw new Error('too large'); } " +
            "if (!out.write(chunk)) { await new Promise(res => out.once('drain', res)); } } " +
            "await new Promise(res => out.end(res)); console.error('bytes ' + total); })()" +
            ".catch(e => { console.error(String((e && e.message) || e)); process.exitCode = 4; });";

        var outFile = AppPaths.NewLogPath("download", ".out.log");
        var errFile = AppPaths.NewLogPath("download", ".err.log");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        // Node writes the file as it streams, so the part file's size is the
        // progress; poll it while the child runs.
        var watcher = WatchProgress(part, progress, timeout.Token);
        var code = await Task.Run(() => Proc.Run(node, new[] { "-e", script }, outFile, errFile, 900_000, timeout.Token), timeout.Token)
            .ConfigureAwait(false);
        try { timeout.Cancel(); } catch { }
        try { await watcher.ConfigureAwait(false); } catch { }

        if (code != 0)
        {
            TryDelete(part);
            return DownloadOutcome.Fail($"node download failed (exit {code}). {Tail(errFile)}".Trim());
        }

        try
        {
            var length = new FileInfo(part).Length;
            if (length == 0) return DownloadOutcome.Fail("the download produced an empty file");
            if (length > maxBytes) return DownloadOutcome.Fail($"the download passed the {maxBytes} byte cap");
            File.Move(part, destPath, overwrite: true);
            progress?.Report(length);
            LastTransport = "node";
            return new DownloadOutcome(true, length, null);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return DownloadOutcome.Fail(ex.Message);
        }
    }

    private static async Task WatchProgress(string part, IProgress<long>? progress, CancellationToken ct)
    {
        if (progress == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                try
                {
                    var file = new FileInfo(part);
                    if (file.Exists) progress.Report(file.Length);
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            // the download finished; the final size is reported by the caller
        }
    }

    public static string LocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }
        return url;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent + "/" + AppInfo.Version);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json, application/json");
        return client;
    }

    private static string Tail(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path).ReplaceLineEndings(" ").Trim();
            return text.Length > 200 ? text[^200..] : text;
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
