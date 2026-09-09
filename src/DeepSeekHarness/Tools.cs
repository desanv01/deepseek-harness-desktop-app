using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DShNative;

/** Finds node, npm and the global @deepseek-ai/dsh entry. */
public sealed class Tools
{
    public string? Node { get; init; }
    public string? NpmCli { get; init; }
    public string? DshCli { get; init; }
    public string? DshVersion { get; init; }
    public bool DshMissing => string.IsNullOrEmpty(DshCli);

    public static Tools Discover()
    {
        string? node = null, npmCli = null, dshCli = null, dshVersion = null;

        var dirs = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = d.Trim();
            if (t.Length > 0 && !dirs.Contains(t, StringComparer.OrdinalIgnoreCase)) dirs.Add(t);
        }
        // npm global bin may sit outside PATH; also probe %APPDATA%\npm
        var appDataNpm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        if (!dirs.Contains(appDataNpm, StringComparer.OrdinalIgnoreCase)) dirs.Add(appDataNpm);

        foreach (var dir in dirs)
        {
            if (node == null && File.Exists(Path.Combine(dir, "node.exe")))
                node = Path.Combine(dir, "node.exe");

            if (npmCli == null && ShimsExist(dir, "npm"))
            {
                var candidate = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(candidate)) npmCli = candidate;
            }

            if (dshCli == null && ShimsExist(dir, "dsh"))
            {
                var candidate = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(candidate))
                {
                    dshCli = candidate;
                    var pkg = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
                        dshVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                    }
                    catch { }
                }
            }
        }

        return new Tools { Node = node, NpmCli = npmCli, DshCli = dshCli, DshVersion = dshVersion };
    }

    /**
     * Exercises the CLI's web help path without binding a fixed port. This
     * catches a partially replaced global package that still has a dsh shim and
     * package.json but cannot load its runtime closure.
     */
    public static bool VerifyDsh(Tools t)
    {
        var node = t.Node;
        var dsh = t.DshCli;
        if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(dsh)) return false;

        var outFile = AppPaths.NewLogPath("dsh-verify", ".out.log");
        var errFile = AppPaths.NewLogPath("dsh-verify", ".err.log");
        var code = Proc.Run(
            node,
            new[] { dsh, "web", "--help", "--no-open", "--host", "127.0.0.1", "--port", "0" },
            outFile,
            errFile,
            45_000);
        if (code == 0) return true;

        Log.Warn($"dsh CLI validation failed (exit {code}); verification logs: {errFile}");
        return false;
    }

    private static bool ShimsExist(string dir, string name)
    {
        foreach (var ext in new[] { ".cmd", ".ps1", ".exe", "" })
        {
            if (File.Exists(Path.Combine(dir, name + ext))) return true;
        }
        return false;
    }
}
