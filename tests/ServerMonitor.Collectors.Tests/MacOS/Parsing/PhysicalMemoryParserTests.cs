using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class PhysicalMemoryParserTests
{
    [Theory]
    [InlineData("8589934592\n", 8589934592L)]      // 8 GiB
    [InlineData("17179869184", 17179869184L)]       // 16 GiB
    [InlineData("68719476736\n", 68719476736L)]     // 64 GiB
    [InlineData("   17179869184   ", 17179869184L)] // surrounding whitespace
    public void Parses_valid_physical_memory(string input, long expected)
    {
        Assert.Equal(expected, PhysicalMemoryParser.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-8589934592")]
    [InlineData("16 GB")]
    [InlineData("not-a-number")]
    [InlineData("99999999999999999999999999")] // overflows long
    public void Rejects_invalid_physical_memory(string? input)
    {
        Assert.Null(PhysicalMemoryParser.Parse(input));
    }
}
