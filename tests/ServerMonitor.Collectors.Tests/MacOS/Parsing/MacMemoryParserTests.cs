using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class MacMemoryParserTests
{
    private const long SixteenGiB = 17179869184L;

    private static string VmStat(long active, long wired, long compressor, long pageSize = 16384) =>
        $"Mach Virtual Memory Statistics: (page size of {pageSize} bytes)\n" +
        "Pages free:                               50000.\n" +
        $"Pages active:                            {active}.\n" +
        "Pages inactive:                           80000.\n" +
        $"Pages wired down:                        {wired}.\n" +
        $"Pages occupied by compressor:            {compressor}.\n";

    [Fact]
    public void Used_is_active_plus_wired_plus_compressor_times_page_size()
    {
        // (100000 + 60000 + 20000) * 16384 = 2,949,120,000 bytes
        var result = MacMemoryParser.Parse(VmStat(100000, 60000, 20000), SixteenGiB.ToString());

        Assert.Equal(SixteenGiB, result.TotalBytes);
        Assert.Equal(2_949_120_000L, result.UsedBytes);
        Assert.Equal(2_949_120_000d / SixteenGiB * 100d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Zero_used_pages_is_a_real_zero()
    {
        var result = MacMemoryParser.Parse(VmStat(0, 0, 0), SixteenGiB.ToString());
        Assert.Equal(0L, result.UsedBytes);
        Assert.Equal(0d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Missing_page_size_yields_unknown()
    {
        var noHeader = "Pages active: 100.\nPages wired down: 50.\nPages occupied by compressor: 10.\n";
        var result = MacMemoryParser.Parse(noHeader, SixteenGiB.ToString());
        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }

    [Fact]
    public void Missing_physical_memory_yields_unknown()
    {
        var result = MacMemoryParser.Parse(VmStat(100000, 60000, 20000), null);
        Assert.Equal(MacMemoryResult.Empty, result);
    }

    [Fact]
    public void Missing_used_category_yields_unknown()
    {
        var withoutCompressor =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages active: 100000.\nPages wired down: 60000.\n";
        Assert.Equal(MacMemoryResult.Empty, MacMemoryParser.Parse(withoutCompressor, SixteenGiB.ToString()));
    }

    [Fact]
    public void Used_is_clamped_to_total_when_it_would_exceed_it()
    {
        // 1,000,000 pages * 16384 far exceeds a 1 GiB total.
        var result = MacMemoryParser.Parse(VmStat(1_000_000, 0, 0), "1073741824");
        Assert.Equal(1073741824L, result.TotalBytes);
        Assert.Equal(1073741824L, result.UsedBytes);
        Assert.Equal(100d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Overflowing_arithmetic_yields_unknown()
    {
        var huge = VmStat(9_000_000_000_000_000L, 0, 0);
        Assert.Equal(MacMemoryResult.Empty, MacMemoryParser.Parse(huge, SixteenGiB.ToString()));
    }
}
