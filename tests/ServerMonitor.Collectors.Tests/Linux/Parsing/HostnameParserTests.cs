using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class HostnameParserTests
{
    [Fact]
    public void Parse_SingleLine_ReturnsTrimmedHostname()
    {
        Assert.Equal("web-01", HostnameParser.Parse("web-01\n"));
    }

    [Fact]
    public void Parse_WithCarriageReturn_TrimsIt()
    {
        Assert.Equal("web-01", HostnameParser.Parse("web-01\r\n"));
    }

    [Fact]
    public void Parse_MultipleLines_ReturnsNull()
    {
        Assert.Null(HostnameParser.Parse("web-01\nextra-garbage\n"));
    }

    [Fact]
    public void Parse_EmbeddedCarriageReturnMidString_ReturnsNull()
    {
        Assert.Null(HostnameParser.Parse("web\r01\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void Parse_NullOrEmptyInput_ReturnsNull(string? hostname)
    {
        Assert.Null(HostnameParser.Parse(hostname));
    }

    [Fact]
    public void Parse_ContainsControlCharacter_ReturnsNull()
    {
        Assert.Null(HostnameParser.Parse("web\t01\n"));
    }

    [Fact]
    public void Parse_ExactlyMaxLength_IsAccepted()
    {
        var hostname = new string('a', 255);

        Assert.Equal(hostname, HostnameParser.Parse(hostname + "\n"));
    }

    [Fact]
    public void Parse_ExceedsMaxLength_ReturnsNull()
    {
        var hostname = new string('a', 256);

        Assert.Null(HostnameParser.Parse(hostname + "\n"));
    }
}
