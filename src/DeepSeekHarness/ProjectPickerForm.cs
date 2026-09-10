using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DShNative;

/**
 * Small chooser shown when no project was supplied and none is remembered.
 * Lists the recent projects and offers a folder browser for a new one.
 */
public sealed class ProjectPickerForm : Form
{
    private readonly ListBox _list;
    private string? _chosen;

    private ProjectPickerForm(string title, AppSettings settings)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(480, 320);
        ShowInTaskbar = true;

        var dark = NativeTheme.IsSystemDark();
        BackColor = dark ? Color.FromArgb(16, 16, 18) : Color.FromArgb(250, 250, 250);
        ForeColor = dark ? Color.FromArgb(235, 238, 244) : Color.FromArgb(31, 35, 40);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(14, 12, 14, 6),
            Text = "Choose the project folder to open in DeepSeek Harness",
            Font = new Font("Segoe UI", 10.5f),
        };

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.75f),
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = dark ? Color.FromArgb(26, 26, 30) : Color.White,
            ForeColor = ForeColor,
        };
        foreach (var path in settings.RecentProjects)
        {
            if (Directory.Exists(path)) _list.Items.Add(path);
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => Accept();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 10, 10, 10),
        };
        var cancel = new Button { Text = "Cancel", Width = 96, DialogResult = DialogResult.Cancel };
        var open = new Button { Text = "Open", Width = 96 };
        var browse = new Button { Text = "Browse ...", Width = 104 };
        open.Click += (_, _) => Accept();
        browse.Click += (_, _) => Browse();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(open);
        buttons.Controls.Add(browse);

        Controls.Add(_list);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = open;
        CancelButton = cancel;
    }

    /** The folder chosen by the user, or null when the dialog was cancelled. */
    public string? Chosen => _chosen;

    public static string? Pick(IWin32Window? owner, AppSettings settings)
    {
        using var form = new ProjectPickerForm("DeepSeek Harness - choose a project", settings);
        var result = owner == null ? form.ShowDialog() : form.ShowDialog(owner);
        return result == DialogResult.OK ? form.Chosen : null;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the project folder DeepSeek Harness should open",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (_list.SelectedItem is string selected && Directory.Exists(selected))
            dialog.SelectedPath = selected;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _chosen = dialog.SelectedPath;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Accept()
    {
        if (_list.SelectedItem is not string selected || !Directory.Exists(selected))
        {
            Browse();
            return;
        }
        _chosen = selected;
        DialogResult = DialogResult.OK;
        Close();
    }
}
