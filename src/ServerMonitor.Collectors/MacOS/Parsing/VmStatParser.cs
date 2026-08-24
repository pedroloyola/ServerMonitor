using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "vm_stat" output into page counts and the page size. The page size
/// is read from the header ("page size of N bytes") and never assumed to be
/// 4096 — Apple Silicon commonly uses 16384. Counts use NumberStyles.None so a
/// sign or stray text drops only that field. Fields are optional; a missing
/// field is null rather than zero.
/// </summary>
public static partial class VmStatParser
{
    private const int MaxLines = 256;

    public static VmStatResult Parse(string? vmStat)
    {
        if (string.IsNullOrWhiteSpace(vmStat))
        {
            return VmStatResult.Empty;
        }

        long? pageSize = null;
        long? free = null, active = null, inactive = null, speculative = null;
        long? wiredDown = null, compressor = null, purgeable = null;

        var lineCount = 0;
        foreach (var rawLine in vmStat.Split('\n'))
        {
            if (++lineCount > MaxLines)
            {
                break;
            }

            var line = rawLine.TrimEnd('\r');

            var pageSizeMatch = PageSizeRegex().Match(line);
            if (pageSizeMatch.Success &&
                long.TryParse(pageSizeMatch.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) &&
                size > 0)
            {
                pageSize = size;
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var valueToken = line[(separatorIndex + 1)..].Trim().TrimEnd('.').Trim();
            if (!long.TryParse(valueToken, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            switch (key)
            {
                case "Pages free": free = count; break;
                case "Pages active": active = count; break;
                case "Pages inactive": inactive = count; break;
                case "Pages speculative": speculative = count; break;
                case "Pages wired down": wiredDown = count; break;
                case "Pages occupied by compressor": compressor = count; break;
                case "Pages purgeable": purgeable = count; break;
            }
        }

        return new VmStatResult(pageSize, free, active, inactive, speculative, wiredDown, compressor, purgeable);
    }

    [GeneratedRegex(@"page size of (?<size>[0-9]+) bytes", RegexOptions.CultureInvariant)]
    private static partial Regex PageSizeRegex();
}

public readonly record struct VmStatResult(
    long? PageSizeBytes,
    long? FreePages,
    long? ActivePages,
    long? InactivePages,
    long? SpeculativePages,
    long? WiredDownPages,
    long? CompressorPages,
    long? PurgeablePages)
{
    public static readonly VmStatResult Empty = new(null, null, null, null, null, null, null, null);
}
