using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class VmStatParserTests
{
    private const string AppleSilicon =
        "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
        "Pages free:                               50000.\n" +
        "Pages active:                            100000.\n" +
        "Pages inactive:                           80000.\n" +
        "Pages speculative:                         2000.\n" +
        "Pages throttled:                              0.\n" +
        "Pages wired down:                         60000.\n" +
        "Pages purgeable:                           1000.\n" +
        "Pages stored in compressor:               40000.\n" +
        "Pages occupied by compressor:             20000.\n";

    private const string Intel =
        "Mach Virtual Memory Statistics: (page size of 4096 bytes)\n" +
        "Pages free:                              200000.\n" +
        "Pages active:                            400000.\n" +
        "Pages inactive:                          300000.\n" +
        "Pages speculative:                         5000.\n" +
        "Pages wired down:                        250000.\n" +
        "Pages occupied by compressor:            100000.\n";

    [Fact]
    public void Reads_16384_page_size_on_apple_silicon()
    {
        var result = VmStatParser.Parse(AppleSilicon);
        Assert.Equal(16384L, result.PageSizeBytes);
        Assert.Equal(100000L, result.ActivePages);
        Assert.Equal(60000L, result.WiredDownPages);
        Assert.Equal(20000L, result.CompressorPages);
        Assert.Equal(50000L, result.FreePages);
        Assert.Equal(80000L, result.InactivePages);
        Assert.Equal(2000L, result.SpeculativePages);
        Assert.Equal(1000L, result.PurgeablePages);
    }

    [Fact]
    public void Reads_4096_page_size_on_intel()
    {
        var result = VmStatParser.Parse(Intel);
        Assert.Equal(4096L, result.PageSizeBytes);
        Assert.Equal(400000L, result.ActivePages);
        Assert.Equal(250000L, result.WiredDownPages);
        Assert.Equal(100000L, result.CompressorPages);
    }

    [Fact]
    public void Field_order_does_not_matter()
    {
        var reordered =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages occupied by compressor:             20000.\n" +
            "Pages wired down:                         60000.\n" +
            "Pages active:                            100000.\n";
        var result = VmStatParser.Parse(reordered);
        Assert.Equal(100000L, result.ActivePages);
        Assert.Equal(60000L, result.WiredDownPages);
        Assert.Equal(20000L, result.CompressorPages);
    }

    [Fact]
    public void Missing_fields_are_null_not_zero()
    {
        var partial =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages active:                            100000.\n";
        var result = VmStatParser.Parse(partial);
        Assert.Equal(100000L, result.ActivePages);
        Assert.Null(result.WiredDownPages);
        Assert.Null(result.CompressorPages);
    }

    [Fact]
    public void Missing_header_leaves_page_size_null()
    {
        var noHeader = "Pages active: 100.\nPages wired down: 50.\n";
        var result = VmStatParser.Parse(noHeader);
        Assert.Null(result.PageSizeBytes);
        Assert.Equal(100L, result.ActivePages);
    }

    [Fact]
    public void Negative_counts_are_rejected_per_field()
    {
        var negative =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages active:                            -5.\n";
        Assert.Null(VmStatParser.Parse(negative).ActivePages);
    }

    [Fact]
    public void Very_large_counts_are_preserved()
    {
        var large =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages active:                            9000000000.\n";
        Assert.Equal(9_000_000_000L, VmStatParser.Parse(large).ActivePages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage without any structure")]
    public void Empty_or_garbage_is_empty(string? input)
    {
        var result = VmStatParser.Parse(input);
        Assert.Null(result.PageSizeBytes);
        Assert.Null(result.ActivePages);
    }
}
