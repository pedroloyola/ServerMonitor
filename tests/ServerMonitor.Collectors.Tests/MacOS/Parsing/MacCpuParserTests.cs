using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class MacCpuParserTests
{
    private const string TwoSampleOutput =
        "Processes: 400 total, 2 running, 398 sleeping\n" +
        "Load Avg: 1.50, 1.60, 1.70\n" +
        "CPU usage: 2.50% user, 3.50% sys, 94.00% idle\n" +
        "PhysMem: 8000M used\n" +
        "Processes: 400 total, 2 running, 398 sleeping\n" +
        "CPU usage: 10.00% user, 5.00% sys, 85.00% idle\n" +
        "PhysMem: 8000M used\n";

    [Fact]
    public void Uses_the_last_sample_and_sums_user_and_sys()
    {
        Assert.Equal(15.0d, MacCpuParser.CalculateUsagePercent(TwoSampleOutput)!.Value, precision: 6);
    }

    [Fact]
    public void Idle_machine_reports_low_usage()
    {
        var output = "CPU usage: 0.50% user, 0.50% sys, 99.00% idle\n";
        Assert.Equal(1.0d, MacCpuParser.CalculateUsagePercent(output)!.Value, precision: 6);
    }

    [Fact]
    public void Fully_idle_is_zero_not_null()
    {
        var output = "CPU usage: 0.00% user, 0.00% sys, 100.00% idle\n";
        var result = MacCpuParser.CalculateUsagePercent(output);
        Assert.NotNull(result);
        Assert.Equal(0.0d, result!.Value, precision: 6);
    }

    [Fact]
    public void Busy_machine_reports_high_usage()
    {
        var output = "CPU usage: 70.00% user, 25.00% sys, 5.00% idle\n";
        Assert.Equal(95.0d, MacCpuParser.CalculateUsagePercent(output)!.Value, precision: 6);
    }

    [Fact]
    public void Sum_above_one_hundred_is_clamped()
    {
        var output = "CPU usage: 80.00% user, 40.00% sys, 0.00% idle\n";
        Assert.Equal(100.0d, MacCpuParser.CalculateUsagePercent(output)!.Value, precision: 6);
    }

    [Fact]
    public void Extra_whitespace_is_tolerated()
    {
        var output = "   CPU usage:   4.00%  user,   6.00%  sys,  90.00% idle   \r\n";
        Assert.Equal(10.0d, MacCpuParser.CalculateUsagePercent(output)!.Value, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Load Avg: 1.0, 2.0, 3.0")]
    [InlineData("CPU usage: this is broken")]
    [InlineData("CPU usage: 12% user")]
    [InlineData("CPU usage: -5.00% user, 3.00% sys, 100.00% idle")]
    [InlineData("CPU usage: NaN% user, 3.00% sys, idle")]
    [InlineData("CPU usage: Infinity% user, 3.00% sys")]
    public void Malformed_or_incomplete_input_is_unknown(string? input)
    {
        Assert.Null(MacCpuParser.CalculateUsagePercent(input));
    }

    [Fact]
    public void Integer_percentages_without_decimals_are_supported()
    {
        var output = "CPU usage: 3% user, 7% sys, 90% idle\n";
        Assert.Equal(10.0d, MacCpuParser.CalculateUsagePercent(output)!.Value, precision: 6);
    }
}
