using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/** A release that was downloaded and verified, ready to be applied. */
public sealed record StagedUpdate(
    string Tag,
    string Version,
    string FilePath,
    long Bytes,
    string Sha256,
    string SourceUrl,
    bool HasChecksum);

/** Progress of a download, for the Updates window. */
public sealed record UpdateProgress(string Phase, long Received, long Total)
{
    public int Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : 0;
}

/** What the helper needs to know to swap one executable for another. */
internal sealed class PendingUpdate
{
    public int Pid { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string StagedPath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Relaunch { get; set; } = true;
}

/**
 * Downloads, verifies and applies a published desktop-app release.
 *
 * Two rules shape this file:
 *
 *  1. never replace the running executable in place - a helper process waits
 *     for this one to exit, then swaps the files and can put the old build
 *     back if the new one does not start;
 *  2. never install something that did not match its published checksum. The
 *     download is refused on a mismatch, and an install without any checksum
 *     at all has to be requested explicitly by the user.
 */
public static class UpdateInstaller
{
    /** Ceiling for a release asset: a desktop build is tens of MB, not hundreds. */
    public const long MaxAssetBytes = 256L * 1024 * 1024;

    /** Anything smaller than this cannot be a real build. */
    private const long MinAssetBytes = 64 * 1024;

    /** Serializes staging and applying across instances. */
    private static string ApplyMutexName => "Local\\DeepSeekHarness-app-update";

    /** The executable asset a release should be updated from. */
    public static ReleaseAsset? ChooseAsset(AppRelease release)
        => release.Asset(AppInfo.AssetName(release.Version, ".exe"))
           ?? release.Asset(".exe")
           ?? release.Asset(".zip");

    public static string StagePath(AppRelease release, ReleaseAsset asset)
        => Path.Combine(AppPaths.UpdateStageDir(release.Tag), asset.Name);

    /**
     * Downloads the release asset into the staging area and checks it against
     * the release's SHA256SUMS. A previously staged file with the right hash is
     * reused, so a retry after a failed apply costs nothing.
     */
    public static async Task<(StagedUpdate? Staged, string? Error)> StageAsync(
        AppRelease release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var asset = ChooseAsset(release);
        if (asset == null)
        {
            return (null, $"{release.Tag} publishes no downloadable asset");
        }

        var stagePath = StagePath(release, asset);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
        }
        catch (Exception ex)
        {
            return (null, "the staging folder could not be created: " + ex.Message);
        }

        var sums = await ReadChecksumsAsync(release, ct).ConfigureAwait(false);
        var expected = sums?.FirstOrDefault(s => string.Equals(s.Name, asset.Name, StringComparison.OrdinalIgnoreCase)).Hash;

        if (File.Exists(stagePath))
        {
            var existing = await HashFileAsync(stagePath, ct).ConfigureAwait(false);
            if (expected != null && string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"{asset.Name} is already staged and verified");
                progress?.Report(new UpdateProgress("already downloaded", 1, 1));
                return (new StagedUpdate(release.Tag, release.Version, stagePath, new FileInfo(stagePath).Length,
                                         existing, asset.Url, HasChecksum: true), null);
            }
            TryDelete(stagePath);
        }

        progress?.Report(new UpdateProgress("downloading", 0, asset.Size));
        var progressReporter = progress == null
            ? null
            : new Progress<long>(bytes => progress.Report(new UpdateProgress("downloading", bytes, asset.Size)));

        var download = await UpdateHttp
            .DownloadAsync(asset.Url, stagePath, MaxAssetBytes, progressReporter, ct)
            .ConfigureAwait(false);
        if (!download.Ok)
        {
            return (null, "the download failed: " + (download.Error ?? "unknown error"));
        }

        if (download.Bytes < MinAssetBytes)
        {
            TryDelete(stagePath);
            return (null, $"the downloaded file is only {download.Bytes} bytes, which cannot be a build");
        }

        if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !LooksLikeExecutable(stagePath))
        {
            TryDelete(stagePath);
            return (null, "the downloaded file is not a Windows executable");
        }

        progress?.Report(new UpdateProgress("verifying", download.Bytes, download.Bytes));
        var actual = await HashFileAsync(stagePath, ct).ConfigureAwait(false);

        if (expected != null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(stagePath);
            var message = $"{asset.Name} did not match its published SHA-256 "
                          + $"(expected {Short(expected)}, got {Short(actual)}); the download was discarded";
            Log.Error("update verification failed: " + message);
            return (null, message);
        }

        if (expected == null)
        {
            Log.Warn($"{release.Tag} publishes no checksum for {asset.Name}; the file was downloaded but is unverified");
        }
        else
        {
            Log.Info($"{asset.Name} downloaded and verified against SHA256SUMS ({Short(actual)})");
        }

        progress?.Report(new UpdateProgress("ready", download.Bytes, download.Bytes));
        return (new StagedUpdate(release.Tag, release.Version, stagePath, download.Bytes, actual, asset.Url,
                                 HasChecksum: expected != null), null);
    }

    /**
     * Starts the helper that swaps the staged build in once this process exits.
     * The caller closes the app immediately afterwards: closing runs the normal
     * shutdown, and the job object stops the harness server.
     */
    public static string? BeginApply(StagedUpdate staged, bool relaunch = true)
    {
        var mutex = new Mutex(initiallyOwned: false, ApplyMutexName);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) return "another update is already being applied";

            var exe = CurrentExecutable();
            if (exe == null) return "the running executable could not be located";

            var destination = exe;
            if (!CanWriteNextTo(destination))
            {
                return $"the app is running from {Path.GetDirectoryName(destination)}, which is not writable. "
                       + "Download the new build and run it from a folder you own.";
            }

            Directory.CreateDirectory(AppPaths.UpdatesDir);
            var pending = new PendingUpdate
            {
                Pid = Environment.ProcessId,
                StartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                StagedPath = staged.FilePath,
                DestinationPath = destination,
                ExpectedSha256 = staged.Sha256,
                Version = staged.Version,
                Relaunch = relaunch,
            };

            var pendingPath = AppPaths.PendingUpdateFile;
            File.WriteAllText(pendingPath, JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));

            // The helper is this same, known-good build: if the new one fails to
            // start, the helper is still able to put the old one back.
            var helper = Path.Combine(AppPaths.UpdatesDir, $"apply-{staged.Version}-{Environment.ProcessId}.exe");
            File.Copy(exe, helper, overwrite: true);

            Log.Info($"staged {staged.Version} from {staged.FilePath}; handing over to {helper}");
            var started = Proc.Spawn(
                helper,
                new[] { "--apply-update", pendingPath },
                AppPaths.NewLogPath("apply", ".out.log"),
                AppPaths.NewLogPath("apply", ".err.log"));
            if (started == null) return "the update helper could not be started";

            Log.Info($"update helper pid {started.Pid} is waiting for this process to exit");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("could not start the update helper: " + ex);
            return ex.Message;
        }
        finally
        {
            if (owns)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex.Dispose();
        }
    }

    /** The staged update a previous run left behind, if it is still usable. */
    public static StagedUpdate? ReadPending()
    {
        try
        {
            if (!File.Exists(AppPaths.PendingUpdateFile)) return null;
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(AppPaths.PendingUpdateFile));
            if (pending == null || string.IsNullOrWhiteSpace(pending.StagedPath)) return null;
            if (!File.Exists(pending.StagedPath)) return null;
            return new StagedUpdate(
                "v" + pending.Version, pending.Version, pending.StagedPath,
                new FileInfo(pending.StagedPath).Length, pending.ExpectedSha256, pending.StagedPath, true);
        }
        catch (Exception ex)
        {
            Log.Warn("could not read the pending update: " + ex.Message);
            return null;
        }
    }

    public static void ForgetPending()
    {
        try { if (File.Exists(AppPaths.PendingUpdateFile)) File.Delete(AppPaths.PendingUpdateFile); } catch { }
    }

    /** Reads SHA256SUMS from the release; null when it is not published. */
    private static async Task<List<(string Hash, string Name)>?> ReadChecksumsAsync(AppRelease release, CancellationToken ct)
    {
        var asset = release.Asset("SHA256SUMS");
        if (asset == null) return null;

        var text = await UpdateHttp.GetStringAsync(asset.Url, ct).ConfigureAwait(false);
        if (text == null)
        {
            Log.Warn($"SHA256SUMS could not be read for {release.Tag}");
            return null;
        }

        var entries = new List<(string Hash, string Name)>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var separator = line.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0) continue;
            var hash = line[..separator].Trim();
            var name = line[separator..].Trim().TrimStart('*');
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name[(slash + 1)..];
            entries.Add((hash, name));
        }

        if (entries.Count == 0)
        {
            Log.Warn($"SHA256SUMS for {release.Tag} contained no usable entries");
            return null;
        }
        return entries;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /** True when the file starts with the DOS "MZ" signature. */
    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    /** True when a file can be created next to the given path. */
    private static bool CanWriteNextTo(string path)
    {
        try
        {
            var probe = path + ".write-test";
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? CurrentExecutable()
    {
        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception ex)
        {
            Log.Warn("could not resolve the running executable: " + ex.Message);
            return null;
        }
    }

    private static string Short(string hash)
        => hash.Length > 12 ? hash[..12] + "..." : hash;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/**
 * The helper side of an update: --apply-update <pending.json>.
 *
 * It waits for the app that staged the update to exit, verifies the staged
 * file once more, swaps it into place with a backup, and relaunches. A build
 * that dies immediately is rolled back, so a failed update leaves a working
 * app behind.
 */
public static class UpdateApply
{
    public static int Run(string pendingPath)
    {
        var log = new List<string>();
        void Note(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            log.Add(line);
            Log.Info("apply: " + message);
        }

        try
        {
            Note($"helper started for {pendingPath}");
            if (!File.Exists(pendingPath))
            {
                Note("the pending update file is gone; nothing to do");
                WriteLog(log);
                return 2;
            }

            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(pendingPath));
            if (pending == null || string.IsNullOrWhiteSpace(pending.StagedPath) || string.IsNullOrWhiteSpace(pending.DestinationPath))
            {
                Note("the pending update file is not readable");
                WriteLog(log);
                return 2;
            }

            WaitForExit(pending, Note);

            if (!File.Exists(pending.StagedPath))
            {
                Note($"the staged file {pending.StagedPath} is missing; leaving the app untouched");
                WriteLog(log);
                return 1;
            }

            var stagedHash = Hash(pending.StagedPath);
            if (!string.IsNullOrWhiteSpace(pending.ExpectedSha256)
                && !string.Equals(stagedHash, pending.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Note($"the staged file no longer matches its checksum ({stagedHash[..12]}...); refusing to apply");
                WriteLog(log);
                return 1;
            }

            var destination = pending.DestinationPath;
            var backup = destination + ".old";
            TryDelete(backup);

            try
            {
                File.Move(destination, backup);
                File.Move(pending.StagedPath, destination);
                Note($"replaced {destination} with {pending.Version} (previous build kept at {backup})");
            }
            catch (Exception ex)
            {
                Note("the swap failed: " + ex.Message);
                if (!File.Exists(destination) && File.Exists(backup))
                {
                    try { File.Move(backup, destination); Note("restored the previous build"); } catch { }
                }
                WriteLog(log);
                return 1;
            }

            if (!pending.Relaunch)
            {
                Note("the update is in place; not relaunching because the caller asked not to");
                WriteLog(log);
                return 0;
            }

            if (!RelaunchAndVerify(destination, Note))
            {
                Note("the new build did not stay up; rolling back");
                try
                {
                    TryDelete(destination);
                    File.Move(backup, destination);
                    Note("the previous build was restored");
                    Process.Start(new ProcessStartInfo { FileName = destination, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Note("the rollback failed: " + ex.Message + $" - the previous build is at {backup}");
                }
                WriteLog(log);
                return 1;
            }

            Note("the new build started; removing the backup");
            TryDelete(backup);
            try { File.Delete(pendingPath); } catch { }
            WriteLog(log);
            return 0;
        }
        catch (Exception ex)
        {
            Note("unexpected failure: " + ex);
            WriteLog(log);
            return 1;
        }
    }

    /** Waits for the app that staged this update to disappear. */
    private static void WaitForExit(PendingUpdate pending, Action<string> note)
    {
        if (pending.Pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pending.Pid);
            var actual = process.StartTime.ToUniversalTime();
            if ((actual - pending.StartTimeUtc).Duration() > TimeSpan.FromSeconds(2))
            {
                note($"pid {pending.Pid} was reused by another process; not waiting for it");
                return;
            }

            note($"waiting for pid {pending.Pid} to exit");
            if (!process.WaitForExit(90_000))
            {
                note($"pid {pending.Pid} is still running after 90s; applying anyway");
            }
            else
            {
                note($"pid {pending.Pid} exited; applying");
            }
        }
        catch (ArgumentException)
        {
            note("the app had already exited when the helper started");
        }
        catch (Exception ex)
        {
            note("could not wait for the app: " + ex.Message);
        }
    }

    /** Starts the new build and reports whether it survived its first seconds. */
    private static bool RelaunchAndVerify(string exe, Action<string> note)
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
            if (started == null)
            {
                note("the new build could not be started");
                return false;
            }

            if (started.WaitForExit(12_000))
            {
                note($"the new build exited after {started.ExitCode} within 12s");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            note("starting the new build failed: " + ex.Message);
            return false;
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteLog(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UpdatesDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.AppendAllLines(Path.Combine(AppPaths.UpdatesDir, $"apply-{stamp}.log"), lines);
        }
        catch
        {
            // the helper's own log is best effort; the desktop log already has it
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
