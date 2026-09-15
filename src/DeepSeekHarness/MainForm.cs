using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DShNative;

/** WebView2 window for the harness UI; icon and title bar follow the theme. */
public sealed class MainForm : Form
{
    // Page background via JS (body first, then <html>).
    private const string BgScript =
        "(function(){try{var s=getComputedStyle(document.body).backgroundColor;" +
        "if(!s||s==='transparent'||s==='rgba(0, 0, 0, 0)'){s=getComputedStyle(document.documentElement).backgroundColor;}" +
        "return s||'';}catch(e){return '';}})()";
    private const string DshMarkerScript =
        "(function(){try{return typeof window.__DSH_BOOT__ !== 'undefined';}catch(e){return false;}})()";

    private readonly string _url;
    private readonly string _userDataDir;
    private readonly string _projectDir;
    private readonly Label _status;
    private WebView2? _web;
    private Color? _pageBg; // measured from the rendered page once loaded
    private TrayIcon? _tray;
    private FormWindowState _stateBeforeHide = FormWindowState.Normal;
    private string _harnessStatus = "Harness: checking ...";
    private HarnessVersions? _harnessVersions;
    private bool _harnessCheckRunning;

    public MainForm(string url, string userDataDir, string projectDir)
    {
        _url = url;
        _userDataDir = userDataDir;
        _projectDir = projectDir;

        var projectName = "";
        try
        {
            projectName = new DirectoryInfo(projectDir).Name;
        }
        catch { }
        Text = projectName.Length > 0
            ? "DeepSeek Harness - " + projectName
            : "DeepSeek Harness";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(860, 560);

        Icon = Theme.AppIcon;
        ApplyPalette(null); // sets BackColor from the OS theme before anything paints

        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12f),
            Text = "Loading the DeepSeek Harness interface ...",
        };
        Controls.Add(_status);
        RecolorStatus();

        Load += OnLoadAsync;
        Shown += OnShown;
        FormClosing += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            _web?.Dispose();
        };
    }

    /**
     * The window exists: publish the tray icon and start the background checks.
     * The tray is not a side effect of minimizing any more - minimizing is an
     * ordinary taskbar minimize - so it is created up front and stays available
     * for the whole window lifetime.
     */
    private void OnShown(object? sender, EventArgs e)
    {
        EnsureTray();
        _ = CheckHarnessAsync();
    }

    /** Brings the window forward when a later launch asks this instance to focus. */
    public void FocusFromSignal()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(RestoreFromTray));
        }
        catch
        {
            // the window is closing; nothing to bring forward
        }
    }

    private void EnsureTray()
    {
        if (_tray != null) return;
        try
        {
            _tray = new TrayIcon(
                Icon ?? SystemIcons.Application,
                _projectDir,
                onOpen: RestoreFromTray,
                onHide: HideToTray,
                onExit: Close,
                onCheckHarness: () => _ = OnCheckHarnessAsync(),
                harnessStatus: _harnessStatus);
            _tray.SetHarnessStatus(_harnessStatus);
        }
        catch (Exception ex)
        {
            // a tray icon can be refused (no shell, policy); the window still works
            Log.Warn("could not create the tray icon: " + ex.Message);
        }
    }

    /**
     * Explicit "Hide to tray": the window leaves the taskbar, the server keeps
     * running. Only this action hides the window - minimizing minimizes.
     */
    public void HideToTray()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (Visible && WindowState != FormWindowState.Minimized) _stateBeforeHide = WindowState;
            Hide();
            ShowInTaskbar = false;
            _tray?.ShowHintOnce();
            Log.Info("window hidden to the tray; the server keeps running");
        }
        catch (Exception ex)
        {
            Log.Warn("could not hide to the tray: " + ex.Message);
        }
    }

    /**
     * Reads the npm dist-tags once per window and records the result for the
     * tray. Read-only: nothing is installed here.
     */
    private async Task CheckHarnessAsync()
    {
        if (_harnessCheckRunning) return;
        _harnessCheckRunning = true;
        try
        {
            var installed = Tools.Discover().DshVersion;
            var info = await HarnessUpdate.QueryAsync(installed).ConfigureAwait(true);
            _harnessVersions = info;

            if (info == null)
            {
                _harnessStatus = installed == null
                    ? "Harness: version unknown"
                    : $"Harness v{installed} (update check unavailable)";
            }
            else if (info.Available == null)
            {
                _harnessStatus = $"Harness v{info.Installed} (up to date)";
            }
            else
            {
                _harnessStatus = $"Harness v{info.Installed} -> {info.Available} available";
                Log.Info($"a newer harness is available: {info.Available} (installed {info.Installed})");
            }

            ApplyHarnessStatus();
        }
        finally
        {
            _harnessCheckRunning = false;
        }
    }

    private void ApplyHarnessStatus()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || _tray == null) return;
                _tray.SetHarnessStatus(_harnessStatus);
                _tray.SetTooltip("DeepSeek Harness - " + _harnessStatus);
            }));
        }
        catch
        {
            // the window is closing; the tray is going away with it
        }
    }

    /** Tray action: report the harness version and offer to install a newer one. */
    private async Task OnCheckHarnessAsync()
    {
        var installed = Tools.Discover().DshVersion;
        var info = await HarnessUpdate.QueryAsync(installed).ConfigureAwait(true);
        _harnessVersions = info;

        if (info == null)
        {
            MessageBox.Show(this,
                "Could not reach the npm registry to check for a harness update.\n\n"
                + $"Installed: {installed ?? "unknown"}",
                "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (info.Available == null)
        {
            MessageBox.Show(this,
                $"DeepSeek Harness {info.Installed} is the newest on the {HarnessUpdate.DefaultChannel} channel.\n\n"
                + $"latest: {info.Latest ?? "n/a"}\nalpha: {info.Alpha ?? "n/a"}",
                "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"DeepSeek Harness {info.Available} is available (installed {info.Installed}).\n\n"
            + "Install it now? The server stops and the window restarts to pick it up.",
            "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var tools = Tools.Discover();
        var version = info.Available;
        Log.Info($"installing harness {version} at the user's request");

        var result = await Task.Run(() => Updater.Install(tools, version)).ConfigureAwait(true);
        if (!result.Usable)
        {
            MessageBox.Show(this,
                "The harness update failed.\n\n" + (result.Error ?? $"exit code {result.ExitCode}")
                + $"\n\nLogs: {AppPaths.LogsDir}",
                "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var restart = MessageBox.Show(this,
            $"DeepSeek Harness {version} is installed.\n\nRestart now to use it?",
            "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (restart == DialogResult.Yes) RestartApp();
    }

    /** Starts a fresh instance; the job object stops this one's server on exit. */
    private void RestartApp()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Warn("could not resolve the executable path; restart manually");
                return;
            }
            Log.Info("restarting to pick up the new harness");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("restart failed: " + ex.Message);
        }
        Close();
    }

    private void RestoreFromTray()
    {
        if (IsDisposed) return;
        try
        {
            ShowInTaskbar = true;
            if (!Visible) Show();
            WindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : _stateBeforeHide;
            Activate();
            BringToFront();
        }
        catch (Exception ex)
        {
            Log.Warn("could not bring the window forward: " + ex.Message);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
    }

    /** Theme change: swap icon; refresh defaults until the page bg is measured. */
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeTheme.WmSettingChange && !IsDisposed)
        {
            Icon = Theme.AppIcon;
            if (_pageBg == null)
            {
                ApplyPalette(null);
                RecolorStatus();
                ApplyWindowChrome();
            }
        }
        base.WndProc(ref m);
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            var web = new WebView2 { Dock = DockStyle.Fill };
            _web = web;
            Controls.Add(web);

            web.CoreWebView2InitializationCompleted += (_, args) =>
            {
                if (args.IsSuccess && web.CoreWebView2 != null)
                {
                    web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    web.DefaultBackgroundColor = CurrentBackground();
                    web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                    web.CoreWebView2.Navigate(_url);
                }
                else
                {
                    SetStatus("WebView2 failed to initialize:\n" + args.InitializationException?.Message);
                }
            };

            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            SetStatus("Could not start the embedded browser:\n" + ex.Message +
                      "\n\nInstall the WebView2 Runtime (Evergreen):\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (!e.IsSuccess || e.HttpStatusCode >= 400)
            {
                SetStatus("The page could not be loaded.\nIs the DeepSeek Harness server running on " + _url + "?");
                return;
            }
            var marker = await _web!.CoreWebView2.ExecuteScriptAsync(DshMarkerScript);
            if (!string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("The endpoint answered HTTP successfully, but it is not a DeepSeek Harness page.\nChoose another --port or stop the service using this endpoint.");
                return;
            }
            SetStatus(string.Empty); // hide overlay
            await MeasurePageBackgroundAsync();
        }
        catch (Exception ex)
        {
            SetStatus("The DeepSeek Harness page could not be verified:\n" + ex.Message);
        }
    }

    /** Re-themes the chrome to match the rendered page background. */
    private async Task MeasurePageBackgroundAsync()
    {
        try
        {
            if (_web?.CoreWebView2 == null) return;
            var json = await _web.CoreWebView2.ExecuteScriptAsync(BgScript);
            var color = ParseCssColor(json);
            if (color == null) return;

            _pageBg = color;
            if (_web != null) _web.DefaultBackgroundColor = color.Value;
            if (!IsDisposed && IsHandleCreated)
            {
                ApplyPalette(color);
                RecolorStatus();
                ApplyWindowChrome();
            }
            Log.Info($"measured page background {color.Value.R},{color.Value.G},{color.Value.B}");
        }
        catch
        {
            // measurement failed - the OS-based theme is a fine fallback
        }
    }

    /** Immersive dark + caption/border tinted to the page background (Win11 22H2+). */
    private void ApplyWindowChrome()
    {
        if (!IsHandleCreated) return;

        var dark = _pageBg != null ? NativeTheme.IsDark(_pageBg.Value) : NativeTheme.IsSystemDark();
        NativeTheme.SetImmersiveDark(Handle, dark);

        if (_pageBg != null && NativeTheme.SupportsCustomCaptionColors)
        {
            var bg = _pageBg.Value;
            var fg = dark ? Theme.DarkText : Theme.LightText;
            NativeTheme.SetCaptionColors(Handle, bg, fg);
            NativeTheme.SetBorderColor(Handle, bg);
        }
    }

    private void ApplyPalette(Color? bg)
    {
        BackColor = bg ?? Theme.Background;
    }

    private void RecolorStatus()
    {
        var dark = _pageBg != null ? NativeTheme.IsDark(_pageBg.Value) : NativeTheme.IsSystemDark();
        _status.BackColor = _pageBg ?? Theme.Background;
        _status.ForeColor = dark ? Theme.DarkText : Theme.LightText;
    }

    private Color CurrentBackground() => _pageBg ?? Theme.Background;

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => SetStatus(text))); } catch { }
            return;
        }
        if (text.Length == 0)
        {
            _status.Visible = false;
        }
        else
        {
            _status.Text = text;
            _status.Visible = true;
            _status.BringToFront();
        }
    }

    private static Color? ParseCssColor(string? jsonResult)
    {
        if (string.IsNullOrEmpty(jsonResult)) return null;
        var match = Regex.Match(jsonResult, @"rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})");
        if (!match.Success) return null;
        return Color.FromArgb(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }
}
