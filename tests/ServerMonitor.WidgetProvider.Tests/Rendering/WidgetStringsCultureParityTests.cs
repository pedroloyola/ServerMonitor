using System.Globalization;
using ServerMonitor.Core.Domain;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// REL.0C: the widget card picks its language with the same table as the app (SupportedCultures.Resolve, linked
/// into this test project as source): pt-BR → pt-BR, other Portuguese → pt-PT, everything else → en-US.
/// Residual (documented): the card follows the Windows culture, not the app's in-app language choice.
/// </summary>
public sealed class WidgetStringsCultureParityTests
{
    public static TheoryData<string> Cultures()
    {
        var data = new TheoryData<string> { "", "en-US", "en-GB", "en-IE", "en-AU", "en", "pt-PT", "pt-AO", "pt-MZ", "pt-BR", "pt", "fr-FR", "de-DE", "de", "es-BR" };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures)
                     .Where(c => c.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) || c.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                     .Select(c => c.Name)
                     .Where(name => name is not ("en-US" or "en-GB" or "en-IE" or "en-AU" or "en" or "pt-PT" or "pt-AO" or "pt-MZ" or "pt-BR" or "pt")))
        {
            data.Add(culture);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void The_card_language_matches_the_app_language(string culture)
    {
        Assert.Equal(SupportedCultures.Resolve(culture), WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)).Culture.Name);
    }

    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("", "en-US")]
    public void The_card_follows_the_rel0c_table(string culture, string expected)
    {
        Assert.Equal(expected, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)).Culture.Name);
    }
}
