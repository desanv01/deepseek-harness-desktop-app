using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DShNative;

/** What the window is allowed to ask the release feed, and where that feed is. */
public sealed record UpdatePolicy(bool CheckOnLaunch, string? FeedUrl);

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

    /**
     * Injected into every page the window loads: a small pill in the corner of
     * the harness UI that only appears when a newer build exists, and that
     * hands the click back to the app through the WebView2 message channel.
     * It adds one element and never touches the harness's own markup.
     */
    private const string BadgeScript = """
        (function () {
          if (window.__dshDesktopUpdates) return;
          var ID = 'dsh-desktop-update-badge';
          function ensure() {
            var el = document.getElementById(ID);
            if (el) return el;
            el = document.createElement('button');
            el.id = ID;
            el.type = 'button';
            el.style.cssText = [
              'position:fixed', 'right:18px', 'bottom:18px', 'z-index:2147483000',
              'display:none', 'align-items:center', 'gap:8px',
              'padding:8px 14px', 'border-radius:999px',
              'border:1px solid rgba(255,255,255,0.16)',
              'background:#2f6df6', 'color:#ffffff',
              'font:600 13px/1.2 "Segoe UI",system-ui,sans-serif',
              'box-shadow:0 8px 22px rgba(0,0,0,0.38)', 'cursor:pointer'
            ].join(';');
            el.addEventListener('click', function () {
              try {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'dsh-desktop-updates' }));
              } catch (e) { }
            });
            (document.body || document.documentElement).appendChild(el);
            return el;
          }
          window.__dshDesktopUpdates = {
            show: function (text) {
              var el = ensure();
              el.textContent = text || 'Update available';
              el.style.display = 'inline-flex';
            },
            hide: function () {
              var el = document.getElementById(ID);
              if (el) el.style.display = 'none';
            }
          };
        })();
        """;

    private readonly string _url;
    private readonly string _userDataDir;
    private readonly string _projectDir;
    private readonly UpdatePolicy _policy;
    private readonly string _baseTitle;
    private readonly Label _status;
    private WebView2? _web;
    private Color? _pageBg; // measured from the rendered page once loaded
    private TrayIcon? _tray;
    private FormWindowState _stateBeforeHide = FormWindowState.Normal;
    private string _harnessStatus = "Harness: checking ...";
    private HarnessVersions? _harnessVersions;
    private bool _harnessCheckRunning;
    private string _appUpdateStatus = "App: checking ...";
    private AppUpdateInfo? _appUpdate;
    private bool _appUpdateCheckRunning;
    private string? _announcedTag;
    private UpdatesForm? _updates;
    private bool _openUpdatesOnShown;
    private readonly Task<CoreWebView2Environment?>? _warmEnvironment;
    private bool _checksStarted;

    /**
     * Starts the embedded browser's process tree before the window exists, so
     * the seconds WebView2 spends starting up overlap the server boot instead of
     * following it. The result is handed to the form, which navigates as soon as
     * the harness is ready.
     *
     * Must be called from the UI thread: the environment is created in the
     * process's single-threaded apartment, and starting it on a pool thread
     * fails with RPC_E_CHANGED_MODE. The returned task does its work off-thread,
     * so the caller is not blocked.
     */
    public static Task<CoreWebView2Environment?> WarmUp(string userDataDir)
    {
        try
        {
            return CoreWebView2Environment.CreateAsync(null, userDataDir)
                .ContinueWith<CoreWebView2Environment?>(
                    task => task.Status == TaskStatus.RanToCompletion ? task.Result : null,
                    TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // The window reports a missing runtime with its own message.
            Log.Warn("WebView2 warm-up could not start: " + ex.Message);
            return Task.FromResult<CoreWebView2Environment?>(null);
        }
    }

    public MainForm(
        string url,
        string userDataDir,
        string projectDir,
        UpdatePolicy? policy = null,
        Task<CoreWebView2Environment?>? warmEnvironment = null)
    {
        _url = url;
        _userDataDir = userDataDir;
        _projectDir = projectDir;
        _policy = policy ?? new UpdatePolicy(CheckOnLaunch: true, FeedUrl: null);
        _warmEnvironment = warmEnvironment;

        var projectName = "";
        try
        {
            projectName = new DirectoryInfo(projectDir).Name;
        }
        catch { }
        _baseTitle = projectName.Length > 0
            ? "DeepSeek Harness - " + projectName
            : "DeepSeek Harness";
        Text = _baseTitle;
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

        // The update checks are cheap but not free, and they compete with the
        // page for network and CPU, so they wait for the first paint. This is
        // the fallback for a page that never paints.
        _ = ChecksFallbackAsync();

        if (_openUpdatesOnShown)
        {
            _openUpdatesOnShown = false;
            BeginInvoke(new Action(ShowUpdates));
        }
    }

    /** Runs the background checks if navigation never reports a paint. */
    private async Task ChecksFallbackAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        await StartBackgroundChecksAsync().ConfigureAwait(true);
    }

    /** Runs the background checks once, after the page has painted. */
    private async Task StartBackgroundChecksAsync()
    {
        if (_checksStarted) return;
        _checksStarted = true;
        try
        {
            // Let the page finish its own first frames before adding work.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            _ = CheckHarnessAsync();
            _ = CheckAppUpdateAsync(force: false);
        }
        catch (Exception ex)
        {
            Log.Warn("could not start the background update checks: " + ex.Message);
        }
    }

    /** --updates: open the updates window as soon as the window is up. */
    public void OpenUpdatesWhenShown() => _openUpdatesOnShown = true;

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
                onCheckUpdates: () => _ = OnCheckUpdatesAsync(),                harnessStatus: _harnessStatus,
                appStatus: _appUpdateStatus);
            _tray.SetHarnessStatus(_harnessStatus);
            _tray.SetAppStatus(_appUpdateStatus);
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
            else
            {
                _harnessStatus = info.StatusLine(HarnessUpdate.DefaultChannel);
                if (info.Available != null && info.InstalledKnown)
                {
                    Log.Info($"a newer harness is available: {info.Available} (installed {info.Installed})");
                }
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
        var tools = Tools.Discover();
        var info = await HarnessUpdate.QueryAsync(tools.DshVersion, force: true).ConfigureAwait(true);
        _harnessVersions = info;

        if (info == null)
        {
            MessageBox.Show(this,
                "Could not reach the npm registry to check for a harness update.\n\n"
                + $"Installed: {tools.DshVersion ?? "not installed"}",
                "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Nothing usable is installed: offer to install (or repair) rather than
        // talking about versions that are not there.
        if (!info.InstalledKnown)
        {
            var problem = tools.DshBroken
                ? "The installed @deepseek-ai/dsh package is incomplete, so it cannot run.\n\n"
                : "@deepseek-ai/dsh is not installed.\n\n";
            var repair = MessageBox.Show(this,
                problem + $"Install {info.Available ?? "the newest release"} now?",
                "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (repair != DialogResult.Yes) return;

            Log.Info("installing the missing harness CLI at the user's request");
            var fix = await Task.Run(() => Updater.Repair(tools)).ConfigureAwait(true);
            if (!fix.Usable)
            {
                MessageBox.Show(this,
                    "The harness could not be installed.\n\n" + (fix.Error ?? $"exit code {fix.ExitCode}")
                    + $"\n\nLogs: {AppPaths.LogsDir}",
                    "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            await CheckHarnessAsync().ConfigureAwait(true);
            var restartNow = MessageBox.Show(this,
                "DeepSeek Harness is installed.\n\nRestart now to use it?",
                "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (restartNow == DialogResult.Yes) RestartApp();
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

    /**
     * Reads the release feed once per window and records the result for the
     * tray and the window title. Read-only: nothing is downloaded or replaced
     * here - installing is a deliberate action in the Updates window.
     */
    private async Task CheckAppUpdateAsync(bool force)
    {
        if (!force && !_policy.CheckOnLaunch) return;
        if (_appUpdateCheckRunning) return;
        _appUpdateCheckRunning = true;
        try
        {
            var info = await AppUpdate.CheckAsync(force, _policy.FeedUrl).ConfigureAwait(true);
            _appUpdate = info;
            _appUpdateStatus = info.StatusLine;
            ApplyUpdateState();
        }
        catch (Exception ex)
        {
            Log.Warn("app update check failed: " + ex.Message);
        }
        finally
        {
            _appUpdateCheckRunning = false;
        }
    }

    /** Pushes the current update state into the tray, its tooltip, and the title. */
    private void ApplyUpdateState()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                var info = _appUpdate;
                var available = info is { UpdateAvailable: true, Latest: not null };

                _tray?.SetAppStatus(_appUpdateStatus);
                _tray?.SetTooltip(TooltipText());
                Text = available
                    ? $"{_baseTitle} - update {info!.Latest!.Version} available"
                    : _baseTitle;
                UpdateBadge();

                if (available && !string.Equals(_announcedTag, info!.Latest!.Tag, StringComparison.Ordinal))
                {
                    _announcedTag = info.Latest.Tag;
                    Log.Info($"a newer build is available: {info.Latest.Tag} (running {info.Installed})");
                    _tray?.AnnounceUpdate(
                        $"DeepSeek Harness {info.Latest.Version} is available",
                        $"You are running {info.Installed}. Open the tray menu and choose "
                        + "\"Check for updates\" to review and install it.");
                }
            }));
        }
        catch
        {
            // the window is closing; the tray is going away with it
        }
    }

    private string TooltipText()
    {
        var text = "DeepSeek Harness - " + _harnessStatus;
        if (_appUpdate is { UpdateAvailable: true, Latest: not null })
            text += $" - update {_appUpdate.Latest.Version} available";
        return text;
    }

    /** Tray action: open the updates window (app and harness tracks). */
    private Task OnCheckUpdatesAsync()
    {
        ShowUpdates();
        return Task.CompletedTask;
    }

    /** Opens the updates window, or brings the open one forward. */
    public void ShowUpdates()
    {
        if (IsDisposed) return;
        try
        {
            if (_updates == null || _updates.IsDisposed)
            {
                _updates = new UpdatesForm(
                    _policy,
                    onAppUpdate: info =>
                    {
                        _appUpdate = info;
                        _appUpdateStatus = info.StatusLine;
                        ApplyUpdateState();
                    },
                    onCheckPolicyChanged: SaveUpdatePolicy,
                    onRestartApp: RestartApp,
                    onApplyExit: Close);
            }

            if (!_updates.Visible) _updates.Show(this);
            _updates.Activate();
            _updates.BringToFront();
            Log.Info("updates window opened");
        }
        catch (Exception ex)
        {
            Log.Warn("could not open the updates window: " + ex.Message);
        }
    }

    /** Persists the "check for updates on launch" preference. */
    private static void SaveUpdatePolicy(bool enabled)
    {
        try
        {
            var settings = AppSettings.Load();
            settings.CheckForUpdates = enabled;
            settings.Save();
            Log.Info($"update checks on launch {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Log.Warn("could not save the update preference: " + ex.Message);
        }
    }

    /** Starts a fresh instance; the job object stops this one's server on exit. */
    private void RestartApp()    {
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
            // The environment was created while the server booted; when the
            // warm-up is missing or failed, create it here as before.
            var environment = _warmEnvironment != null
                ? await _warmEnvironment
                : await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            if (environment == null)
            {
                environment = await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            }

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
                    web.CoreWebView2.WebMessageReceived += OnWebMessage;
                    _ = web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BadgeScript);
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
            UpdateBadge();
            _ = StartBackgroundChecksAsync();
            await MeasurePageBackgroundAsync();
        }
        catch (Exception ex)
        {
            SetStatus("The DeepSeek Harness page could not be verified:\n" + ex.Message);
        }
    }

    /** The update pill inside the page opens the updates window. */
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.WebMessageAsJson?.Contains("dsh-desktop-updates", StringComparison.Ordinal) == true)
            {
                ShowUpdates();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not read a page message: " + ex.Message);
        }
    }

    /** Shows or hides the in-page update pill to match the current state. */
    private void UpdateBadge()
    {
        try
        {
            var core = _web?.CoreWebView2;
            if (core == null) return;

            var script = _appUpdate is { UpdateAvailable: true, Latest: not null }
                ? $"window.__dshDesktopUpdates&&window.__dshDesktopUpdates.show({JsonSerializer.Serialize("Update " + _appUpdate.Latest.Version + " available")})"
                : "window.__dshDesktopUpdates&&window.__dshDesktopUpdates.hide()";
            _ = core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("could not update the in-page badge: " + ex.Message);
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
