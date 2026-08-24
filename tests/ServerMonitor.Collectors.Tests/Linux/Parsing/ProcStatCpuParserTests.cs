using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class ProcStatCpuParserTests
{
    [Fact]
    public void CalculateUsagePercent_WithBusyDelta_ReturnsExpectedPercentage()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var first = "cpu  100 0 0 800 0 0 0 0 0 0\n" +
                     "cpu0 100 0 0 800 0 0 0 0 0 0\n";
        var second = "cpu  150 0 0 850 0 0 0 0 0 0\n" +
                      "cpu0 150 0 0 850 0 0 0 0 0 0\n";

        // total delta = 100, idle delta = 50 => busy delta 50 => 50%.
        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(50d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_FullyIdle_ReturnsZeroNotUnknown()
    {
        var first = "cpu  100 0 0 800 0 0 0 0 0 0\n";
        var second = "cpu  100 0 0 900 0 0 0 0 0 0\n";

        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(0d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_FullyBusy_ReturnsHundred()
    {
        var first = "cpu  100 0 0 800 0 0 0 0 0 0\n";
        var second = "cpu  200 0 0 800 0 0 0 0 0 0\n";

        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(100d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_HandlesShortLegacyFieldSet()
    {
        // Older kernels: user nice system idle only, no iowait/irq/softirq/steal.
        var first = "cpu  100 0 0 800\n";
        var second = "cpu  150 0 0 850\n";

        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(50d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_IncludesIowaitInIdle()
    {
        var first = "cpu  100 0 0 700 100 0 0 0 0 0\n";
        var second = "cpu  150 0 0 750 100 0 0 0 0 0\n";

        // total delta = 100, idle delta (idle+iowait) = 50 => 50% busy.
        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(50d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_IgnoresGuestFieldsInTotal()
    {
        // guest/guest_nice (indices 8/9) must not be double counted into total,
        // since they are already folded into user/nice by the kernel.
        var withGuest = "cpu  100 0 0 800 0 0 0 0 500 0\n";
        var withGuestLater = "cpu  150 0 0 850 0 0 0 0 500 0\n";

        var result = ProcStatCpuParser.CalculateUsagePercent(withGuest, withGuestLater);

        Assert.NotNull(result);
        Assert.Equal(50d, result!.Value, precision: 6);
    }

    [Fact]
    public void CalculateUsagePercent_SkipsPerCoreLineAndUsesAggregate()
    {
        var first = "cpu0 999 999 999 999 999 999 999 999\n" +
                     "cpu  100 0 0 800 0 0 0 0\n";
        var second = "cpu0 999 999 999 999 999 999 999 999\n" +
                      "cpu  150 0 0 850 0 0 0 0\n";

        var result = ProcStatCpuParser.CalculateUsagePercent(first, second);

        Assert.NotNull(result);
        Assert.Equal(50d, result!.Value, precision: 6);
    }

    [Theory]
    [InlineData(null, "cpu  1 1 1 1")]
    [InlineData("cpu  1 1 1 1", null)]
    [InlineData(null, null)]
    [InlineData("", "cpu  1 1 1 1")]
    public void CalculateUsagePercent_MissingSample_ReturnsNull(string? first, string? second)
    {
        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_NoCpuLine_ReturnsNull()
    {
        var first = "intr 12345\n";
        var second = "intr 12400\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_MalformedNumbers_ReturnsNull()
    {
        var first = "cpu  100 0 0 800\n";
        var second = "cpu  abc 0 0 850\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_NegativeNumbers_ReturnsNull()
    {
        var first = "cpu  100 0 0 800\n";
        var second = "cpu  -5 0 0 850\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_TooFewFields_ReturnsNull()
    {
        var first = "cpu  100 0\n";
        var second = "cpu  150 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_NoDeltaBetweenSamples_ReturnsNull()
    {
        var sample = "cpu  100 0 0 800 0 0 0 0 0 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(sample, sample));
    }

    [Fact]
    public void CalculateUsagePercent_CountersGoBackwards_ReturnsNull()
    {
        var first = "cpu  200 0 0 900 0 0 0 0 0 0\n";
        var second = "cpu  100 0 0 800 0 0 0 0 0 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_IdleCounterGoesBackwards_ReturnsNull()
    {
        // total delta is positive (100 -> 300 user) but idle regresses
        // (900 -> 800): the unsigned idle delta underflows.
        var first = "cpu  100 0 0 900 0 0 0 0 0 0\n";
        var second = "cpu  300 0 0 800 0 0 0 0 0 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_FieldSumOverflowsUlong_ReturnsNull()
    {
        // steal = ulong.MaxValue; summing any other positive field overflows.
        var first = "cpu  10 10 10 10 10 10 10 18446744073709551615 0 0\n";
        var second = "cpu  20 20 20 20 20 20 20 18446744073709551615 0 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }

    [Fact]
    public void CalculateUsagePercent_ValueExceedsUlongRange_ReturnsNull()
    {
        // 18446744073709551616 == ulong.MaxValue + 1, cannot be parsed at all.
        var first = "cpu  18446744073709551616 0 0 800 0 0 0 0 0 0\n";
        var second = "cpu  150 0 0 850 0 0 0 0 0 0\n";

        Assert.Null(ProcStatCpuParser.CalculateUsagePercent(first, second));
    }
}
