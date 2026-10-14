using ServerMonitor.Core.Domain;

namespace ServerMonitor.Core.Tests.Domain;

/// <summary>
/// REL.0C: en-US is the default/base language and any Windows culture resolves by BASE language
/// (pt-BR → pt-BR, other Portuguese → pt-PT, everything else → en-US). A persisted choice is never overwritten.
/// </summary>
public sealed class SupportedCulturesTests
{
    [Fact]
    public void TheDefaultIsAmericanEnglish_AndTheThreeLanguagesAreSupported()
    {
        Assert.Equal("en-US", SupportedCultures.Default);
        Assert.Equal(["en-US", "pt-PT", "pt-BR"], SupportedCultures.All);
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("en-IE", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("PT-br", "pt-BR")]
    [InlineData("pt_BR", "pt-BR")]
    [InlineData("pt-Latn-BR", "pt-BR")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("de", "en-US")]
    [InlineData("es-BR", "en-US")] // Spanish in Brazil is not Portuguese
    [InlineData("pts", "en-US")] // not the "pt" language subtag
    [InlineData("", "en-US")] // invariant culture
    [InlineData(null, "en-US")]
    public void Resolve_MatchesByBaseLanguage(string? culture, string expected)
    {
        Assert.Equal(expected, SupportedCultures.Resolve(culture));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void StartupOverride_WritesNothing_ForAnExactlySupportedSystemCulture(string systemCulture)
    {
        Assert.Null(SupportedCultures.StartupOverride(persistedOverride: null, systemCulture));
    }

    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("de", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void StartupOverride_FirstRun_ResolvesAnUnsupportedSystemCulture(string? systemCulture, string expected)
    {
        Assert.Equal(expected, SupportedCultures.StartupOverride(persistedOverride: null, systemCulture));
        Assert.Equal(expected, SupportedCultures.StartupOverride(persistedOverride: "  ", systemCulture));
    }

    [Theory]
    [InlineData("pt-BR", "fr-FR")] // the old pt-BR default persisted on an unsupported system: kept, not migrated
    [InlineData("pt-BR", "en-GB")]
    [InlineData("pt-PT", "en-US")]
    [InlineData("en-US", "pt-BR")]
    [InlineData("pt-BR", "pt-PT")]
    public void StartupOverride_NeverOverwritesAPersistedChoice(string persisted, string systemCulture)
    {
        Assert.Null(SupportedCultures.StartupOverride(persisted, systemCulture));
    }
}
