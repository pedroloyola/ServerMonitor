using System.Globalization;
using System.Text;

namespace ServerMonitor.WidgetContract;

/// <summary>
/// Sanitizes a user-configured server name for display on an OS widget surface (§10). Applied on write
/// by the app and re-checkable on read by the provider (defense in depth, L-018). It:
/// <list type="bullet">
///   <item>drops C0/C1 control characters, Unicode format characters (bidi overrides/isolates, joiners),
///     surrogate and private-use code points — anti-spoofing and anti-injection;</item>
///   <item>collapses any run of whitespace to a single space and trims;</item>
///   <item>caps the length at <see cref="WidgetSchema.MaxDisplayNameLength"/>.</item>
/// </list>
/// It NEVER falls back to an IP or technical hostname (§10). If the result is empty, it stays empty and
/// the reader supplies a neutral placeholder — the sanitizer never invents a name.
/// </summary>
public static class WidgetDisplayName
{
    /// <summary>Returns a sanitized, length-capped display name. Never returns <c>null</c>.</summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, WidgetSchema.MaxDisplayNameLength));
        var pendingSpace = false;

        foreach (var rune in raw.EnumerateRunes())
        {
            if (IsWhitespace(rune))
            {
                // Collapse; a leading run produces nothing, an interior run one space (deferred so a
                // trailing run never lands in the output).
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (IsDisallowed(rune))
            {
                continue;
            }

            var width = rune.Utf16SequenceLength;
            var extra = pendingSpace ? 1 : 0;
            if (builder.Length + extra + width > WidgetSchema.MaxDisplayNameLength)
            {
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// True if <paramref name="value"/> is already within the sanitized shape (length and character
    /// set). Used by the read-side validator so an externally-altered file cannot smuggle control or
    /// format characters into a name.
    /// </summary>
    public static bool IsSanitized(string? value)
    {
        if (value is null || value.Length > WidgetSchema.MaxDisplayNameLength)
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsDisallowed(rune))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespace(Rune rune)
    {
        switch (rune.Value)
        {
            case ' ':
            case '\t':
            case '\n':
            case '\r':
            case '\f':
            case '\v':
                return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    private static bool IsDisallowed(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control       // C0, C1, DEL
            or UnicodeCategory.Format                    // bidi overrides/isolates, ZWJ/ZWNJ, etc.
            or UnicodeCategory.Surrogate                 // lone surrogates
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;
    }
}
