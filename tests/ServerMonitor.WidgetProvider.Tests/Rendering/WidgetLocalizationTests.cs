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
        Assert.Equal(expectedHealthy, strings.Healthy);
    }

    [Fact]
    public void PtBr_and_PtPt_differ_where_the_terminology_differs()
    {
        var br = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-BR"));
        var pt = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("pt-PT"));
        // "monitoramento" (pt-BR) vs "monitorização" (pt-PT).
        Assert.Contains("monitoramento", br.NoDataBody);
        Assert.Contains("monitorização", pt.NoDataBody);
    }

    [Fact]
    public void Health_label_maps_all_values()
    {
        var en = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("Healthy", en.HealthLabel(WidgetHealth.Healthy));
        Assert.Equal("Warning", en.HealthLabel(WidgetHealth.Warning));
        Assert.Equal("Critical", en.HealthLabel(WidgetHealth.Critical));
        Assert.Equal("Offline", en.HealthLabel(WidgetHealth.Offline));
        Assert.Equal("Unknown", en.HealthLabel(WidgetHealth.Unknown));
        Assert.Equal("Unknown", en.HealthLabel((WidgetHealth)99));
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
                    Id = Guid.NewGuid(), DisplayName = "Home", Health = WidgetHealth.Warning,
                    CpuUsagePercent = 50, MemoryUsagePercent = 60, DiskUsagePercent = 70, LastUpdatedUtc = Now
                }
            }
        });

        var vm = WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, strings);
        var json = WidgetCardRenderer.Render(vm).TemplateJson;

        Assert.Contains("Alerta", json);            // localized "Warning"
        Assert.Contains("Atualizado há 3 min", json); // localized freshness
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
            Assert.Equal("Healthy", en.Healthy);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
