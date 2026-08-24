using ServerMonitor.Collectors.MacOS.Parsing;

namespace ServerMonitor.Collectors.Tests.MacOS.Parsing;

public sealed class SwVersParserTests
{
    private const string Typical =
        "ProductName:\tmacOS\n" +
        "ProductVersion:\t15.1\n" +
        "BuildVersion:\t24B83\n";

    [Fact]
    public void Parses_typical_output()
    {
        var result = SwVersParser.Parse(Typical);
        Assert.Equal("macOS", result.ProductName);
        Assert.Equal("15.1", result.ProductVersion);
        Assert.Equal("24B83", result.BuildVersion);
    }

    [Fact]
    public void Field_order_does_not_matter()
    {
        var reordered = "BuildVersion: 24B83\nProductVersion: 15.1\nProductName: macOS\n";
        var result = SwVersParser.Parse(reordered);
        Assert.Equal("macOS", result.ProductName);
        Assert.Equal("15.1", result.ProductVersion);
    }

    [Fact]
    public void Missing_field_is_null()
    {
        var result = SwVersParser.Parse("ProductName: macOS\nProductVersion: 15.1\n");
        Assert.Equal("macOS", result.ProductName);
        Assert.Equal("15.1", result.ProductVersion);
        Assert.Null(result.BuildVersion);
    }

    [Fact]
    public void Unknown_lines_are_ignored()
    {
        var withNoise =
            "SomethingElse: value\n" +
            "ProductName: macOS\n" +
            "Copyright: (c) Apple\n" +
            "ProductVersion: 14.6.1\n";
        var result = SwVersParser.Parse(withNoise);
        Assert.Equal("macOS", result.ProductName);
        Assert.Equal("14.6.1", result.ProductVersion);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        var result = SwVersParser.Parse("ProductVersion:      13.6      \r\n");
        Assert.Equal("13.6", result.ProductVersion);
    }

    [Fact]
    public void Control_characters_in_value_are_rejected()
    {
        var result = SwVersParser.Parse("ProductVersion: 15.\n");
        Assert.Null(result.ProductVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no colon here")]
    public void Empty_or_malformed_is_empty(string? input)
    {
        Assert.Equal(SwVersResult.Empty, SwVersParser.Parse(input));
    }
}
