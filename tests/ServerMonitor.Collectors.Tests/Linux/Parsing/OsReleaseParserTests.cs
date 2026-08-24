using ServerMonitor.Collectors.Linux.Parsing;

namespace ServerMonitor.Collectors.Tests.Linux.Parsing;

public sealed class OsReleaseParserTests
{
    [Fact]
    public void Parse_DropsDisplayValueLongerThanLimit()
    {
        var result = OsReleaseParser.Parse($"NAME={new string('A', 257)}\nVERSION_ID=13\n");

        Assert.Null(result.Name);
        Assert.Equal("13", result.Version);
    }

    [Fact]
    public void Parse_DropsDisplayValueContainingControlCharacter()
    {
        var result = OsReleaseParser.Parse("NAME=Debian" + '\u0001' + "Server\nVERSION_ID=13\n");

        Assert.Null(result.Name);
        Assert.Equal("13", result.Version);
    }

    [Fact]
    public void Parse_UbuntuStyleFile_ExtractsNameAndVersionId()
    {
        var osRelease = """
            NAME="Ubuntu"
            VERSION="22.04.3 LTS (Jammy Jellyfish)"
            ID=ubuntu
            VERSION_ID="22.04"
            PRETTY_NAME="Ubuntu 22.04.3 LTS"
            """;

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Ubuntu", result.Name);
        Assert.Equal("22.04", result.Version);
    }

    [Fact]
    public void Parse_MissingVersionId_FallsBackToVersion()
    {
        var osRelease = "NAME=\"Debian GNU/Linux\"\nVERSION=\"12 (bookworm)\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Debian GNU/Linux", result.Name);
        Assert.Equal("12 (bookworm)", result.Version);
    }

    [Fact]
    public void Parse_MissingName_FallsBackToPrettyName()
    {
        var osRelease = "PRETTY_NAME=\"Alpine Linux v3.19\"\nVERSION_ID=3.19\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Alpine Linux v3.19", result.Name);
        Assert.Equal("3.19", result.Version);
    }

    [Fact]
    public void Parse_UnquotedValues_AreReadAsIs()
    {
        var osRelease = "NAME=Ubuntu\nVERSION_ID=22.04\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Ubuntu", result.Name);
        Assert.Equal("22.04", result.Version);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var osRelease = "# comment\n\nNAME=\"Ubuntu\"\n\nVERSION_ID=\"22.04\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Ubuntu", result.Name);
        Assert.Equal("22.04", result.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmptyInput_ReturnsEmpty(string? osRelease)
    {
        var result = OsReleaseParser.Parse(osRelease);

        Assert.Null(result.Name);
        Assert.Null(result.Version);
    }

    [Fact]
    public void Parse_NoRecognizedKeys_ReturnsEmpty()
    {
        var result = OsReleaseParser.Parse("SOME_OTHER_FIELD=value\n");

        Assert.Null(result.Name);
        Assert.Null(result.Version);
    }

    [Fact]
    public void Parse_UnopenedTrailingQuote_IsRejectedForThatKey()
    {
        var osRelease = "NAME=Ubuntu\"\nVERSION_ID=\"22.04\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        // NAME's stray closing quote makes it invalid; no fallback exists.
        Assert.Null(result.Name);
        Assert.Equal("22.04", result.Version);
    }

    [Fact]
    public void Parse_UnclosedLeadingQuote_IsRejectedForThatKey()
    {
        var osRelease = "NAME=\"Ubuntu\nVERSION_ID=\"22.04\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Null(result.Name);
        Assert.Equal("22.04", result.Version);
    }

    [Fact]
    public void Parse_InvalidNamePrimaryKey_StillFallsBackToPrettyName()
    {
        var osRelease = "NAME=Ubuntu\"\nPRETTY_NAME=\"Ubuntu 22.04.3 LTS\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Ubuntu 22.04.3 LTS", result.Name);
    }

    [Fact]
    public void Parse_EmptyQuotedValue_IsTreatedAsUnset()
    {
        var osRelease = "NAME=\"\"\nPRETTY_NAME=\"Alpine Linux v3.19\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Alpine Linux v3.19", result.Name);
    }

    [Fact]
    public void Parse_KeysOutsideAllowlist_AreNeverStoredEvenIfWellFormed()
    {
        var osRelease = "NAME=\"Ubuntu\"\nID=\"ubuntu\"\nID_LIKE=\"debian\"\nHOME_URL=\"https://ubuntu.com\"\n";

        var result = OsReleaseParser.Parse(osRelease);

        Assert.Equal("Ubuntu", result.Name);
        // No VERSION/VERSION_ID present, and ID/ID_LIKE/HOME_URL must not
        // leak into the result through the allowlisted fields.
        Assert.Null(result.Version);
    }
}
