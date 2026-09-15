using System;
using System.Windows.Forms;

namespace DShNative;

/** User errors and questions: a message box when we have a UI, the log regardless. */
public static class Ui
{
    public static void Error(Options o, string message)
    {
        Log.Error(message.ReplaceLineEndings(" | "));
        if (!o.ShowDialogs) return;
        try
        {
            MessageBox.Show(message, "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    /** Yes/No question; false when there is no UI to ask with. */
    public static bool Confirm(string message, string caption)
    {
        try
        {
            return MessageBox.Show(message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                   == DialogResult.Yes;
        }
        catch
        {
            return false;
        }
    }
}
