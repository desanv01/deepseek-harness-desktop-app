using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DShNative;

/**
 * Keeps the harness access token out of anything written to disk.
 *
 * The token is a bearer credential: `dsh web` prints
 *
 *   dsh web: http://127.0.0.1:4567/?token=<secret>
 *
 * and that line is both the readiness signal the app parses AND the first line
 * of `server-*.out.log`. The app reads it into memory on purpose, so the log is
 * the only place it would otherwise leak - and the log is exactly the artefact a
 * user pastes into a bug report.
 *
 * One rule covers both shapes a token reaches a log in: it always follows the
 * `token` label, either as `?token=` inside the ready-line URL or as `token:`
 * in a diagnostic line. Scheme, host, port and path are preserved so the line
 * stays diagnostically useful.
 *
 * Everything here is pure and fail-open: text that matches nothing is returned
 * unchanged, so redaction can never break the readiness parse or lose output.
 * Callers keep the unredacted line for parsing and redact only what they write.
 */
public static class Redact
{
    /** The placeholder every redacted secret is replaced with. */
    public const string Mask = "***";

    /** `token=<secret>` / `token: <secret>`, wherever the label appears. */
    private static readonly Regex LabeledToken = new(
        @"\btoken\b\s*[:=]\s*(?<value>[^\s&""'<>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /** The in-memory value of a token, replaced by the mask. */
    public static string Token(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Mask;

    /**
     * Rewrites every credential this module knows about in one line of output.
     * Returns the input unchanged when there is nothing to redact.
     */
    public static string Line(string? line)
    {
        if (line == null) return string.Empty;
        if (line.Length == 0) return line;
        try
        {
            return LabeledToken.Replace(line, "token=" + Mask);
        }
        catch
        {
            // Redaction must never lose output: if the replacement throws, the
            // caller still gets its line back and decides what to persist.
            return line;
        }
    }

    /** True when redacting this text would actually change it. */
    public static bool WouldRedact(string? line)
        => line != null && !string.Equals(Line(line), line, StringComparison.Ordinal);

    /** Appends a redacted line, for building a sanitized block of output. */
    public static StringBuilder AppendRedacted(StringBuilder target, string? line)
        => target.AppendLine(Line(line));
}
