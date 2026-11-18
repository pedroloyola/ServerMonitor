using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class ProcLoadAvgParserTests
{
    [Fact]
    public void Parse_TypicalProcLoadAvg_ReturnsTheThreeAverages()
    {
        var load = ProcLoadAvgParser.Parse("0.52 0.40 0.31 1/123 4567\n");

        Assert.Equal(new LoadAverage(0.52, 0.40, 0.31), load);
    }

    [Fact]
    public void Parse_BusyBoxAlpine_ReturnsTheThreeAverages()
    {
        // BusyBox's cat prints the kernel file unchanged; Alpine hosts often run idle at 0.00.
        var load = ProcLoadAvgParser.Parse("0.00 0.01 0.05 2/87 1234");

        Assert.Equal(new LoadAverage(0.00, 0.01, 0.05), load);
    }

    [Fact]
    public void Parse_IdleHost_ZeroIsAReading_NotUnknown()
    {
        Assert.Equal(new LoadAverage(0, 0, 0), ProcLoadAvgParser.Parse("0.00 0.00 0.00 1/50 99\n"));
    }

    [Fact]
    public void Parse_BusyManyCoreHost_HasNoUpperBound()
    {
        Assert.Equal(new LoadAverage(128.75, 96.10, 64.00), ProcLoadAvgParser.Parse("128.75 96.10 64.00 130/2048 99999\n"));
    }

    [Fact]
    public void Parse_OnlyTheThreeAverages_IsEnough()
    {
        Assert.Equal(new LoadAverage(1.5, 1.25, 1), ProcLoadAvgParser.Parse("1.50 1.25 1.00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Parse_Missing_ReturnsNull(string? output)
    {
        Assert.Null(ProcLoadAvgParser.Parse(output));
    }

    [Theory]
    [InlineData("0.52 0.40")]                        // short
    [InlineData("0.52 abc 0.31 1/123 4567")]          // a non-number
    [InlineData("-0.52 0.40 0.31 1/123 4567")]        // negative
    [InlineData("0,52 0,40 0,31 1/123 4567")]         // locale-formatted, never accepted
    [InlineData("NaN 0.40 0.31 1/123 4567")]
    [InlineData("Infinity 0.40 0.31 1/123 4567")]
    [InlineData("1e3 0.40 0.31 1/123 4567")]          // exponent form is not what the kernel prints
    [InlineData("cat: /proc/loadavg: No such file or directory")]
    public void Parse_Malformed_ReturnsNull(string output)
    {
        Assert.Null(ProcLoadAvgParser.Parse(output));
    }

    [Fact]
    public void Parse_OverlongToken_ReturnsNull()
    {
        Assert.Null(ProcLoadAvgParser.Parse(new string('9', 64) + " 0.40 0.31 1/123 4567"));
    }

    [Theory]
    [InlineData("1234567.8901 0.40 0.31 1/123 4567", true)]    // 12 characters: the longest accepted
    [InlineData("12345678.9012 0.40 0.31 1/123 4567", false)]  // 13 characters
    [InlineData("0000000000000000000000000000001 0.40 0.31 1/123 4567", false)] // a 31-digit run
    public void Parse_TokenLength_IsCappedAtTwelveCharacters(string output, bool accepted)
    {
        Assert.Equal(accepted, ProcLoadAvgParser.Parse(output) is not null);
    }

    [Fact]
    public void Parse_OnlyTheFirstLineIsRead()
    {
        Assert.Equal(new LoadAverage(0.1, 0.2, 0.3), ProcLoadAvgParser.Parse("0.10 0.20 0.30 1/1 1\n9.99 9.99 9.99 1/1 1\n"));
    }
}
