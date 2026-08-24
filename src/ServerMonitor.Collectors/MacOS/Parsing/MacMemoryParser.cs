namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Combines "vm_stat" (page counts + page size) with "sysctl -n hw.memsize"
/// (total physical memory) into normalized memory bytes.
///
/// Formula (documented in ADR-010):
///   Used      = (active + wired down + occupied by compressor) * pageSize
///   Total     = hw.memsize
///   Available = Total - Used
///
/// Inactive, speculative and purgeable pages are treated as reclaimable
/// (available), matching how macOS reports memory pressure and avoiding an
/// inflated "used" figure. All results are in bytes; null means unknown, never
/// zero. Arithmetic is checked so an absurd count fails loudly rather than
/// wrapping. Requires page size, the three "used" categories and the total;
/// otherwise the whole group is unknown.
/// </summary>
public static class MacMemoryParser
{
    public static MacMemoryResult Parse(string? vmStat, string? hwMemsize)
    {
        var pages = VmStatParser.Parse(vmStat);
        var total = PhysicalMemoryParser.Parse(hwMemsize);

        if (pages.PageSizeBytes is not { } pageSize || pageSize <= 0 ||
            pages.ActivePages is not { } active ||
            pages.WiredDownPages is not { } wired ||
            pages.CompressorPages is not { } compressor ||
            total is not { } totalBytes || totalBytes <= 0)
        {
            return MacMemoryResult.Empty;
        }

        try
        {
            checked
            {
                var usedBytes = (active + wired + compressor) * pageSize;
                if (usedBytes < 0)
                {
                    return MacMemoryResult.Empty;
                }

                // Reserved firmware/GPU pages can nudge the sum above hw.memsize;
                // clamp so a valid reading never exceeds the total.
                if (usedBytes > totalBytes)
                {
                    usedBytes = totalBytes;
                }

                var usagePercent = usedBytes / (double)totalBytes * 100d;
                return new MacMemoryResult(totalBytes, usedBytes, usagePercent);
            }
        }
        catch (OverflowException)
        {
            return MacMemoryResult.Empty;
        }
    }
}

public readonly record struct MacMemoryResult(long? TotalBytes, long? UsedBytes, double? UsagePercent)
{
    public static readonly MacMemoryResult Empty = new(null, null, null);
}
