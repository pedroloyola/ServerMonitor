using System.Globalization;

namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Extracts MemTotal/MemAvailable from "cat /proc/meminfo" output. Both
/// fields are required, must carry the kernel's "kB" unit, and total must
/// be strictly positive; anything else yields Empty rather than a
/// half-derived value. The kB-to-byte conversion is checked so a corrupt
/// or absurd value cannot silently wrap instead of failing loudly.
/// </summary>
public static class MemInfoParser
{
    private const string ExpectedUnit = "kB";

    public static MemInfoParseResult Parse(string? memInfo)
    {
        if (string.IsNullOrWhiteSpace(memInfo))
        {
            return MemInfoParseResult.Empty;
        }

        long? totalKb = null;
        long? availableKb = null;

        foreach (var rawLine in memInfo.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key is not ("MemTotal" or "MemAvailable"))
            {
                continue;
            }

            var tokens = line[(separatorIndex + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length != 2 ||
                !string.Equals(tokens[1], ExpectedUnit, StringComparison.Ordinal) ||
                !long.TryParse(
                    tokens[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var kilobytes))
            {
                continue;
            }

            if (key == "MemTotal")
            {
                totalKb = kilobytes;
            }
            else
            {
                availableKb = kilobytes;
            }
        }

        if (totalKb is not { } total || total <= 0 ||
            availableKb is not { } available ||
            available > total)
        {
            return MemInfoParseResult.Empty;
        }

        try
        {
            checked
            {
                var totalBytes = total * 1024;
                var usedBytes = (total - available) * 1024;
                var usagePercent = usedBytes / (double)totalBytes * 100d;
                return new MemInfoParseResult(totalBytes, usedBytes, usagePercent);
            }
        }
        catch (OverflowException)
        {
            return MemInfoParseResult.Empty;
        }
    }
}

public readonly record struct MemInfoParseResult(long? TotalBytes, long? UsedBytes, double? UsagePercent)
{
    public static readonly MemInfoParseResult Empty = new(null, null, null);
}
