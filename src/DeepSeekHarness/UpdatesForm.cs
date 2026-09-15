using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DShNative;

/**
 * "Check for updates": one window with a tab per update track.
 *
 * Desktop app - the newest GitHub release of this app, with download,
 * checksum verification, and a restart that hands over to the update helper.
 * Harness - the installed @deepseek-ai/dsh against the npm dist-tags.
 * About - the versions and paths that make a support report useful.
 *
 * The window never installs anything on its own: every download and every
 * replacement is one deliberate click.
 */
public sealed class UpdatesForm : Form
{
    private UpdatePolicy _policy;
    private readonly Action<AppUpdateInfo>? _onAppUpdate;
    private readonly Action<bool>? _onCheckPolicyChanged;
    private readonly Action? _onRestartApp;
    private readonly Action? _onApplyExit;
    private readonly CancellationTokenSource _closing = new();

    // desktop app tab
    private readonly Label _appInstalled = Value("");
    private readonly Label _appLatest = Value("");
    private readonly Label _appStatus = Status();
    private readonly TextBox _appNotes = Notes();
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Continuous,
        Visible = false,
    };
    private readonly Button _appCheck = Action("Check now");
    private readonly Button _appInstall = Action("Download and install");
    private readonly Button _appRestart = Action("Restart to apply");
    private readonly Button _appPage = Action("Open release page");
    private readonly CheckBox _checkOnLaunch = new()
    {
        Text = "Check for updates when the app starts",
        AutoSize = true,
        ForeColor = Theme.Foreground,
    };

    // harness tab
    private readonly Label _harnessInstalled = Value("");
    private readonly Label _harnessChannels = Value("");
    private readonly Label _harnessStatus = Status();
    private readonly Button _harnessCheck = Action("Check now");
    private readonly Button _harnessInstall = Action("Install update");
    private readonly Button _harnessRestart = Action("Restart to apply");

    private AppUpdateInfo? _appUpdate;
    private AppRelease? _release;
    private StagedUpdate? _staged;
    private HarnessVersions? _harness;
    private bool _busy;

    public UpdatesForm(
        UpdatePolicy policy,
        Action<AppUpdateInfo>? onAppUpdate = null,
        Action<bool>? onCheckPolicyChanged = null,
        Action? onRestartApp = null,
        Action? onApplyExit = null)
    {
        _policy = policy;
        _onAppUpdate = onAppUpdate;
        _onCheckPolicyChanged = onCheckPolicyChanged;
        _onRestartApp = onRestartApp;
        _onApplyExit = onApplyExit;

        Text = "DeepSeek Harness - updates";
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(660, 480);
        ShowInTaskbar = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Foreground;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 8),
        };
        tabs.TabPages.Add(AppTab());
        tabs.TabPages.Add(HarnessTab());
        tabs.TabPages.Add(AboutTab());
        Controls.Add(tabs);

        _appCheck.Click += async (_, _) => await CheckAppAsync(force: true);
        _appInstall.Click += async (_, _) => await InstallAppAsync();
        _appRestart.Click += (_, _) => ApplyStagedUpdate();
        _appPage.Click += (_, _) => OpenUrl((_release ?? _appUpdate?.Latest)?.HtmlUrl ?? AppInfo.ReleasesPageUrl);
        _checkOnLaunch.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _onCheckPolicyChanged?.Invoke(_checkOnLaunch.Checked);
            if (_appUpdate != null) _policy = _policy with { CheckOnLaunch = _checkOnLaunch.Checked };
        };

        _harnessCheck.Click += async (_, _) => await CheckHarnessAsync();
        _harnessInstall.Click += async (_, _) => await InstallHarnessAsync();
        _harnessRestart.Click += (_, _) => RestartForHarness();

        Shown += async (_, _) =>
        {
            _loading = true;
            _checkOnLaunch.Checked = _policy.CheckOnLaunch;
            _loading = false;
            _staged = UpdateInstaller.ReadPending();
            await CheckAppAsync(force: false);
            await CheckHarnessAsync();
        };
        FormClosing += (_, _) =>
        {
            try { _closing.Cancel(); } catch { }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Match the main window's chrome: dark caption when Windows apps are
        // dark, with the caption and border tinted to the app background.
        var dark = NativeTheme.IsSystemDark();
        NativeTheme.SetImmersiveDark(Handle, dark);
        if (NativeTheme.SupportsCustomCaptionColors)
        {
            var background = Theme.Background;
            NativeTheme.SetCaptionColors(Handle, background, dark ? Theme.DarkText : Theme.LightText);
            NativeTheme.SetBorderColor(Handle, background);
        }
    }

    private bool _loading;

    // ---- desktop app tab -----------------------------------------------------

    private TabPage AppTab()
    {
        var page = Page("Desktop app");

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 12),
            BackColor = Theme.Background,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        Add(grid, Caption("Installed version"), 0, 0);
        Add(grid, _appInstalled, 1, 0);
        Add(grid, Caption("Newest published"), 0, 1);
        Add(grid, _appLatest, 1, 1);
        Add(grid, _appStatus, 0, 2);
        grid.SetColumnSpan(_appStatus, 2);

        Add(grid, _appNotes, 0, 3);
        grid.SetColumnSpan(_appNotes, 2);
        Add(grid, _progress, 0, 4);
        grid.SetColumnSpan(_progress, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background,
        };
        buttons.Controls.Add(_appCheck);
        buttons.Controls.Add(_appInstall);
        buttons.Controls.Add(_appRestart);
        buttons.Controls.Add(_appPage);
        Add(grid, buttons, 0, 5);
        grid.SetColumnSpan(buttons, 2);

        Add(grid, _checkOnLaunch, 0, 6);
        grid.SetColumnSpan(_checkOnLaunch, 2);

        page.Controls.Add(grid);
        return page;
    }

    private async Task CheckAppAsync(bool force)
    {
        if (_busy) return;
        SetBusy(true, force ? "Asking GitHub for the newest release ..." : null);
        try
        {
            var info = await AppUpdate.CheckAsync(force, _policy.FeedUrl, _closing.Token).ConfigureAwait(true);
            _appUpdate = info;
            _release = info.Latest;
            _onAppUpdate?.Invoke(info);
            RenderAppState();
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void RenderAppState()
    {
        var info = _appUpdate;
        _appInstalled.Text = AppInfo.Version;
        _appLatest.Text = info?.Latest == null
            ? "unknown"
            : $"{info.Latest.Version}   {info.Latest.Describe()}";
        _appNotes.Text = info?.Latest?.Notes.Trim() is { Length: > 0 } notes ? notes : "(no release notes)";

        if (info == null)
        {
            _appStatus.Text = "No check has run yet.";
            _appInstall.Enabled = false;
            _appRestart.Enabled = _staged != null;
            return;
        }

        if (info.Error != null)
        {
            _appStatus.Text = "The release feed could not be read: " + info.Error;
            _appInstall.Enabled = false;
        }
        else if (info.Latest == null)
        {
            _appStatus.Text = "No published release was found.";
            _appInstall.Enabled = false;
        }
        else if (!info.UpdateAvailable)
        {
            _appStatus.Text = $"You are running the newest published build"
                              + (info.FromCache ? $" (checked {Ago(info.CheckedAtUtc)})" : ".");
            _appInstall.Enabled = false;
        }
        else
        {
            var staged = _staged != null && string.Equals(_staged.Version, info.Latest.Version, StringComparison.OrdinalIgnoreCase);
            _appStatus.Text = staged
                ? $"{info.Latest.Version} is downloaded and verified - restart to apply it."
                : $"{info.Latest.Version} is available."
                  + (info.FromCache ? $" (checked {Ago(info.CheckedAtUtc)})" : "");
            _appInstall.Enabled = !staged;
        }

        _appRestart.Enabled = _staged != null;
        _appPage.Enabled = info.Latest != null;
    }

    private async Task InstallAppAsync()
    {
        var release = _appUpdate?.Latest;
        if (release == null || _busy) return;

        SetBusy(true, $"Downloading {release.Version} ...");
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                _progress.Visible = true;
                _progress.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                if (p.Total > 0) _progress.Value = Math.Clamp(p.Percent, 0, 100);
                _appStatus.Text = p.Total > 0
                    ? $"{p.Phase}: {Mb(p.Received)} of {Mb(p.Total)} MB ({p.Percent}%)"
                    : $"{p.Phase} ...";
            });

            var (staged, error) = await UpdateInstaller
                .StageAsync(release, progress, _closing.Token)
                .ConfigureAwait(true);

            if (staged == null)
            {
                _appStatus.Text = error ?? "The download failed.";
                return;
            }

            if (!staged.HasChecksum)
            {
                var answer = MessageBox.Show(this,
                    $"{release.Tag} publishes no SHA256SUMS, so the download could not be verified.\n\n"
                    + "Installing an unverified download is exactly what an update mechanism should not do. "
                    + "Install it anyway?",
                    "Unverified download", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                {
                    _appStatus.Text = "The download was verified only by size and was left in the staging folder.";
                    return;
                }
            }

            _staged = staged;
            _appStatus.Text = $"{staged.Version} is downloaded and verified - restart to apply it.";
            Log.Info($"staged update {staged.Tag} ({staged.Bytes} bytes, sha256 {staged.Sha256[..12]}...)");
        }
        finally
        {
            _progress.Visible = _progress.Value > 0 && _progress.Value < 100;
            SetBusy(false, null);
            RenderAppState();
        }
    }

    /** Hands the staged build to the helper and exits so it can swap the files. */
    private void ApplyStagedUpdate()
    {
        var staged = _staged;
        if (staged == null) return;

        var answer = MessageBox.Show(this,
            $"Install {staged.Version} and restart DeepSeek Harness?\n\n"
            + "The server stops, the window closes, and the app comes back on the new build. "
            + "If the new build does not start, the previous one is restored automatically.",
            "Restart to apply", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var error = UpdateInstaller.BeginApply(staged, relaunch: true);
        if (error != null)
        {
            MessageBox.Show(this, "The update could not be started:\n\n" + error,
                "Restart to apply", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _appStatus.Text = "Applying the update and restarting ...";
        _onApplyExit?.Invoke();
    }

    // ---- harness tab ---------------------------------------------------------

    private TabPage HarnessTab()
    {
        var page = Page("Harness");

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 12),
            BackColor = Theme.Background,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        Add(grid, Caption("Installed version"), 0, 0);
        Add(grid, _harnessInstalled, 1, 0);
        Add(grid, Caption("npm dist-tags"), 0, 1);
        Add(grid, _harnessChannels, 1, 1);
        Add(grid, _harnessStatus, 0, 2);
        grid.SetColumnSpan(_harnessStatus, 2);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Hint,
            Font = new Font("Segoe UI", 9f),
            Text = "@deepseek-ai/dsh is installed globally with npm. The app follows the 'latest' channel; "
                   + "'alpha' is shown for reference but is not installed automatically.\n\n"
                   + "Installing stops the server and restarts the window, exactly like the tray action.",
        };
        Add(grid, hint, 0, 3);
        grid.SetColumnSpan(hint, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background,
        };
        buttons.Controls.Add(_harnessCheck);
        buttons.Controls.Add(_harnessInstall);
        buttons.Controls.Add(_harnessRestart);
        Add(grid, buttons, 0, 4);
        grid.SetColumnSpan(buttons, 2);

        page.Controls.Add(grid);
        return page;
    }

    private async Task CheckHarnessAsync()
    {
        _harnessStatus.Text = "Reading the npm registry ...";
        _harnessInstalled.Text = Tools.Discover().DshVersion ?? "not installed";
        var info = await HarnessUpdate.QueryAsync(_harnessInstalled.Text, HarnessUpdate.DefaultChannel)
            .ConfigureAwait(true);
        _harness = info;

        if (info == null)
        {
            _harnessChannels.Text = "unknown";
            _harnessStatus.Text = "The npm registry could not be read.";
            _harnessInstall.Enabled = false;
            return;
        }

        _harnessChannels.Text = $"latest {info.Latest ?? "n/a"}    alpha {info.Alpha ?? "n/a"}";
        _harnessStatus.Text = info.InstalledKnown
            ? info.Available == null
                ? $"{info.Installed} is the newest on the {HarnessUpdate.DefaultChannel} channel."
                : $"{info.Available} is available (installed {info.Installed})."
            : "No usable harness CLI was found; installing one also repairs a broken install.";
        _harnessInstall.Enabled = info.Available != null;
        _harnessRestart.Enabled = true;
    }

    private async Task InstallHarnessAsync()
    {
        var info = _harness;
        var version = info?.Available;
        if (version == null || _busy) return;

        var answer = MessageBox.Show(this,
            $"Install DeepSeek Harness {version} (installed {info!.Installed})?\n\n"
            + "The server stops and the window restarts to pick it up.",
            "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        SetBusy(true, $"Installing @deepseek-ai/dsh@{version} ...");
        try
        {
            var tools = Tools.Discover();
            var result = await Task.Run(() => Updater.Install(tools, version), _closing.Token).ConfigureAwait(true);
            if (!result.Usable)
            {
                _harnessStatus.Text = "The update failed: " + (result.Error ?? $"exit code {result.ExitCode}");
                MessageBox.Show(this,
                    "The harness update failed.\n\n" + (result.Error ?? $"exit code {result.ExitCode}")
                    + $"\n\nLogs: {AppPaths.LogsDir}",
                    "Harness update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _harnessStatus.Text = $"{version} is installed - restart to use it.";
            var restart = MessageBox.Show(this, $"DeepSeek Harness {version} is installed.\n\nRestart now?",
                "Harness update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (restart == DialogResult.Yes) RestartForHarness();
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void RestartForHarness() => _onRestartApp?.Invoke();

    // ---- about tab -----------------------------------------------------------

    private TabPage AboutTab()
    {
        var page = Page("About");
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Background,
            ForeColor = Theme.Foreground,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join(Environment.NewLine, new[]
            {
                $"DeepSeek Harness desktop   {AppInfo.Version}",
                $"harness (npm)              {Tools.Discover().DshVersion ?? "not found"}",
                $"webview2                   {WebView2Version()}",
                $"data root                  {AppPaths.Root}{(AppPaths.IsPortable ? $" (from {AppPaths.RootEnvVar})" : "")}",
                $"harness home (DSH_HOME)    {new Options().ResolveHome()}",
                $"logs                       {AppPaths.LogsDir}",
                $"staged updates             {AppPaths.UpdatesDir}",
                $"release repository         https://github.com/{AppInfo.Repo}",
                "",
                "Update checks read public release metadata only. No telemetry is sent, and",
                "nothing is downloaded or replaced without a click.",
            }),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(14, 8, 14, 8),
            BackColor = Theme.Background,
        };
        var logs = Action("Open logs");
        logs.Click += (_, _) => OpenPath(AppPaths.LogsDir);
        var root = Action("Open data root");
        root.Click += (_, _) => OpenPath(AppPaths.Root);
        var repo = Action("Open repository");
        repo.Click += (_, _) => OpenUrl($"https://github.com/{AppInfo.Repo}");
        buttons.Controls.Add(logs);
        buttons.Controls.Add(root);
        buttons.Controls.Add(repo);

        page.Controls.Add(box);
        page.Controls.Add(buttons);
        return page;
    }

    // ---- shared --------------------------------------------------------------

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        _appCheck.Enabled = !busy;
        _appInstall.Enabled = !busy && _appUpdate is { UpdateAvailable: true };
        _harnessCheck.Enabled = !busy;
        _harnessInstall.Enabled = !busy && _harness?.Available != null;
        UseWaitCursor = busy;
        if (busy) _progress.Visible = true;
        if (status != null) _appStatus.Text = status;
    }

    private static TabPage Page(string title)
    {
        return new TabPage(title)
        {
            BackColor = Theme.Background,
            ForeColor = Theme.Foreground,
            Padding = new Padding(0),
        };
    }

    private static void Add(TableLayoutPanel grid, Control control, int column, int row)
    {
        control.Dock = DockStyle.Fill;
        grid.Controls.Add(control, column, row);
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Hint,
        Font = new Font("Segoe UI", 9f),
    };

    private static Label Value(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Foreground,
        Font = new Font("Segoe UI", 10f),
    };

    private static Label Status() => new()
    {
        Text = "",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Foreground,
        Font = new Font("Segoe UI", 9.5f),
    };

    private static TextBox Notes() => new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = NativeTheme.IsSystemDark() ? Color.FromArgb(26, 26, 30) : Color.White,
        ForeColor = Theme.Foreground,
        Font = new Font("Segoe UI", 9f),
    };

    private static Button Action(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(0, 0, 8, 0),
    };

    private static string Mb(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }

    private static string WebView2Version()
    {
        try
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "n/a";
        }
        catch
        {
            return "not installed";
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {url}: {ex.Message}");
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {path}: {ex.Message}");
        }
    }
}
