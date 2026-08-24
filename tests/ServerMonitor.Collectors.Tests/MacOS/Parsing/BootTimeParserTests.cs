using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class BootTimeParserTests
{
    [Fact]
    public void Parses_typical_boottime_and_ignores_localized_text()
    {
        var output = "{ sec = 1720000000, usec = 0 } Wed Jul  3 12:26:40 2024\n";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1720000000), BootTimeParser.Parse(output));
    }

    [Fact]
    public void Ignores_extra_whitespace()
    {
        var output = "{   sec   =   1720000000 ,  usec = 500 }";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1720000000), BootTimeParser.Parse(output));
    }

    [Fact]
    public void Does_not_match_the_sec_inside_usec_when_usec_comes_first()
    {
        var output = "{ usec = 0, sec = 1720000000 }";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1720000000), BootTimeParser.Parse(output));
    }

    [Fact]
    public void A_far_future_boot_time_is_still_parsed_to_that_instant()
    {
        // The parser only reads the field; the collector decides whether the
        // resulting uptime is plausible (negative uptime becomes unknown there).
        var output = "{ sec = 4102444800, usec = 0 }"; // year 2100
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), BootTimeParser.Parse(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ usec = 0 } no seconds here")]
    [InlineData("{ sec = notanumber, usec = 0 }")]
    [InlineData("{ sec = -5, usec = 0 }")]
    [InlineData("{ sec = 0, usec = 0 }")]
    [InlineData("{ sec = 99999999999999999999, usec = 0 }")] // overflow
    public void Malformed_or_out_of_range_is_unknown(string? input)
    {
        Assert.Null(BootTimeParser.Parse(input));
    }
}
