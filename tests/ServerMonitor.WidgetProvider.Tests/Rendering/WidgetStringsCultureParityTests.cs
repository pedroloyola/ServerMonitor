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

    // ---- F-1 (REL.0D, Cortex A-lite): the card follows the PROFILE language, like the app -------------------------------

    public static TheoryData<string[], string> Machines() => new()
    {
        { ["en-US", "pt-BR"], "pt-BR" },          // A (the VM): English app → English card
        { ["pt-BR", "en-US"], "en-US" },          // B
        { ["en-GB", "pt-PT"], "pt-PT" },          // C
        { ["fr-FR", "pt-PT"], "pt-PT" },
        { ["fr-FR"], "pt-PT" },
        { [], "pt-BR" },
        { ["de-DE", "pt", "en-US"], "en-US" },
        { ["pt-AO"], "fr-FR" },
        { ["EN-au", "pt-BR"], "pt-BR" },
    };

    [Theory]
    [MemberData(nameof(Machines))]
    public void The_profile_language_matches_the_app(string[] preferred, string display)
    {
        Assert.Equal(SupportedCultures.ProfileLanguage(preferred, display), WidgetStrings.ProfileLanguage(preferred, display));
        Assert.Equal(
            SupportedCultures.Resolve(SupportedCultures.ProfileLanguage(preferred, display)),
            WidgetStrings.ForLanguages(preferred, display).Culture.Name);
    }

    [Theory]
    [InlineData(new[] { "en-US", "pt-BR" }, "pt-BR", "en-US")] // A: not the display's PtBr
    [InlineData(new[] { "pt-BR", "en-US" }, "en-US", "pt-BR")] // B
    [InlineData(new[] { "en-GB", "pt-PT" }, "pt-PT", "en-US")] // C
    public void The_card_follows_the_profile_not_the_display(string[] preferred, string display, string expected)
    {
        Assert.Equal(expected, WidgetStrings.ForLanguages(preferred, display).Culture.Name);
    }

    /// <summary>The provider's live entry point reads the preferred list (not CultureInfo.CurrentUICulture alone).</summary>
    [Fact]
    public void Current_reads_the_preferred_languages()
    {
        var source = File.ReadAllText(Path.Combine(
            Architecture.ProviderBoundaryTests.RepositoryRoot(), "src", "ServerMonitor.WidgetProvider", "Rendering", "WidgetStrings.cs"));

        Assert.Contains(
            "public static WidgetStrings Current() => ForLanguageSource(PreferredLanguages, CultureInfo.CurrentUICulture.Name);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("GlobalizationPreferences.Languages", source, StringComparison.Ordinal);
    }

    /// <summary>PR #58 round 1 (Cortex L-1, Vigil N-1): a failed read renders in the display language, never fails the render.</summary>
    [Fact]
    public void A_failed_read_renders_in_the_display_language()
    {
        foreach (var failure in new Exception[] { new NotSupportedException(), new NotImplementedException(), new ArgumentException() })
        {
            Assert.Equal("pt-BR", WidgetStrings.ForLanguageSource(() => throw failure, "pt-BR").Culture.Name);
            Assert.Equal("en-US", WidgetStrings.ForLanguageSource(() => throw failure, "en-GB").Culture.Name);
        }

        Assert.Equal("en-US", WidgetStrings.ForLanguageSource(() => ["en-US", "pt-BR"], "pt-BR").Culture.Name); // the list wins
    }
}
