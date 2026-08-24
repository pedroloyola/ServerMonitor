using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class ProcUptimeParserTests
{
    [Fact]
    public void Parse_ValidUptime_ReturnsTimeSpan()
    {
        var result = ProcUptimeParser.Parse("12345.67 98765.43\n");

        Assert.Equal(TimeSpan.FromSeconds(12345.67), result);
    }

    [Fact]
    public void Parse_ZeroUptime_ReturnsZeroNotUnknown()
    {
        var result = ProcUptimeParser.Parse("0.00 0.00\n");

        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void Parse_WithoutIdleField_UsesFirstToken()
    {
        var result = ProcUptimeParser.Parse("42.5\n");

        Assert.Equal(TimeSpan.FromSeconds(42.5), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmptyInput_ReturnsNull(string? uptime)
    {
        Assert.Null(ProcUptimeParser.Parse(uptime));
    }

    [Fact]
    public void Parse_NegativeUptime_ReturnsNull()
    {
        Assert.Null(ProcUptimeParser.Parse("-1.0 0.0\n"));
    }

    [Fact]
    public void Parse_NonNumericValue_ReturnsNull()
    {
        Assert.Null(ProcUptimeParser.Parse("notanumber 0.0\n"));
    }

    [Fact]
    public void Parse_ValueExceedsTimeSpanRange_ReturnsNullInsteadOfThrowing()
    {
        var seconds = double.MaxValue.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        var result = ProcUptimeParser.Parse($"{seconds} 0.0\n");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ValueAtDoubleMax_DoesNotThrow()
    {
        var exception = Record.Exception(() => ProcUptimeParser.Parse("1.7976931348623157E+308 0.0\n"));

        Assert.Null(exception);
    }
}
