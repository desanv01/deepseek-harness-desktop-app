using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DShNative;

/**
 * Renders release notes into a RichTextBox.
 *
 * GitHub release bodies are markdown, and a raw dump of `## Heading` and
 * `**bold**` is hard to read. This is not a markdown engine: it handles the
 * subset that release notes actually use - headings, bullet and numbered lists,
 * bold and inline code, links, fenced code and quotes - and it repairs one
 * artifact seen in practice: a body that arrived with literal "\n" sequences
 * instead of real line breaks.
 */
public static class MarkdownView
{
    public static void Render(RichTextBox box, string? markdown)
    {
        box.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            box.SelectionFont = Body(box, italic: true);
            box.SelectionColor = Theme.Hint;
            box.AppendText("(no release notes)");
            return;
        }

        var text = Normalize(markdown);
        var dark = NativeTheme.IsSystemDark();

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                AppendLine(box, "", null, null);
                continue;
            }

            // fenced code: keep it monospaced
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                AppendLine(box, trimmed.Length > 3 ? trimmed[3..] : "", Body(box, mono: true), null);
                continue;
            }

            var heading = Heading(trimmed, out var level);
            if (heading != null)
            {
                AppendLine(box, heading, HeadingFont(box, level), Theme.Foreground);
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                AppendInline(box, "| " + trimmed[2..], Body(box, italic: true), Theme.Hint);
                continue;
            }

            var bullet = trimmed.StartsWith("- ", StringComparison.Ordinal)
                      || trimmed.StartsWith("* ", StringComparison.Ordinal)
                      || trimmed.StartsWith("+ ", StringComparison.Ordinal);
            if (bullet)
            {
                AppendInline(box, "  \u2022  " + trimmed[2..], Body(box), Theme.Foreground);
                continue;
            }

            AppendInline(box, line, Body(box), Theme.Foreground);
        }
    }

    /**
     * Repairs bodies that carry escaped newlines, and normalizes the whitespace
     * around them so paragraphs survive.
     */
    public static string Normalize(string text)
    {
        var value = text
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "    ", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        // three or more blank lines collapse into one blank line
        while (value.Contains("\n\n\n", StringComparison.Ordinal))
        {
            value = value.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }
        return value.TrimEnd();
    }

    /** "### Title" -> "Title"; null when the line is not a heading. */
    private static string? Heading(string line, out int level)
    {
        level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level is < 1 or > 6 || level >= line.Length || line[level] != ' ') return null;
        return line[(level + 1)..].Trim();
    }

    /** Writes one line, styling the inline spans it contains. */
    private static void AppendInline(RichTextBox box, string text, Font baseFont, Color color)
    {
        var index = 0;
        while (index < text.Length)
        {
            var bold = text.IndexOf("**", index, StringComparison.Ordinal);
            var code = text.IndexOf('`', index);
            var link = text.IndexOf('[', index);

            var next = new[] { bold, code, link }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (next < 0)
            {
                Append(box, Plain(text[index..]), baseFont, color);
                break;
            }

            if (next > index) Append(box, Plain(text[index..next]), baseFont, color);

            if (next == bold)
            {
                var end = text.IndexOf("**", bold + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Append(box, Plain(text[bold..]), baseFont, color);
                    break;
                }
                Append(box, Plain(text[(bold + 2)..end]), Bold(box, baseFont), color);
                index = end + 2;
            }
            else if (next == code)
            {
                var end = text.IndexOf('`', code + 1);
                if (end < 0)
                {
                    Append(box, Plain(text[code..]), baseFont, color);
                    break;
                }
                Append(box, text[(code + 1)..end], Body(box, mono: true), Theme.Foreground);
                index = end + 1;
            }
            else
            {
                // [label](url) -> "label (url)"; a bare [text] is left alone
                var close = text.IndexOf(']', link + 1);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > 0)
                    {
                        var label = text[(link + 1)..close];
                        var url = text[(close + 2)..end];
                        Append(box, $"{label} ({url})", baseFont, Theme.Hint);
                        index = end + 1;
                        continue;
                    }
                }
                Append(box, "[", baseFont, color);
                index = link + 1;
            }
        }
        box.AppendText(Environment.NewLine);
    }

    private static string Plain(string text) => text
        .Replace("__", "")
        .Replace("~~", "")
        .Replace("\\", "");

    private static void AppendLine(RichTextBox box, string text, Font? font, Color? color)
        => Append(box, text, font ?? Body(box), color ?? Theme.Foreground, newline: true);

    private static void Append(RichTextBox box, string text, Font font, Color color, bool newline = false)
    {
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.AppendText(newline ? text + Environment.NewLine : text);
    }

    private static Font Body(RichTextBox box, bool mono = false, bool italic = false)
    {
        var family = mono ? "Consolas" : "Segoe UI";
        var style = italic ? FontStyle.Italic : FontStyle.Regular;
        return new Font(family, mono ? 9.5f : 9.5f, style);
    }

    private static Font HeadingFont(RichTextBox box, int level)
        => new("Segoe UI", level == 1 ? 12f : level == 2 ? 11f : 10f, FontStyle.Bold);

    private static Font Bold(RichTextBox box, Font baseFont)
        => new(baseFont.FontFamily, baseFont.Size, baseFont.Style | FontStyle.Bold);

    /** A one-line summary for logs and dialogs. */
    public static string FirstLine(string markdown)
    {
        var line = Normalize(markdown).Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Trim().TrimStart('#', ' ').TrimEnd('*', '#').Trim();
    }
}
