using ServerMonitor.Core.Domain;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class SupportedCulturesTests
{
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("PT-br")]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    public void Resolve_ReturnsSupportedCulture(string culture)
    {
        Assert.Equal(culture, SupportedCultures.Resolve(culture), ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    public void Resolve_FallsBackToBrazilianPortuguese(string? culture)
    {
        Assert.Equal("pt-BR", SupportedCultures.Resolve(culture));
    }
}
