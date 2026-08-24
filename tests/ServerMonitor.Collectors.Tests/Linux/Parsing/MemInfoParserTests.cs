using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class MemInfoParserTests
{
    [Fact]
    public void Parse_ValidMemInfo_ComputesUsedAndPercent()
    {
        var memInfo = """
            MemTotal:       16000000 kB
            MemFree:         2000000 kB
            MemAvailable:   10000000 kB
            Buffers:          100000 kB
            """;

        var result = MemInfoParser.Parse(memInfo);

        Assert.Equal(16000000L * 1024, result.TotalBytes);
        Assert.Equal((16000000L - 10000000L) * 1024, result.UsedBytes);
        Assert.NotNull(result.UsagePercent);
        Assert.Equal(37.5, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Parse_FullyAvailable_ReturnsZeroUsedNotUnknown()
    {
        var memInfo = "MemTotal: 1000 kB\nMemAvailable: 1000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Equal(1000L * 1024, result.TotalBytes);
        Assert.Equal(0L, result.UsedBytes);
        Assert.Equal(0d, result.UsagePercent!.Value, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmptyInput_ReturnsEmpty(string? memInfo)
    {
        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }

    [Fact]
    public void Parse_MissingMemAvailable_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 16000000 kB\nMemFree: 2000000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }

    [Fact]
    public void Parse_MissingMemTotal_ReturnsEmpty()
    {
        var memInfo = "MemAvailable: 2000000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_AvailableExceedsTotal_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 1000 kB\nMemAvailable: 5000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
    }

    [Fact]
    public void Parse_MalformedNumber_ReturnsEmpty()
    {
        var memInfo = "MemTotal: notanumber kB\nMemAvailable: 2000000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_IgnoresUnrelatedFields()
    {
        var memInfo = """
            MemTotal:       8000000 kB
            SwapTotal:      2000000 kB
            SwapFree:       2000000 kB
            MemAvailable:   4000000 kB
            """;

        var result = MemInfoParser.Parse(memInfo);

        Assert.Equal(8000000L * 1024, result.TotalBytes);
        Assert.Equal(4000000L * 1024, result.UsedBytes);
    }

    [Fact]
    public void Parse_MissingUnit_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 16000000\nMemAvailable: 10000000\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_WrongUnit_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 16000 MB\nMemAvailable: 10000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_UnitCaseMismatch_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 16000000 KB\nMemAvailable: 10000000 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_TotalIsZero_ReturnsEmpty()
    {
        var memInfo = "MemTotal: 0 kB\nMemAvailable: 0 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }

    [Fact]
    public void Parse_TotalTooLargeForByteConversion_ReturnsEmptyInsteadOfOverflowing()
    {
        var memInfo = $"MemTotal: {long.MaxValue} kB\nMemAvailable: 0 kB\n";

        var result = MemInfoParser.Parse(memInfo);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }
}
