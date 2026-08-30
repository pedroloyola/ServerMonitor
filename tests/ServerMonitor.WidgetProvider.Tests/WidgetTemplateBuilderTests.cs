using System.Text.Json;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetTemplateBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static WidgetReadResult AvailableWith(string displayName) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealth.Warning,
            Servers = new[]
            {
                new WidgetServerState
                {
                    Id = Guid.NewGuid(),
                    DisplayName = displayName,
                    Health = WidgetHealth.Warning,
                    CpuUsagePercent = 50,
                    MemoryUsagePercent = 60,
                    DiskUsagePercent = 70,
                    LastUpdatedUtc = Now
                }
            }
        });

    private static void AssertValidAdaptiveCard(string templateJson)
    {
        using var doc = JsonDocument.Parse(templateJson); // throws if invalid
        var root = doc.RootElement;
        Assert.Equal("AdaptiveCard", root.GetProperty("type").GetString());
        Assert.Equal("1.5", root.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("body").ValueKind);
    }

    [Fact]
    public void Available_card_is_valid_json_with_aggregates_and_dev_marker()
    {
        var card = WidgetTemplateBuilder.Build(AvailableWith("Home"), WidgetFreshnessState.Fresh, WidgetSizeHint.Medium);
        AssertValidAdaptiveCard(card.TemplateJson);
        Assert.Contains("ServerAlyzer", card.TemplateJson);
        Assert.Contains("Warning", card.TemplateJson);      // overall health
        Assert.Contains("1 server", card.TemplateJson);     // count
        Assert.Contains(WidgetTemplateBuilder.DevMarker, card.TemplateJson);
        Assert.Equal("{}", card.DataJson);
    }

    [Fact]
    public void Unavailable_card_is_valid_and_neutral()
    {
        var read = WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);
        var card = WidgetTemplateBuilder.Build(read, WidgetFreshnessState.Unavailable, WidgetSizeHint.Small);
        AssertValidAdaptiveCard(card.TemplateJson);
        Assert.Contains("unavailable", card.TemplateJson);
        Assert.Contains(WidgetTemplateBuilder.DevMarker, card.TemplateJson);
    }

    [Fact]
    public void Dev_card_never_leaks_server_display_names()
    {
        // The dev template shows only aggregates; an individual server name must never appear (§19/§9).
        const string secret = "TopSecretServerName";
        var card = WidgetTemplateBuilder.Build(AvailableWith(secret), WidgetFreshnessState.Fresh, WidgetSizeHint.Large);
        Assert.DoesNotContain(secret, card.TemplateJson);
    }
}
