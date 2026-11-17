using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class MacLoadAverageParserTests
{
    [Fact]
    public void Parse_TypicalSysctlOutput_ReturnsLoadAndLogicalCpuCount()
    {
        var result = MacLoadAverageParser.Parse("{ 1.83 2.01 2.12 }\n10\n");

        Assert.Equal(new LoadAverage(1.83, 2.01, 2.12), result.Load);
        Assert.Equal(10, result.LogicalCpuCount);
    }

    [Fact]
    public void Parse_CrLfLineEndings_AreAccepted()
    {
        var result = MacLoadAverageParser.Parse("{ 0.10 0.20 0.30 }\r\n8\r\n");

        Assert.Equal(new LoadAverage(0.1, 0.2, 0.3), result.Load);
        Assert.Equal(8, result.LogicalCpuCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n")]
    public void Parse_Missing_ReturnsNothing(string? output)
    {
        var result = MacLoadAverageParser.Parse(output);

        Assert.Null(result.Load);
        Assert.Null(result.LogicalCpuCount);
    }

    [Theory]
    [InlineData("{ 1.83 2.01 2.12 }")]                 // the count line is missing
    [InlineData("{ 1.83 2.01 2.12 }\n10\n12\n")]        // an extra line
    [InlineData("sysctl: unknown oid 'vm.loadavg'\n")]
    public void Parse_UnexpectedShape_ReturnsNothing(string output)
    {
        var result = MacLoadAverageParser.Parse(output);

        Assert.Null(result.Load);
        Assert.Null(result.LogicalCpuCount);
    }

    [Theory]
    [InlineData("1.83 2.01 2.12\n10\n")]               // no braces
    [InlineData("{ 1.83 2.01 }\n10\n")]                // two values
    [InlineData("{ 1.83 -2.01 2.12 }\n10\n")]          // negative
    [InlineData("{ 1,83 2,01 2,12 }\n10\n")]           // locale-formatted
    public void Parse_MalformedLoad_KeepsTheCountButNoLoad(string output)
    {
        var result = MacLoadAverageParser.Parse(output);

        Assert.Null(result.Load);
        Assert.Equal(10, result.LogicalCpuCount);
    }

    [Theory]
    [InlineData("{ 1.83 2.01 2.12 }\n0\n")]
    [InlineData("{ 1.83 2.01 2.12 }\n-4\n")]
    [InlineData("{ 1.83 2.01 2.12 }\nten\n")]
    [InlineData("{ 1.83 2.01 2.12 }\n1234567\n")]       // absurdly long
    public void Parse_MalformedCount_KeepsTheLoadButNoCount(string output)
    {
        var result = MacLoadAverageParser.Parse(output);

        Assert.Equal(new LoadAverage(1.83, 2.01, 2.12), result.Load);
        Assert.Null(result.LogicalCpuCount);
    }
}
