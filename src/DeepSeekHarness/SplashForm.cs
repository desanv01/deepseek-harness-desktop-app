using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DShNative;

/**
 * Startup window shown while the server boots. The embedded loading GIF
 * (transparent whale, assets\source\loading_splash_image.gif) plays above
 * the live status line; animated GIFs animate, single-frame ones are shown
 * as a still. Normal window: movable, minimizable; closing it cancels the
 * launch.
 */
public sealed class SplashForm : Form
{
    private readonly PictureBox _splash;
    private readonly Label _status;
    private readonly Action _onCancel;
    private MemoryStream? _gifStream;
    private bool _finished;

    public SplashForm(string target, Action onCancel)
    {
        _onCancel = onCancel;
        var bg = Theme.Background;

        Text = "DeepSeek Harness";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 330);
        BackColor = bg;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = bg,
            Padding = new Padding(24, 12, 24, 14),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        _splash = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        table.Controls.Add(_splash, 0, 0);

        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f),
            ForeColor = Theme.Foreground,
            Text = "Starting DeepSeek Harness ...",
        };
        table.Controls.Add(_status, 0, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.Hint,
            Text = "You can move or minimize this window. Closing it cancels startup.",
        };
        table.Controls.Add(hint, 0, 2);

        Controls.Add(table);
        Load += (_, _) => LoadSplashImage();
        FormClosing += (_, _) =>
        {
            // Image.FromStream keeps the stream alive; drop both on close
            _splash.Image?.Dispose();
            _gifStream?.Dispose();
            if (!_finished) { try { _onCancel(); } catch { } }
        };
    }

    /** Thread-safe; the boot runs on a background task. */
    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(() => SetStatus(text)));
            else _status.Text = text;
        }
        catch { }
    }

    /** Startup finished (or gave up): closing now must not cancel anything. */
    public void Finish()
    {
        _finished = true;
    }

    private void LoadSplashImage()
    {
        try
        {
            using var stream = typeof(SplashForm).Assembly.GetManifestResourceStream("DShNative.splash.gif");
            if (stream == null) { Log.Warn("splash gif resource missing"); return; }
            _gifStream = new MemoryStream();
            stream.CopyTo(_gifStream);
            _gifStream.Position = 0;
            _splash.Image = Image.FromStream(_gifStream);
        }
        catch (Exception ex)
        {
            Log.Warn("splash gif could not be loaded: " + ex.Message);
        }
    }
}
