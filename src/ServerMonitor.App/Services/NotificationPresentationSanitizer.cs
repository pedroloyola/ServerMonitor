using System.Globalization;
using System.Text;

namespace ServerMonitor.App.Services;

internal static class NotificationPresentationSanitizer
{
    internal const int MaximumTextElements = 80;
    internal const int MaximumInputCodeUnits = 1024;

    public static string SanitizeServerName(string? value, string fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        string normalized;
        try
        {
            var bounded = BoundInput(value ?? string.Empty);
            normalized = bounded.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // An ill-formed UTF-16 display name is untrusted presentation data, not a reason
            // to fail the alert worker.
            return fallback;
        }
        var result = new StringBuilder(Math.Min(normalized.Length, MaximumTextElements));
        var elements = StringInfo.GetTextElementEnumerator(normalized);
        var count = 0;
        var whitespacePending = false;

        while (elements.MoveNext() && count < MaximumTextElements)
        {
            var element = elements.GetTextElement();
            if (ContainsBidirectionalControl(element))
            {
                continue;
            }

            if (ContainsControlCharacter(element))
            {
                whitespacePending = result.Length > 0;
                continue;
            }

            if (element.All(char.IsWhiteSpace))
            {
                whitespacePending = result.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                result.Append(' ');
                whitespacePending = false;
            }

            result.Append(element);
            count++;
        }

        return result.Length == 0 ? fallback : result.ToString();
    }

    private static string BoundInput(string value)
    {
        if (value.Length <= MaximumInputCodeUnits)
        {
            return value;
        }

        var length = MaximumInputCodeUnits;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
        {
            length++;
        }

        return value[..length];
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBidirectionalControl(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsBidirectionalControl(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBidirectionalControl(int value) =>
        value is 0x061C or 0x200E or 0x200F or
            >= 0x202A and <= 0x202E or
            >= 0x2066 and <= 0x206F;
}
