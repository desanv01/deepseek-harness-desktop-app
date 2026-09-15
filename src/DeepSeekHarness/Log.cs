using System;
using System.IO;

namespace DShNative;

/** Logs to file + console (when attached). Never throws. */
public static class Log
{
    private const long MaxFileBytes = 4L * 1024 * 1024;

    private static readonly object Sync = new();

    /**
     * Resolved per write, not once: the data root can move to %TEMP% when the
     * chosen one turns out not to be writable, and logging must follow it.
     */
    private static string FilePath => Path.Combine(AppPaths.LogsDir, "desktop.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
        try
        {
            lock (Sync)
            {
                AppPaths.Ensure();
                RotateIfNeeded();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never crash the app
        }
        try { Console.WriteLine(line); } catch { }
    }

    /** Keeps desktop.log bounded by moving it to desktop.log.1 once it grows. */
    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxFileBytes) return;
            var previous = FilePath + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(FilePath, previous);
        }
        catch
        {
            // a failed rotation must not stop logging
        }
    }
}
