using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class DiskUsageParserTests
{
    [Fact]
    public void Parse_StandardSingleLineOutput_ParsesTotalsAndUsesDfCapacityVerbatim()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1    21474836480 10737418240 10200547328      52% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Equal(21474836480L, result.TotalBytes);
        Assert.Equal(10737418240L, result.UsedBytes);
        Assert.Equal(52d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Parse_CapacityDiffersFromNaiveRatio_TrustsDfOverRecomputation()
    {
        // used/total here is exactly 50%, but df reports 55% (e.g. reserved
        // blocks on the filesystem). The parser must not recompute.
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1       1000000    500000       500000      55% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Equal(55d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Parse_WrappedFilesystemNameLine_StillParsesTotals()
    {
        // df wraps onto its own line when the filesystem name is long.
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/mapper/very-long-volume-group-name-root\n" +
                 "             21474836480 10737418240 10200547328      52% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Equal(21474836480L, result.TotalBytes);
        Assert.Equal(10737418240L, result.UsedBytes);
        Assert.Equal(52d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Parse_FullyUsedDisk_ReturnsHundredPercent()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1        1000000   1000000           0     100% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Equal(1000000L, result.TotalBytes);
        Assert.Equal(1000000L, result.UsedBytes);
        Assert.Equal(100d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Parse_EmptyDisk_ReturnsZeroUsedNotUnknown()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1        1000000         0     1000000       0% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Equal(1000000L, result.TotalBytes);
        Assert.Equal(0L, result.UsedBytes);
        Assert.Equal(0d, result.UsagePercent!.Value, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmptyInput_ReturnsEmpty(string? df)
    {
        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.UsagePercent);
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_UsedExceedsTotal_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1          1000      5000       -4000       500% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_MalformedRow_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1      notanumber  10737418240 10200547328      52% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_TotalIsZero_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1              0         0           0       0% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_MountPointIsNotRoot_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sdb1        1000000    500000      500000      50% /data\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_CapacityMissingPercentSign_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1        1000000    500000      500000      50 /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void Parse_CapacityOutOfRange_ReturnsEmpty()
    {
        var df = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                 "/dev/sda1        1000000    500000      500000     150% /\n";

        var result = DiskUsageParser.Parse(df);

        Assert.Null(result.TotalBytes);
    }
}
