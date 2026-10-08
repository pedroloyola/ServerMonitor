using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

public sealed class WidgetLocalizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("en-US", "Healthy")]
    [InlineData("pt-BR", "Saudável")]
    [InlineData("pt-PT", "Saudável")]
    [InlineData("fr-FR", "Healthy")] // unsupported → English default
    public void Culture_resolves_to_supported_or_default(string culture, string expectedHealthy)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        Assert.Equal(expectedHealthy, strings.StatusHealthy);
    }

    [Fact]
    public void PtBr_and_PtPt_differ_where_the_terminology_differs()
    {
        var br = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-BR"));
        var pt = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-PT"));
        // "monitoramento"/"conexão" (pt-BR) vs "monitorização"/"ligação" (pt-PT).
        Assert.Contains("monitoramento", br.UnavailableTitle);
        Assert.Contains("monitorização", pt.UnavailableTitle);
        Assert.Equal("Sem conexão", br.StatusOffline);
        Assert.Equal("Sem ligação", pt.StatusOffline);
    }

    [Fact]
    public void Each_culture_carries_its_own_number_culture()
    {
        Assert.Equal("en-US", WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-GB")).Culture.Name);
        Assert.Equal("pt-PT", WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-PT")).Culture.Name);
        Assert.Equal("pt-PT", WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-AO")).Culture.Name);
        Assert.Equal("pt-BR", WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-BR")).Culture.Name);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    public void Rendered_card_carries_localized_text(string culture)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var read = WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now.AddMinutes(-3),
            OverallHealth = WidgetHealth.Warning,
            Servers = new[]
            {
                new WidgetServerState
                {
                    Id = new Guid("00000000-0000-0000-0000-000000000001"), DisplayName = "Home", Health = WidgetHealth.Warning,
                    CpuUsagePercent = 50, MemoryUsagePercent = 60, DiskUsagePercent = 70,
                    // A 300 s server (staleAfter 600 s) keeps a 3-minute-old snapshot fresh (D-UI9-4).
                    LastUpdatedUtc = Now.AddMinutes(-3), StaleAfterSeconds = 600
                }
            }
        });

        var vm = WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, strings);
        var card = WidgetCardRenderer.Render(vm);
        var texts = CardTemplateHarness.VisibleTexts(CardTemplateHarness.Expand(card.TemplateJson, card.DataJson));

        Assert.Contains("Atenção", texts);              // localized "Warning" status
        Assert.Contains("Atualizado há 3 min", texts);  // localized freshness
        Assert.Contains("RAM", texts);
        Assert.Contains("Disco", texts);
    }

    [Fact]
    public void No_culture_leakage_between_tests()
    {
        // Building with an explicit strings instance must not depend on the ambient thread culture.
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        try
        {
            var en = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));
            Assert.Equal("Healthy", en.StatusHealthy);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
