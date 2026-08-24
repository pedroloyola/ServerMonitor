using System.Globalization;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "sysctl -n hw.memsize" output: total physical memory in bytes on a
/// single line. NumberStyles.None rejects signs and separators; the value must
/// be strictly positive. Returns null (unknown) for anything else.
/// </summary>
public static class PhysicalMemoryParser
{
    private const int MaxLength = 64;

    public static long? Parse(string? hwMemsize)
    {
        if (string.IsNullOrWhiteSpace(hwMemsize))
        {
            return null;
        }

        var token = hwMemsize.Trim();
        if (token.Length is 0 or > MaxLength)
        {
            return null;
        }

        if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            return null;
        }

        return bytes;
    }
}
