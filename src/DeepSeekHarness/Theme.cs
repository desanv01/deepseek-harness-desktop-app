using System;
using System.Drawing;

namespace DShNative;

/**
 * Shared palette and embedded artwork for the app's windows. Both the splash
 * and the main window read the same colours, so a theme change is one edit.
 */
public static class Theme
{
    public static readonly Color DarkBg = Color.FromArgb(16, 16, 18);
    public static readonly Color DarkText = Color.FromArgb(235, 238, 244);
    public static readonly Color DarkHint = Color.FromArgb(148, 152, 162);
    public static readonly Color LightBg = Color.FromArgb(250, 250, 250);
    public static readonly Color LightText = Color.FromArgb(31, 35, 40);
    public static readonly Color LightHint = Color.FromArgb(110, 115, 125);

    private static readonly Icon Light = LoadIcon("icon-light.ico");
    private static readonly Icon Dark = LoadIcon("icon-dark.ico");

    /** Window icon for the current Windows app theme. */
    public static Icon AppIcon => NativeTheme.IsSystemDark() ? Dark : Light;

    /** Window background for the current Windows app theme. */
    public static Color Background => NativeTheme.IsSystemDark() ? DarkBg : LightBg;

    /** Body text colour for the current Windows app theme. */
    public static Color Foreground => NativeTheme.IsSystemDark() ? DarkText : LightText;

    /** Secondary text colour for the current Windows app theme. */
    public static Color Hint => NativeTheme.IsSystemDark() ? DarkHint : LightHint;

    /** Loads one embedded .ico, falling back to the stock application icon. */
    public static Icon LoadIcon(string resourceName)
    {
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("DShNative." + resourceName);
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // a missing or malformed resource falls back to the stock icon
        }
        return SystemIcons.Application;
    }
}
