using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class MacDiskUsageParserTests
{
    private const string ApfsRoot =
        "Filesystem     1024-blocks      Used  Available Capacity  Mounted on\n" +
        "/dev/disk3s1s1   971350180  22334040  400000000      52%    /\n";

    [Fact]
    public void Parses_apfs_root_volume_and_multiplies_kilobyte_blocks_to_bytes()
    {
        var result = MacDiskUsageParser.Parse(ApfsRoot);
        Assert.Equal(971350180L * 1024, result.TotalBytes);
        Assert.Equal(22334040L * 1024, result.UsedBytes);
        Assert.Equal(52d, result.UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Uses_capacity_column_verbatim()
    {
        var output =
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
            "/dev/disk1  1000000 900000 100000 91% /\n";
        Assert.Equal(91d, MacDiskUsageParser.Parse(output).UsagePercent!.Value, precision: 6);
    }

    [Fact]
    public void Non_root_mount_is_ignored()
    {
        var output =
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
            "/dev/disk2  1000000 500000 500000 50% /Volumes/External\n";
        Assert.Equal(MacDiskUsageResult.Empty, MacDiskUsageParser.Parse(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n")] // header only
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/disk1 1000 500 500 fifty% /\n")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/disk1 1000 500 500 150% /\n")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/disk1 1000 2000 0 50% /\n")] // used > total
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/disk1 1000 500 500 50 /\n")] // capacity missing %
    public void Malformed_input_is_empty(string? input)
    {
        Assert.Equal(MacDiskUsageResult.Empty, MacDiskUsageParser.Parse(input));
    }

    [Fact]
    public void Large_volumes_are_supported()
    {
        var output =
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n" +
            "/dev/disk1  1000000000 400000000 600000000 40% /\n";
        Assert.Equal(1000000000L * 1024, MacDiskUsageParser.Parse(output).TotalBytes);
    }
}
