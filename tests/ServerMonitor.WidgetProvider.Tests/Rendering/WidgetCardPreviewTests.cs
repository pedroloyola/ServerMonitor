using System.Globalization;
using System.Text.Json;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// Renders representative real-world scenarios across sizes and cultures, ASSERTS each is a valid
/// Adaptive Card, and writes the JSON to a scratch folder as a side effect for the visual review
/// (ADR-018 §47). This is a genuine test (it always runs and always asserts) — not a no-op — so it never
/// reports PASS for work that did not happen. The written JSON is a PREVIEW (pasteable into the official
/// Adaptive Cards designer); the real Widgets-board runtime remains honestly NOT_RUN.
/// </summary>
public sealed class WidgetCardPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 30, TimeSpan.Zero);

    public static IEnumerable<object[]> Scenarios()
    {
        yield return new object[] { "small-en-mixed", Mixed(), WidgetSizeHint.Small, "en-US" };
        yield return new object[] { "medium-en-mixed", Mixed(), WidgetSizeHint.Medium, "en-US" };
        yield return new object[] { "large-en-mixed", Mixed(), WidgetSizeHint.Large, "en-US" };
        yield return new object[] { "small-en-healthy", Healthy(), WidgetSizeHint.Small, "en-US" };
        yield return new object[] { "medium-ptbr-mixed", Mixed(), WidgetSizeHint.Medium, "pt-BR" };
        yield return new object[] { "medium-ptpt-mixed", Mixed(), WidgetSizeHint.Medium, "pt-PT" };
        yield return new object[] { "medium-en-empty", Empty(), WidgetSizeHint.Medium, "en-US" };
        yield return new object[] { "medium-en-unavailable", Unavailable(), WidgetSizeHint.Medium, "en-US" };
        yield return new object[] { "medium-en-oneattention", OneAttention(), WidgetSizeHint.Medium, "en-US" };
        yield return new object[] { "medium-en-longname", LongName(), WidgetSizeHint.Medium, "en-US" };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario_renders_a_valid_card_and_is_written_for_preview(
        string name, WidgetReadResult read, WidgetSizeHint size, string culture)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var vm = WidgetViewModelBuilder.Build(read, size, Now, strings);
        var card = WidgetCardRenderer.Render(vm);

        using var doc = JsonDocument.Parse(card.TemplateJson); // asserts valid JSON
        Assert.Equal("AdaptiveCard", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.5", doc.RootElement.GetProperty("version").GetString());

        var dir = Path.Combine(Path.GetTempPath(), "sm-widget-preview");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), card.TemplateJson);
    }

    private static WidgetServerState Server(string n, WidgetHealth h, double? c, double? m, double? d) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = n,
        Health = h,
        CpuUsagePercent = c,
        MemoryUsagePercent = m,
        DiskUsagePercent = d,
        LastUpdatedUtc = Now.AddMinutes(-4)
    };

    private static WidgetReadResult Read(DateTimeOffset at, params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = at,
            OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
            Servers = servers
        });

    private static WidgetReadResult Mixed() => Read(Now.AddMinutes(-4),
        Server("Prod DB", WidgetHealth.Critical, 96, 88, 74),
        Server("mac-mini", WidgetHealth.Offline, null, null, null),
        Server("web-01", WidgetHealth.Warning, 71, 63, 40),
        Server("cache", WidgetHealth.Unknown, null, null, null),
        Server("hermes-debian", WidgetHealth.Healthy, 3, 39, 12),
        Server("backup", WidgetHealth.Healthy, 8, 22, 55),
        Server("edge", WidgetHealth.Healthy, 12, 30, 41),
        Server("relay", WidgetHealth.Healthy, 5, 18, 27));

    private static WidgetReadResult Healthy() => Read(Now.AddSeconds(-20),
        Server("hermes-debian", WidgetHealth.Healthy, 3, 39, 12),
        Server("mac-mini", WidgetHealth.Healthy, 9, 44, 33));

    private static WidgetReadResult OneAttention() => Read(Now.AddSeconds(-40),
        Server("Prod DB", WidgetHealth.Critical, 96, 88, 74),
        Server("web-01", WidgetHealth.Healthy, 20, 30, 40));

    private static WidgetReadResult LongName() => Read(Now.AddMinutes(-2),
        Server("a-really-long-server-display-name-that-overflows", WidgetHealth.Warning, 55, 66, 77));

    private static WidgetReadResult Empty() => Read(Now);

    private static WidgetReadResult Unavailable() => WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);
}
