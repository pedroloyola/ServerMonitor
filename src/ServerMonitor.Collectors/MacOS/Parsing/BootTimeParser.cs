using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "sysctl -n kern.boottime" output, e.g.
/// <c>{ sec = 1720000000, usec = 0 } Tue Jul  3 12:00:00 2024</c>. Only the
/// numeric <c>sec</c> field (Unix epoch seconds, UTC) is used; the trailing
/// localized date text is ignored. Returns the boot instant as a
/// <see cref="DateTimeOffset"/>, or null when the field is missing or out of
/// range. Uptime itself is derived by the collector against its TimeProvider.
/// </summary>
public static partial class BootTimeParser
{
    public static DateTimeOffset? Parse(string? bootTimeOutput)
    {
        if (string.IsNullOrWhiteSpace(bootTimeOutput))
        {
            return null;
        }

        var match = SecRegex().Match(bootTimeOutput);
        if (!match.Success ||
            !long.TryParse(match.Groups["sec"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // \b prevents matching the "sec" inside "usec" when field order varies.
    [GeneratedRegex(@"\bsec\s*=\s*(?<sec>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecRegex();
}
