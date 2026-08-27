using System.Text;

namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Pure sanitizer for text materialized from <b>untrusted</b> remote workload output (container names,
/// image references, status lines, systemd descriptions, service ids/labels). Remote output is hostile
/// input, so beyond stripping control characters and clamping length this also removes the
/// terminal-escape and Unicode-spoofing vectors that a plain control-strip misses:
/// <list type="bullet">
///   <item><b>ANSI/VT escape sequences</b> — CSI (<c>ESC [ … final</c>), and OSC/DCS/PM/APC/SOS string
///   sequences (<c>ESC ] / P / ^ / _ / X … BEL|ST</c>), plus other <c>ESC</c>-introduced forms — are
///   removed <i>as a unit</i>, so no residual <c>[31m</c> letters survive once the (control) <c>ESC</c>
///   byte is gone.</item>
///   <item><b>Bidirectional overrides/isolates</b> (U+202A–202E, U+2066–2069, U+200E/200F, U+061C) are
///   removed to defeat Trojan-Source / right-to-left name spoofing. These are Unicode <i>format</i>
///   characters, which <see cref="char.IsControl(char)"/> does not catch.</item>
///   <item><b>C0/C1 control characters</b> (newlines, tabs, NUL, …) collapse to a single space so a
///   field never spans lines and adjacent tokens never fuse.</item>
///   <item><b>Ill-formed UTF-16</b> (an unpaired surrogate that a strict UTF-8 decode upstream could not
///   have produced, but a hostile or corrupt source might smuggle in) is dropped.</item>
///   <item><b>Legitimate Unicode</b> — accents, CJK, emoji (surrogate pairs) — is preserved.</item>
/// </list>
/// The transport layer already enforces strict UTF-8 at decode time (an ill-formed byte stream drops the
/// whole source); this string-level pass is defense in depth. The workload store never contains secrets;
/// this is not a substitute for the infrastructure layer never emitting credentials (§12 spirit).
/// </summary>
public static class WorkloadTextSanitizer
{
    private const char Escape = (char)0x1B; // ESC
    private const char Bell = (char)0x07;   // BEL — terminates OSC/DCS-style string sequences

    /// <summary>
    /// Returns a single-line, escape-free, length-clamped copy of <paramref name="value"/> with control
    /// characters collapsed to a space and surrounding whitespace trimmed. Returns
    /// <see cref="string.Empty"/> for <c>null</c>/empty. Clamped to <see cref="WorkloadLimits.MaxTextLength"/>.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, WorkloadLimits.MaxTextLength));
        for (var i = 0; i < value.Length; i++)
        {
            if (builder.Length >= WorkloadLimits.MaxTextLength)
            {
                break;
            }

            var ch = value[i];

            // Terminal escape sequences: drop the whole sequence (the ESC byte and its payload) so no
            // visible "[0m"-style residue is left behind once the control ESC is removed.
            if (ch == Escape)
            {
                i = SkipEscapeSequence(value, i);
                continue;
            }

            // Bidirectional overrides/isolates are Unicode format chars (not caught by IsControl).
            if (IsBidiFormat(ch))
            {
                continue;
            }

            // C0/C1 control chars (newlines, tabs, NUL, …): collapse to a single space.
            if (char.IsControl(ch))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            // Preserve a surrogate pair (e.g. emoji) as a unit; drop an unpaired surrogate.
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    if (builder.Length + 2 > WorkloadLimits.MaxTextLength)
                    {
                        break;
                    }

                    builder.Append(ch).Append(value[i + 1]);
                    i++;
                }

                continue; // lone high surrogate: drop.
            }

            if (char.IsLowSurrogate(ch))
            {
                continue; // lone low surrogate: drop.
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Sanitizes an optional field, preserving <c>null</c> (unknown) rather than coercing to empty.</summary>
    public static string? SanitizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var sanitized = Sanitize(value);
        return sanitized.Length == 0 ? null : sanitized;
    }

    /// <summary>
    /// Given <paramref name="value"/>[<paramref name="escapeIndex"/>] == ESC, returns the index of the
    /// last character of the escape sequence, so the caller's <c>i++</c> advances past it.
    /// </summary>
    private static int SkipEscapeSequence(string value, int escapeIndex)
    {
        var next = escapeIndex + 1;
        if (next >= value.Length)
        {
            return escapeIndex;
        }

        return value[next] switch
        {
            // CSI: ESC [ (params/intermediates) final byte in @…~ (0x40-0x7E).
            '[' => SkipUntilCsiFinal(value, next + 1),
            // OSC / DCS / PM / APC / SOS string sequences: terminated by BEL or ST (ESC \).
            ']' or 'P' or '^' or '_' or 'X' => SkipUntilStringTerminator(value, next + 1),
            // Any other ESC-introduced two-char form (e.g. ESC c, ESC =): consume the single byte.
            _ => next
        };
    }

    private static int SkipUntilCsiFinal(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is >= '@' and <= '~')
            {
                return i;
            }
        }

        return value.Length - 1;
    }

    private static int SkipUntilStringTerminator(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == Bell)
            {
                return i;
            }

            if (value[i] == Escape && i + 1 < value.Length && value[i + 1] == '\\')
            {
                return i + 1;
            }
        }

        return value.Length - 1;
    }

    private static bool IsBidiFormat(char ch)
    {
        int code = ch;
        return code is 0x200E or 0x200F or 0x061C   // LRM, RLM, ALM
            or (>= 0x202A and <= 0x202E)             // LRE RLE PDF LRO RLO
            or (>= 0x2066 and <= 0x2069);            // LRI RLI FSI PDI
    }
}
