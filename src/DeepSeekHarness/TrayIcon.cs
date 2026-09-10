using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DShNative;

/** Tray icon and its menu while the window is minimized or hidden. */
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _harnessItem;
    private bool _hintShown;

    public TrayIcon(Icon icon, string projectDir, Action onOpen, Action onExit, Action onCheckHarness, string harnessStatus)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open window", null, (_, _) => onOpen());
        menu.Items.Add("Open project folder", null, (_, _) => Open(projectDir));
        menu.Items.Add("Open logs", null, (_, _) => Open(AppPaths.LogsDir));
        menu.Items.Add(new ToolStripSeparator());

        // Shows the installed harness version; refreshed by the background check.
        _harnessItem = new ToolStripMenuItem(harnessStatus) { Enabled = false };
        menu.Items.Add(_harnessItem);
        menu.Items.Add("Check for harness update ...", null, (_, _) => onCheckHarness());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Stop server and exit", null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "DeepSeek Harness",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    /** Updates the harness version line. Call on the UI thread. */
    public void SetHarnessStatus(string text)
    {
        try { _harnessItem.Text = text; } catch { }
    }

    /** Tooltip under the tray icon; also shows the harness version. */
    public void SetTooltip(string text)
    {
        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { }
    }

    /** Explains the tray once per process, the first time the window hides. */
    public void ShowHintOnce()
    {
        if (_hintShown) return;
        _hintShown = true;
        try
        {
            _icon.ShowBalloonTip(4000, "DeepSeek Harness",
                "Still running in the tray. The server keeps working until you close it.", ToolTipIcon.Info);
        }
        catch
        {
            // balloon tips can be disabled by policy; the tray menu still works
        }
    }

    private static void Open(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("could not open " + path + ": " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // disposal must not block window teardown
        }
    }
}
